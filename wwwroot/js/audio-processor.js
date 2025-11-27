/**
 * AudioWorklet Processor for capturing microphone audio in chunks
 * Buffers audio samples and sends them in configurable chunk sizes
 */

class AudioProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();

        // Get chunk duration from options (default 300ms)
        this.chunkDuration = options.processorOptions?.chunkDuration || 300;

        // Calculate chunk size in samples (sampleRate is available in AudioWorkletProcessor)
        // sampleRate is typically 16000 Hz for our use case
        this.chunkSize = Math.floor((sampleRate * this.chunkDuration) / 1000);

        // Buffer to accumulate samples
        this.buffer = [];

        console.log(`AudioProcessor initialized: chunkDuration=${this.chunkDuration}ms, chunkSize=${this.chunkSize} samples, sampleRate=${sampleRate}Hz`);
    }

    process(inputs, outputs, parameters) {
        // Get the first input (microphone)
        const input = inputs[0];

        // If we have input data
        if (input && input.length > 0) {
            // Get the first channel (mono)
            const channel = input[0];

            // Add samples to buffer
            for (let i = 0; i < channel.length; i++) {
                this.buffer.push(channel[i]);
            }

            // If we've accumulated enough samples, send them
            if (this.buffer.length >= this.chunkSize) {
                // Extract chunk
                const chunk = this.buffer.slice(0, this.chunkSize);
                this.buffer = this.buffer.slice(this.chunkSize);

                // Send chunk to main thread
                this.port.postMessage({
                    audioData: new Float32Array(chunk)
                });
            }
        }

        // Return true to keep processor alive
        return true;
    }
}

// Register the processor
registerProcessor('audio-processor', AudioProcessor);
