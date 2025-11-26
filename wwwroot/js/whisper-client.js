/**
 * WhisperRecognition - A Web Speech API-like interface for Whisper transcription
 *
 * Usage:
 *   const recognizer = new WhisperRecognition();
 *   recognizer.onstart = () => console.log('Started');
 *   recognizer.onresult = (event) => console.log('Result:', event.results);
 *   recognizer.onend = () => console.log('Ended');
 *   recognizer.onerror = (event) => console.error('Error:', event.error);
 *   recognizer.start();
 */

class WhisperRecognition {
    constructor(options = {}) {
        this.continuous = options.continuous !== undefined ? options.continuous : true;
        this.interimResults = options.interimResults !== undefined ? options.interimResults : true;
        this.lang = options.lang || 'auto';
        this.model = options.model || 'ggml-small.bin';
        this.chunkDuration = options.chunkDuration || 300; // Reduced from 500ms to 300ms for faster response

        this.wsUrl = options.wsUrl || this._getWebSocketUrl();
        this.ws = null;
        this.audioContext = null;
        this.mediaStream = null;
        this.processor = null;
        this.isRecording = false;
        this.audioChunks = [];

        this.onstart = null;
        this.onend = null;
        this.onresult = null;
        this.onerror = null;
        this.onaudiostart = null;
        this.onaudioend = null;
        this.onsoundstart = null;
        this.onsoundend = null;
        this.onnomatch = null;
    }

    _getWebSocketUrl() {
        const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        return `${protocol}//${window.location.host}/ws-transcribe`;
    }

    async start() {
        if (this.isRecording) {
            this._emitError('already-started', 'Recognition is already started');
            return;
        }

        try {
            await this._initializeWebSocket();
            await this._waitForSessionStart();
            await this._initializeAudio();
            this.isRecording = true;

            if (this.onstart) {
                this.onstart();
            }
        } catch (error) {
            this._emitError('audio-capture', error.message);
        }
    }

    stop() {
        if (!this.isRecording) {
            return;
        }

        this.isRecording = false;
        this._stopAudioCapture();
        this._sendControlMessage('stop');

        if (this.onend) {
            this.onend();
        }
    }

    abort() {
        this.stop();
        if (this.ws) {
            this.ws.close();
            this.ws = null;
        }
    }

    async _initializeWebSocket() {
        return new Promise((resolve, reject) => {
            this.ws = new WebSocket(this.wsUrl);

            this.ws.onopen = () => {
                resolve();
            };

            this.ws.onmessage = (event) => {
                this._handleWebSocketMessage(event.data);
            };

            this.ws.onerror = (error) => {
                reject(new Error('WebSocket connection failed'));
            };

            this.ws.onclose = () => {
                if (this.isRecording) {
                    this._emitError('network', 'WebSocket connection closed unexpectedly');
                    this.stop();
                }
            };
        });
    }

    async _waitForSessionStart() {
        return new Promise((resolve, reject) => {
            const timeout = setTimeout(() => {
                reject(new Error('Session start timeout'));
            }, 5000);

            const originalOnMessage = this.ws.onmessage;
            this.ws.onmessage = (event) => {
                try {
                    const message = JSON.parse(event.data);
                    if (message.type === 'started') {
                        clearTimeout(timeout);
                        this.ws.onmessage = originalOnMessage;
                        resolve();
                    } else if (message.type === 'error') {
                        clearTimeout(timeout);
                        this.ws.onmessage = originalOnMessage;
                        reject(new Error(message.error));
                    } else {
                        originalOnMessage(event);
                    }
                } catch (error) {
                    originalOnMessage(event);
                }
            };

            // Ensure WebSocket is ready before sending
            if (this.ws.readyState === WebSocket.OPEN) {
                this._sendControlMessage('start');
            } else {
                clearTimeout(timeout);
                reject(new Error('WebSocket not ready'));
            }
        });
    }

    async _initializeAudio() {
        try {
            this.mediaStream = await navigator.mediaDevices.getUserMedia({
                audio: {
                    channelCount: 1,
                    sampleRate: 16000,
                    echoCancellation: true,
                    noiseSuppression: true
                }
            });

            this.audioContext = new (window.AudioContext || window.webkitAudioContext)({
                sampleRate: 16000
            });

            const source = this.audioContext.createMediaStreamSource(this.mediaStream);

            await this.audioContext.audioWorklet.addModule(this._getProcessorCode());
            this.processor = new AudioWorkletNode(this.audioContext, 'audio-chunk-processor', {
                processorOptions: {
                    chunkDuration: this.chunkDuration
                }
            });

            this.processor.port.onmessage = (event) => {
                if (event.data.type === 'audio-chunk') {
                    this._sendAudioChunk(event.data.chunk);
                }
            };

            source.connect(this.processor);
            this.processor.connect(this.audioContext.destination);

            if (this.onaudiostart) {
                this.onaudiostart();
            }
        } catch (error) {
            throw new Error(`Failed to initialize audio: ${error.message}`);
        }
    }

    _getProcessorCode() {
        const processorCode = `
            class AudioChunkProcessor extends AudioWorkletProcessor {
                constructor(options) {
                    super();
                    this.chunkDuration = options.processorOptions?.chunkDuration || 500;
                    this.sampleRate = 16000;
                    this.samplesPerChunk = Math.floor(this.sampleRate * this.chunkDuration / 1000);
                    this.buffer = [];
                }

                process(inputs, outputs, parameters) {
                    const input = inputs[0];
                    if (input.length > 0) {
                        const channelData = input[0];
                        this.buffer.push(...channelData);

                        if (this.buffer.length >= this.samplesPerChunk) {
                            const chunk = this.buffer.slice(0, this.samplesPerChunk);
                            this.buffer = this.buffer.slice(this.samplesPerChunk);

                            this.port.postMessage({
                                type: 'audio-chunk',
                                chunk: chunk
                            });
                        }
                    }
                    return true;
                }
            }

            registerProcessor('audio-chunk-processor', AudioChunkProcessor);
        `;

        const blob = new Blob([processorCode], { type: 'application/javascript' });
        return URL.createObjectURL(blob);
    }

    _sendControlMessage(command) {
        if (this.ws && this.ws.readyState === WebSocket.OPEN) {
            this.ws.send(JSON.stringify({
                command: command,
                modelName: this.model,
                language: this.lang
            }));
        }
    }

    _sendAudioChunk(audioSamples) {
        if (!this.ws || this.ws.readyState !== WebSocket.OPEN || !this.isRecording) {
            return;
        }

        const wavBuffer = this._createWavBuffer(audioSamples);
        this.ws.send(wavBuffer);
    }

    _createWavBuffer(audioSamples) {
        const sampleRate = 16000;
        const numChannels = 1;
        const bitsPerSample = 16;
        const bytesPerSample = bitsPerSample / 8;
        const blockAlign = numChannels * bytesPerSample;
        const byteRate = sampleRate * blockAlign;
        const dataSize = audioSamples.length * bytesPerSample;
        const bufferSize = 44 + dataSize;

        const buffer = new ArrayBuffer(bufferSize);
        const view = new DataView(buffer);

        const writeString = (offset, string) => {
            for (let i = 0; i < string.length; i++) {
                view.setUint8(offset + i, string.charCodeAt(i));
            }
        };

        writeString(0, 'RIFF');
        view.setUint32(4, bufferSize - 8, true);
        writeString(8, 'WAVE');

        writeString(12, 'fmt ');
        view.setUint32(16, 16, true);
        view.setUint16(20, 1, true);
        view.setUint16(22, numChannels, true);
        view.setUint32(24, sampleRate, true);
        view.setUint32(28, byteRate, true);
        view.setUint16(32, blockAlign, true);
        view.setUint16(34, bitsPerSample, true);

        writeString(36, 'data');
        view.setUint32(40, dataSize, true);

        let offset = 44;
        for (let i = 0; i < audioSamples.length; i++) {
            const sample = Math.max(-1, Math.min(1, audioSamples[i]));
            const int16 = sample < 0 ? sample * 0x8000 : sample * 0x7FFF;
            view.setInt16(offset, int16, true);
            offset += 2;
        }

        return buffer;
    }

    _handleWebSocketMessage(data) {
        try {
            const message = JSON.parse(data);

            switch (message.type) {
                case 'started':
                    console.log('Transcription session started:', message.model);
                    break;

                case 'result':
                    if (this.onresult) {
                        const event = {
                            results: [{
                                isFinal: message.isFinal,
                                transcript: message.text,
                                confidence: 1.0
                            }],
                            resultIndex: 0
                        };
                        this.onresult(event);
                    }
                    break;

                case 'stopped':
                    console.log('Transcription session stopped');
                    break;

                case 'error':
                    this._emitError('service-error', message.error);
                    break;

                default:
                    console.warn('Unknown message type:', message.type);
            }
        } catch (error) {
            console.error('Failed to parse WebSocket message:', error);
        }
    }

    _stopAudioCapture() {
        if (this.processor) {
            this.processor.disconnect();
            this.processor = null;
        }

        if (this.audioContext) {
            this.audioContext.close();
            this.audioContext = null;
        }

        if (this.mediaStream) {
            this.mediaStream.getTracks().forEach(track => track.stop());
            this.mediaStream = null;
        }

        if (this.onaudioend) {
            this.onaudioend();
        }
    }

    _emitError(error, message) {
        if (this.onerror) {
            this.onerror({
                error: error,
                message: message
            });
        }
    }
}

if (typeof module !== 'undefined' && module.exports) {
    module.exports = WhisperRecognition;
}
