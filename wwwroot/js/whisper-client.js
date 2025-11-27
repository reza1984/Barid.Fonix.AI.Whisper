/**
 * WhisperRecognition - A Web Speech API-like interface for Whisper transcription
 * Supports both WebSocket and SignalR transports with automatic detection
 *
 * Usage:
 *   // Using SignalR (default - with automatic reconnection)
 *   const recognizer = new WhisperRecognition();
 *
 *   // Using WebSocket (auto-detected from URL)
 *   const recognizer = new WhisperRecognition({ url: 'ws://localhost:7000/ws-transcribe' });
 *
 *   // Using SignalR with custom hub URL
 *   const recognizer = new WhisperRecognition({ url: '/my-hub' });
 *
 *   // Explicit transport override
 *   const recognizer = new WhisperRecognition({ transport: 'websocket' });
 *
 *   recognizer.onstart = () => console.log('Started');
 *   recognizer.onresult = (event) => console.log('Result:', event.results);
 *   recognizer.onend = () => console.log('Ended');
 *   recognizer.onerror = (event) => console.error('Error:', event.error);
 *   recognizer.start();
 */

class WhisperRecognition {
    constructor(options = {}) {
        // Auto-detect transport from URL or use explicit transport
        const url = options.url || options.wsUrl; // Support legacy wsUrl

        if (options.transport) {
            // Explicit transport override
            this.transport = options.transport;
        } else if (url && (url.startsWith('ws:') || url.startsWith('wss:'))) {
            // Auto-detect WebSocket from URL protocol
            this.transport = 'websocket';
        } else {
            // Default to SignalR
            this.transport = 'signalr';
        }

        // Common properties
        this.continuous = options.continuous !== undefined ? options.continuous : true;
        this.interimResults = options.interimResults !== undefined ? options.interimResults : true;
        this.lang = options.lang || 'auto';
        this.model = options.model || 'ggml-small.bin';
        this.chunkDuration = options.chunkDuration || 300;
        this.sessionId = options.sessionId || null; // For multi-user collaboration

        // Events
        this.onstart = null;
        this.onresult = null;
        this.onerror = null;
        this.onend = null;

        // Transport-specific properties
        if (this.transport === 'signalr') {
            this.hubUrl = url || '/transcription-hub';
            this.connection = null;
        } else {
            this.wsUrl = url || this._getDefaultWebSocketUrl();
            this.ws = null;
            this.sessionStarted = false;
        }

        // Audio capture (shared)
        this.audioContext = null;
        this.audioWorklet = null;
        this.stream = null;
        this._isRecording = false;

        console.log(`WhisperRecognition initialized with ${this.transport} transport (URL: ${this.transport === 'signalr' ? this.hubUrl : this.wsUrl})`);
    }

    _getDefaultWebSocketUrl() {
        const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        return `${protocol}//${window.location.host}/ws-transcribe`;
    }

    // ========== SignalR Implementation ==========

    async _initializeSignalRConnection() {
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(this.hubUrl)
            .withAutomaticReconnect({
                nextRetryDelayInMilliseconds: (retryContext) => {
                    if (retryContext.elapsedMilliseconds < 60000) {
                        return Math.min(1000 * Math.pow(2, retryContext.previousRetryCount), 60000);
                    } else {
                        return null;
                    }
                }
            })
            .configureLogging(signalR.LogLevel.Information)
            .build();

        this.connection.on('SessionStarted', (data) => {
            console.log('SignalR: Session started:', data);
            if (this.onstart) this.onstart();
        });

        this.connection.on('TranscriptionResult', (data) => {
            if (this.onresult) {
                const event = {
                    results: [{
                        transcript: data.text,
                        confidence: 1.0,
                        isFinal: data.isFinal
                    }],
                    resultIndex: 0,
                    segments: data.segments
                };
                this.onresult(event);
            }
        });

        this.connection.on('SessionStopped', (data) => {
            console.log('SignalR: Session stopped:', data);
            if (this.onend) this.onend();
        });

        this.connection.on('Error', (error) => {
            console.error('SignalR: Server error:', error);
            if (this.onerror) {
                this.onerror({ error: 'service-error', message: error });
            }
        });

        this.connection.onreconnecting((error) => {
            console.warn('SignalR: Connection lost. Reconnecting...', error);
            if (this.onerror) {
                this.onerror({
                    error: 'network',
                    message: 'Connection lost. Reconnecting...',
                    isReconnecting: true
                });
            }
        });

        this.connection.onreconnected((connectionId) => {
            console.log('SignalR: Reconnected:', connectionId);
            if (this._isRecording) {
                this._restartSignalRSession();
            }
        });

        this.connection.onclose((error) => {
            console.error('SignalR: Connection closed:', error);
            if (this.onerror) {
                this.onerror({ error: 'network', message: 'Connection closed' });
            }
            if (this.onend) this.onend();
        });

        try {
            await this.connection.start();
            console.log('SignalR: Connected');
        } catch (err) {
            console.error('SignalR: Failed to connect:', err);
            throw err;
        }
    }

    async _restartSignalRSession() {
        try {
            await this.connection.invoke('StartSession', this.model, this.lang, this.sessionId);
        } catch (err) {
            console.error('SignalR: Failed to restart session:', err);
            if (this.onerror) {
                this.onerror({ error: 'service-error', message: 'Failed to restart session' });
            }
        }
    }

    async _sendAudioChunkSignalR(float32Samples) {
        if (!this.connection || this.connection.state !== signalR.HubConnectionState.Connected) {
            console.warn('SignalR: Not connected, skipping chunk');
            return;
        }

        try {
            const wavBuffer = this._createWavBuffer(float32Samples);
            const uint8Array = new Uint8Array(wavBuffer);

            // Convert to base64 string for SignalR JSON protocol
            const base64 = this._arrayBufferToBase64(uint8Array);
            await this.connection.invoke('SendAudioChunk', base64);
        } catch (err) {
            console.error('SignalR: Failed to send audio chunk:', err);
        }
    }

    async _stopSignalR() {
        if (this.connection && this.connection.state === signalR.HubConnectionState.Connected) {
            try {
                await this.connection.invoke('StopSession');
            } catch (err) {
                console.error('SignalR: Failed to stop session:', err);
            }
        }
    }

    async _disconnectSignalR() {
        if (this.connection) {
            await this.connection.stop();
            this.connection = null;
        }
    }

    // ========== WebSocket Implementation ==========

    async _initializeWebSocket() {
        return new Promise((resolve, reject) => {
            this.ws = new WebSocket(this.wsUrl);

            this.ws.onopen = () => {
                console.log('WebSocket: Connected');
                resolve();
            };

            this.ws.onmessage = (event) => {
                this._handleWebSocketMessage(event.data);
            };

            this.ws.onerror = (error) => {
                console.error('WebSocket: Connection error:', error);
                reject(new Error('WebSocket connection failed'));
            };

            this.ws.onclose = () => {
                console.log('WebSocket: Connection closed');
                if (this.onend) this.onend();
            };
        });
    }

    _handleWebSocketMessage(data) {
        try {
            const message = JSON.parse(data);

            if (message.type === 'started') {
                console.log('WebSocket: Session started');
                this.sessionStarted = true;
                if (this.onstart) this.onstart();
            } else if (message.type === 'result') {
                if (this.onresult) {
                    const event = {
                        results: [{
                            transcript: message.text,
                            confidence: 1.0,
                            isFinal: !message.isPartial
                        }],
                        resultIndex: 0,
                        segments: message.segments
                    };
                    this.onresult(event);
                }
            } else if (message.type === 'stopped') {
                console.log('WebSocket: Session stopped');
                if (this.onend) this.onend();
            } else if (message.type === 'error') {
                console.error('WebSocket: Server error:', message.error);
                if (this.onerror) {
                    this.onerror({ error: 'service-error', message: message.error });
                }
            }
        } catch (err) {
            console.error('WebSocket: Failed to parse message:', err);
        }
    }

    _sendWebSocketControlMessage(command, data = {}) {
        if (this.ws && this.ws.readyState === WebSocket.OPEN) {
            const message = { command, ...data };
            this.ws.send(JSON.stringify(message));
        }
    }

    async _waitForWebSocketSessionStart() {
        return new Promise((resolve, reject) => {
            const timeout = setTimeout(() => {
                reject(new Error('Session start timeout'));
            }, 5000);

            const checkSession = () => {
                if (this.sessionStarted) {
                    clearTimeout(timeout);
                    resolve();
                } else {
                    setTimeout(checkSession, 100);
                }
            };
            checkSession();
        });
    }

    async _sendAudioChunkWebSocket(float32Samples) {
        if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
            console.warn('WebSocket: Not connected, skipping chunk');
            return;
        }

        try {
            const wavBuffer = this._createWavBuffer(float32Samples);
            this.ws.send(wavBuffer);
        } catch (err) {
            console.error('WebSocket: Failed to send audio chunk:', err);
        }
    }

    _stopWebSocket() {
        this._sendWebSocketControlMessage('stop');
    }

    _disconnectWebSocket() {
        if (this.ws) {
            this.ws.close();
            this.ws = null;
        }
        this.sessionStarted = false;
    }

    // ========== Common Methods ==========

    async start() {
        if (this._isRecording) {
            console.warn('Already recording');
            return;
        }

        try {
            if (this.transport === 'signalr') {
                // SignalR flow
                if (!this.connection || this.connection.state === signalR.HubConnectionState.Disconnected) {
                    await this._initializeSignalRConnection();
                }
                await this.connection.invoke('StartSession', this.model, this.lang, this.sessionId);
            } else {
                // WebSocket flow
                await this._initializeWebSocket();
                this._sendWebSocketControlMessage('start', {
                    modelName: this.model,
                    language: this.lang
                });
                await this._waitForWebSocketSessionStart();
            }

            // Initialize audio capture (common)
            await this._startAudioCapture();
            this._isRecording = true;

        } catch (err) {
            console.error('Failed to start:', err);
            if (this.onerror) {
                this.onerror({ error: 'audio-capture', message: err.message });
            }
        }
    }

    async _startAudioCapture() {
        this.stream = await navigator.mediaDevices.getUserMedia({
            audio: {
                channelCount: 1,
                sampleRate: 16000,
                echoCancellation: true,
                noiseSuppression: true,
                autoGainControl: true
            }
        });

        this.audioContext = new AudioContext({ sampleRate: 16000 });
        const source = this.audioContext.createMediaStreamSource(this.stream);

        await this.audioContext.audioWorklet.addModule('/js/audio-processor.js');
        this.audioWorklet = new AudioWorkletNode(this.audioContext, 'audio-processor', {
            processorOptions: { chunkDuration: this.chunkDuration }
        });

        this.audioWorklet.port.onmessage = async (event) => {
            const { audioData } = event.data;
            await this._sendAudioChunk(audioData);
        };

        source.connect(this.audioWorklet);
        this.audioWorklet.connect(this.audioContext.destination);
    }

    async _sendAudioChunk(float32Samples) {
        if (this.transport === 'signalr') {
            await this._sendAudioChunkSignalR(float32Samples);
        } else {
            await this._sendAudioChunkWebSocket(float32Samples);
        }
    }

    _createWavBuffer(float32Samples) {
        const sampleRate = 16000;
        const numChannels = 1;
        const bitsPerSample = 16;

        const int16Samples = new Int16Array(float32Samples.length);
        for (let i = 0; i < float32Samples.length; i++) {
            const sample = Math.max(-1, Math.min(1, float32Samples[i]));
            int16Samples[i] = sample < 0 ? sample * 0x8000 : sample * 0x7FFF;
        }

        const dataSize = int16Samples.length * 2;
        const buffer = new ArrayBuffer(44 + dataSize);
        const view = new DataView(buffer);

        this._writeString(view, 0, 'RIFF');
        view.setUint32(4, 36 + dataSize, true);
        this._writeString(view, 8, 'WAVE');
        this._writeString(view, 12, 'fmt ');
        view.setUint32(16, 16, true);
        view.setUint16(20, 1, true);
        view.setUint16(22, numChannels, true);
        view.setUint32(24, sampleRate, true);
        view.setUint32(28, sampleRate * numChannels * bitsPerSample / 8, true);
        view.setUint16(32, numChannels * bitsPerSample / 8, true);
        view.setUint16(34, bitsPerSample, true);
        this._writeString(view, 36, 'data');
        view.setUint32(40, dataSize, true);

        const samples = new Int16Array(buffer, 44);
        samples.set(int16Samples);

        return buffer;
    }

    _writeString(view, offset, string) {
        for (let i = 0; i < string.length; i++) {
            view.setUint8(offset + i, string.charCodeAt(i));
        }
    }

    _arrayBufferToBase64(uint8Array) {
        let binary = '';
        const len = uint8Array.byteLength;
        for (let i = 0; i < len; i++) {
            binary += String.fromCharCode(uint8Array[i]);
        }
        return btoa(binary);
    }

    async stop() {
        if (!this._isRecording) return;

        this._isRecording = false;

        // Stop audio capture
        if (this.audioWorklet) {
            this.audioWorklet.disconnect();
            this.audioWorklet = null;
        }
        if (this.stream) {
            this.stream.getTracks().forEach(track => track.stop());
            this.stream = null;
        }
        if (this.audioContext) {
            await this.audioContext.close();
            this.audioContext = null;
        }

        // Stop session (transport-specific)
        if (this.transport === 'signalr') {
            await this._stopSignalR();
        } else {
            this._stopWebSocket();
        }
    }

    async disconnect() {
        await this.stop();

        if (this.transport === 'signalr') {
            await this._disconnectSignalR();
        } else {
            this._disconnectWebSocket();
        }
    }

    // Alias for compatibility
    abort() {
        this.disconnect();
    }
}
