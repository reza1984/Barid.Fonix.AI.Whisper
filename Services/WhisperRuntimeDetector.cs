using System.Runtime.InteropServices;

namespace Barid.Fonix.AI.Whisper.Services;

public enum WhisperRuntimeType
{
    Cpu,
    CoreML,
    Cuda,
    Vulkan
}

public class WhisperRuntimeDetector
{
    private readonly ILogger<WhisperRuntimeDetector> _logger;

    public WhisperRuntimeDetector(ILogger<WhisperRuntimeDetector> logger)
    {
        _logger = logger;
    }

    public WhisperRuntimeType DetectBestRuntime()
    {
        _logger.LogInformation("Detecting best Whisper runtime...");

        // Check for macOS Apple Silicon (CoreML)
        if (IsMacOSAppleSilicon())
        {
            _logger.LogInformation("Detected macOS with Apple Silicon - CoreML runtime recommended");
            return WhisperRuntimeType.CoreML;
        }

        // Check for macOS Intel (no CoreML, suggest CPU or Vulkan)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (HasVulkanSupport())
            {
                _logger.LogInformation("Detected macOS Intel with Vulkan - Vulkan runtime recommended");
                return WhisperRuntimeType.Vulkan;
            }
            else
            {
                _logger.LogInformation("Detected macOS Intel - CPU runtime recommended (CoreML requires Apple Silicon)");
                return WhisperRuntimeType.Cpu;
            }
        }

        // Check for NVIDIA CUDA
        if (HasNvidiaCuda())
        {
            _logger.LogInformation("Detected NVIDIA CUDA - Cuda runtime recommended");
            return WhisperRuntimeType.Cuda;
        }

        // Check for Vulkan support
        if (HasVulkanSupport())
        {
            _logger.LogInformation("Detected Vulkan support - Vulkan runtime recommended");
            return WhisperRuntimeType.Vulkan;
        }

        // Default to CPU
        _logger.LogInformation("No GPU acceleration detected - using CPU runtime");
        return WhisperRuntimeType.Cpu;
    }

    private static bool IsMacOSAppleSilicon()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return false;

        try
        {
            // Check if running on ARM64 (Apple Silicon)
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        }
        catch
        {
            return false;
        }
    }

    private bool HasNvidiaCuda()
    {
        try
        {
            // Check for NVIDIA SMI (nvidia-smi command)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return CheckCommandExists("nvidia-smi.exe");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return CheckCommandExists("nvidia-smi");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking for CUDA support");
        }

        return false;
    }

    private bool HasVulkanSupport()
    {
        try
        {
            // Check for Vulkan loader
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vulkan-1.dll"));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return File.Exists("/usr/lib/x86_64-linux-gnu/libvulkan.so.1") ||
                       File.Exists("/usr/lib64/libvulkan.so.1");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return File.Exists("/usr/local/lib/libvulkan.dylib") ||
                       File.Exists("/opt/homebrew/lib/libvulkan.dylib");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking for Vulkan support");
        }

        return false;
    }

    private bool CheckCommandExists(string command)
    {
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                    Arguments = command,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit(1000);

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public string GetRuntimePackageName(WhisperRuntimeType runtimeType)
    {
        return runtimeType switch
        {
            WhisperRuntimeType.Cpu => "Whisper.net.Runtime",
            WhisperRuntimeType.CoreML => "Whisper.net.Runtime.CoreML",
            WhisperRuntimeType.Cuda => "Whisper.net.Runtime.Cuda",
            WhisperRuntimeType.Vulkan => "Whisper.net.Runtime.Vulkan",
            _ => "Whisper.net.Runtime"
        };
    }

    public void LogRuntimeInfo()
    {
        _logger.LogInformation("=== System Information ===");
        _logger.LogInformation("OS: {OS}", RuntimeInformation.OSDescription);
        _logger.LogInformation("Architecture: {Arch}", RuntimeInformation.ProcessArchitecture);
        _logger.LogInformation("Framework: {Framework}", RuntimeInformation.FrameworkDescription);

        var runtime = DetectBestRuntime();
        var packageName = GetRuntimePackageName(runtime);

        _logger.LogInformation("=== Recommended Runtime ===");
        _logger.LogInformation("Runtime Type: {RuntimeType}", runtime);
        _logger.LogInformation("Package: {PackageName}", packageName);
        _logger.LogInformation("==========================");
    }
}
