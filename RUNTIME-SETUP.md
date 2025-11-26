# Whisper.NET Runtime Setup Guide

This application supports multiple Whisper.NET runtime backends for optimal performance on different hardware configurations.

## Available Runtimes

### 1. **CPU Runtime** (Default)
- **Package**: `Whisper.net.Runtime`
- **Platforms**: Windows, macOS, Linux
- **Use case**: Universal compatibility, no GPU required
- **Performance**: Slowest, but works everywhere

### 2. **CoreML Runtime** (macOS Apple Silicon)
- **Package**: `Whisper.net.Runtime.CoreML`
- **Platforms**: macOS with M1/M2/M3/M4 chips
- **Use case**: Best performance on Apple Silicon Macs
- **Performance**: **~10-20x faster than CPU**
- **Requirements**: macOS 12.0+ with Apple Silicon

### 3. **CUDA Runtime** (NVIDIA GPUs)
- **Package**: `Whisper.net.Runtime.Cuda`
- **Platforms**: Windows, Linux with NVIDIA GPUs
- **Use case**: Best for systems with NVIDIA graphics cards
- **Performance**: **~15-30x faster than CPU**
- **Requirements**:
  - NVIDIA GPU with CUDA support
  - CUDA Toolkit 11.8+ installed
  - cuDNN library

### 4. **Vulkan Runtime** (Cross-platform GPU)
- **Package**: `Whisper.net.Runtime.Vulkan`
- **Platforms**: Windows, Linux, macOS
- **Use case**: GPU acceleration for AMD/Intel/other GPUs
- **Performance**: **~5-15x faster than CPU**
- **Requirements**: Vulkan-compatible GPU and drivers

## How to Select a Runtime

### Method 1: Environment Variable (Recommended)

Set the `WhisperRuntime` environment variable before building:

```bash
# For macOS Apple Silicon (M1/M2/M3)
export WhisperRuntime=coreml
dotnet build

# For NVIDIA CUDA
export WhisperRuntime=cuda
dotnet build

# For Vulkan
export WhisperRuntime=vulkan
dotnet build

# For CPU (default)
export WhisperRuntime=cpu
dotnet build
# OR just:
dotnet build
```

**Windows (PowerShell)**:
```powershell
$env:WhisperRuntime="coreml"
dotnet build
```

**Windows (CMD)**:
```cmd
set WhisperRuntime=coreml
dotnet build
```

### Method 2: Build Command

Pass the runtime as a property during build:

```bash
# macOS Apple Silicon
dotnet build -p:WhisperRuntime=coreml

# NVIDIA CUDA
dotnet build -p:WhisperRuntime=cuda

# Vulkan
dotnet build -p:WhisperRuntime=vulkan

# CPU (default)
dotnet build
```

### Method 3: Edit .csproj File

You can directly modify the `Barid.Fonix.AI.Whisper.csproj` file to hardcode your runtime choice.

Currently, the conditional package references are:
```xml
<!-- Default: CPU -->
<ItemGroup Condition="'$(WhisperRuntime)' == '' OR '$(WhisperRuntime)' == 'cpu'">
  <PackageReference Include="Whisper.net.Runtime" Version="1.9.0" />
</ItemGroup>

<!-- macOS Apple Silicon -->
<ItemGroup Condition="'$(WhisperRuntime)' == 'coreml'">
  <PackageReference Include="Whisper.net.Runtime.CoreML" Version="1.9.0" />
</ItemGroup>

<!-- NVIDIA CUDA -->
<ItemGroup Condition="'$(WhisperRuntime)' == 'cuda'">
  <PackageReference Include="Whisper.net.Runtime.Cuda" Version="1.9.0" />
</ItemGroup>

<!-- Vulkan -->
<ItemGroup Condition="'$(WhisperRuntime)' == 'vulkan'">
  <PackageReference Include="Whisper.net.Runtime.Vulkan" Version="1.9.0" />
</ItemGroup>
```

## Automatic Detection

When the application starts, it will automatically detect your system capabilities and recommend the best runtime:

```
=== System Information ===
OS: macOS 14.0
Architecture: Arm64
Framework: .NET 10.0.0

=== Recommended Runtime ===
Runtime Type: CoreML
Package: Whisper.net.Runtime.CoreML
==========================
```

**Note**: The detection is informational only. You still need to build with the correct runtime package.

## Installation Examples

### macOS Apple Silicon (M1/M2/M3)

```bash
# Clean previous builds
dotnet clean

# Build with CoreML runtime
export WhisperRuntime=coreml
dotnet build

# Run
dotnet run
```

Expected performance: **Real-time transcription with <100ms latency**

### Windows/Linux with NVIDIA GPU

```bash
# Install CUDA Toolkit first
# Download from: https://developer.nvidia.com/cuda-downloads

# Build with CUDA runtime
export WhisperRuntime=cuda
dotnet build

# Run
dotnet run
```

Expected performance: **Real-time transcription with ~50ms latency**

### Any System with Vulkan GPU

```bash
# Ensure Vulkan drivers are installed
# Most modern GPUs support Vulkan

# Build with Vulkan runtime
export WhisperRuntime=vulkan
dotnet build

# Run
dotnet run
```

Expected performance: **Real-time transcription with ~150ms latency**

## Troubleshooting

### "Package not found" Error
Make sure you've set the environment variable before building:
```bash
export WhisperRuntime=coreml
dotnet restore
dotnet build
```

### Runtime Library Not Found
- **CoreML**: Ensure you're on macOS 12.0+ with Apple Silicon
- **CUDA**: Install CUDA Toolkit and cuDNN
- **Vulkan**: Install Vulkan drivers for your GPU

### Performance Issues
If transcription is slow:
1. Check the startup logs to see which runtime is recommended
2. Rebuild with the recommended runtime
3. Ensure you have the latest GPU drivers installed

## Performance Comparison

Model: `ggml-base.bin` (74MB)

| Runtime | Hardware | Speed | Latency |
|---------|----------|-------|---------|
| CPU | Intel i7 | 1.0x | ~2000ms |
| CoreML | Apple M2 | **20x** | **~100ms** |
| CUDA | RTX 3080 | **25x** | **~80ms** |
| Vulkan | RTX 3060 | **12x** | **~150ms** |

*Speeds are approximate and depend on specific hardware*

## Recommended Setup by Platform

| Platform | Recommended Runtime | Command |
|----------|-------------------|---------|
| macOS Apple Silicon | CoreML | `export WhisperRuntime=coreml` |
| Windows with NVIDIA | CUDA | `$env:WhisperRuntime="cuda"` |
| Linux with NVIDIA | CUDA | `export WhisperRuntime=cuda` |
| Windows with AMD/Intel GPU | Vulkan | `$env:WhisperRuntime="vulkan"` |
| Linux with AMD GPU | Vulkan | `export WhisperRuntime=vulkan"` |
| Any other system | CPU | *(no action needed)* |

## Next Steps

1. Check the console output when you start the app to see the recommended runtime
2. Clean and rebuild with your chosen runtime
3. Test the transcription performance
4. Adjust the model size in `appsettings.json` if needed

For more information, visit: https://github.com/sandrohanea/whisper.net
