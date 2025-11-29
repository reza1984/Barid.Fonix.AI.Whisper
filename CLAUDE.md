# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

1. First think through the problem, read the codebase for relevant files, and write a plan to tasks/todo.md.
2. The plan should have a list of todo items that you can check off as you complete them
3. Before you begin working, check in with me and I will verify the plan.
4. Then, begin working on the todo items, marking them as complete as you go.
5. Please every step of the way just give me a high level explanation of what changes you made
6. Make every task and code change you do as simple as possible. We want to avoid making any massive or complex changes.Every change should impact as little code as possible. Everything is about simplicity.
7. Finally, add a review section to the todo.md file with a summary of the changes you made and any other relevant information.
8. DO NOT BE LAZY. NEVER BE LAZY.IF THERE IS A BUG FIND THE ROOT CAUSE AND FIX IT. NO TEMPORARY FIXES.YOU ARE ASENIOR DEVELOPER. NEVER BE LAZY
9. MAKE ALL FIXES AND CODE CHANGES AS SIMPLE AS HUMANLY POSSIBLE. THEY SHOULD ONLY IMPACT NECESSARY CODE RELEVANT TO THE TASK AND NOTHING ELSE. IT SHOULD IMPACT AS LITTLE CODE AS POSSIBLE. YOUR GOAL IS TO NOT INTRODUCE ANY BUGS.
IT'S ALL ABOUT SIMPLICITY

## Project Overview

Real-time speech-to-text transcription service using OpenAI Whisper, built with ASP.NET Core 10.0. Supports dual transport modes (SignalR and WebSocket) for live audio streaming with automatic reconnection, hardware-accelerated inference, and browser-based audio capture.

## Build Commands

### Standard Build
```bash
dotnet build
dotnet run
```

### Development with Hot Reload
```bash
dotnet watch run
```

### Runtime Selection
The application supports multiple Whisper.NET runtime backends for hardware acceleration. Set the `WhisperRuntime` environment variable before building:

```bash
# macOS Apple Silicon (20x faster - recommended for M1/M2/M3/M4)
export WhisperRuntime=coreml
dotnet build

# NVIDIA CUDA (25x faster - Windows/Linux with NVIDIA GPU)
export WhisperRuntime=cuda
dotnet build

# Vulkan (12x faster - AMD/Intel GPUs)
export WhisperRuntime=vulkan
dotnet build

# CPU (default - cross-platform, slowest)
dotnet build
```

See [RUNTIME-SETUP.md](RUNTIME-SETUP.md) for detailed runtime configuration instructions.

## Architecture

### Dual Transport System

The application supports **two transport modes simultaneously** with intelligent auto-detection:

1. **SignalR** (default, recommended): `/transcription-hub`
   - Automatic reconnection with exponential backoff (2s → 10s → 30s → 60s max)
   - Transport fallback (WebSocket → Server-Sent Events → Long Polling)
   - Groups API for future multi-user collaboration
   - Base64 encoding for binary data over JSON protocol

2. **WebSocket** (legacy): `/ws-transcribe`
   - Maintained for backward compatibility
   - Lower latency but no automatic reconnection
   - Direct binary transfer

**Auto-detection**: Client library (`whisper-client.js`) automatically selects transport based on URL:
- URLs starting with `ws://` or `wss://` → WebSocket transport
- All other URLs → SignalR transport

### Core Components

#### Backend Services (Services/)

**WhisperService**: Singleton service managing Whisper model lifecycle
- Downloads and caches models in `ai-models/` directory
- Creates fresh `WhisperFactory` per session (factories cannot be reused)
- Enforces concurrent session limits via semaphore (default: 5 concurrent sessions)
- Model downloads on-demand from Hugging Face if not present locally

**TranscriptionSession**: Per-connection transcription state
- Owns both `WhisperProcessor` and `WhisperFactory` for proper disposal
- 3-second rolling audio buffer with 125ms overlap for context
- Voice Activity Detection (VAD) with 0.02 threshold to skip silence
- Cumulative transcript tracking with deduplication

**AudioUtils**: WAV format validation and conversion
- Validates 16kHz, mono, 16-bit PCM WAV headers
- Converts WAV bytes to Float32 samples for Whisper

#### Transport Handlers

**TranscriptionHub** (Hubs/TranscriptionHub.cs): SignalR hub
- Session management using `Context.ConnectionId` as key
- Thread-safe `ConcurrentDictionary<string, TranscriptionSession>` for active sessions
- **Critical**: Accepts `string audioDataBase64` not `byte[]` - SignalR JSON protocol requires base64 encoding
- Groups API for collaborative sessions (future feature)
- Automatic cleanup on disconnect via `OnDisconnectedAsync`

**WebSocketTranscriptionHandler** (Handlers/): Legacy WebSocket handler
- Stateful connection with manual session lifecycle
- Direct binary transfer without encoding overhead
- No reconnection support

#### Frontend (wwwroot/)

**whisper-client.js**: Web Speech API-compatible client library
- Unified interface for both transports
- Web Audio API + AudioWorklet for 300ms audio chunks at 16kHz
- Base64 encoding for SignalR binary data transmission
- Event structure: `event.results[0].transcript` and `event.results[0].isFinal`

**audio-processor.js**: AudioWorklet processor
- Captures microphone audio in configurable chunks (default 300ms)
- Runs in audio thread for low-latency capture
- Posts Float32Array chunks to main thread

**index.html**: Demo UI
- Transport selector (SignalR vs WebSocket)
- Live transcript display with interim/final result handling
- Model and language selection

### Session Lifecycle

1. **Client connects** → Hub/WebSocket handler accepts connection
2. **StartSession called** with model name and language
3. **WhisperService creates session**:
   - Creates fresh `WhisperFactory` from model file
   - Builds `WhisperProcessor` with language settings
   - Returns `TranscriptionSession` owning both factory and processor
4. **Audio chunks stream** (300ms @ 16kHz = ~4800 samples/chunk):
   - SignalR: Base64 encoded string → decoded to byte[] → Float32 samples
   - WebSocket: Binary WAV → Float32 samples
5. **TranscriptionSession processes**:
   - Accumulates chunks in rolling 3-second buffer
   - Runs Whisper inference when buffer ≥ 1 second
   - Returns incremental results (partial) and final segments
6. **Disconnect** → Session disposed, factory released, semaphore freed

### Critical Implementation Details

**WhisperFactory Lifecycle**:
- Each `WhisperFactory` can only create ONE processor instance
- **Must create fresh factory per session** - do not cache or reuse
- Factory must be disposed alongside processor to free native resources

**SignalR Binary Data**:
- SignalR JSON protocol cannot serialize byte arrays
- Client converts audio to base64 string before sending
- Server decodes base64 to byte[] in hub method
- Method signature: `Task SendAudioChunk(string audioDataBase64)`

**Event Structure Compatibility**:
- UI expects Web Speech API format: `event.results[0].transcript`
- Client library provides flat array: `results: [{ transcript, isFinal }]`
- Not nested: `results: [[{ ... }]]` (this breaks UI)

**Hot Reload Limitations**:
- Method signature changes cause TypeLoadException
- Requires full restart with `dotnet watch run` or manual `dotnet run`

## Configuration (appsettings.json)

- `WhisperModelsPath`: Model storage directory (default: `ai-models/`)
- `DefaultWhisperModel`: Model loaded at startup (default: `ggml-small.bin`)
- `MaxConcurrentTranscriptionSessions`: Concurrent session limit (default: 5)

Available models in this repo:
- `ggml-base.bin` (74MB) - Balanced speed/accuracy
- `ggml-small.bin` (488MB) - Better accuracy, slower
- `ggml-medium.bin` (1.5GB) - High accuracy, slowest

## API Endpoints

### SignalR Hub (`/transcription-hub`)
**Methods**:
- `StartSession(string modelName, string language, string? sessionId)`
- `SendAudioChunk(string audioDataBase64)` - Base64 encoded WAV data
- `StopSession()`

**Events**:
- `SessionStarted` - Session initialized
- `TranscriptionResult` - Transcription result with `{ text, isFinal, segments }`
- `Error` - Error message
- `SessionStopped` - Session ended

### WebSocket (`/ws-transcribe`)
Binary protocol - send WAV chunks, receive JSON results.

### REST API
- `GET /api/transcribe/models` - List available models
- `POST /api/transcribe/file` - Upload file for transcription

## Common Development Patterns

### Adding New Whisper Model Support
1. Download model to `ai-models/` directory (must match `ggml-*.bin` pattern)
2. Model auto-detected and available via API
3. Update `DefaultWhisperModel` in appsettings.json if needed

### Modifying Audio Processing
- Audio chunk size: Change `chunkDuration` in WhisperRecognition constructor
- Buffer size: Modify `MaxBufferSamples` in TranscriptionSession.cs
- VAD threshold: Adjust `VadThreshold` for sensitivity

### Debugging Transport Issues
- SignalR: Check `options.MaximumReceiveMessageSize` (currently 1MB limit)
- WebSocket: Check `KeepAliveInterval` in Program.cs
- Browser console shows transport selection: "WhisperRecognition initialized with {transport} transport"

### Performance Optimization
1. Select appropriate runtime backend for hardware (see Build Commands)
2. Adjust `MaxConcurrentTranscriptionSessions` based on hardware
3. Use smaller models (base < small < medium) for faster inference
4. Monitor startup logs for runtime detection recommendations

## Testing

Access demo UI at `http://localhost:7000/index.html` after running the application.

Test both transports:
- SignalR (default): Select "SignalR (Auto-Reconnect)" in dropdown
- WebSocket: Select "WebSocket (Legacy)" in dropdown

Test reconnection: Stop/restart server while recording to verify SignalR auto-reconnect.
