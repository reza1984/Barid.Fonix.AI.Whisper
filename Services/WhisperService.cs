using Whisper.net;
using Whisper.net.Ggml;

namespace Barid.Fonix.AI.Whisper.Services;

public class WhisperService : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly string _modelsDirectory;
    private readonly ILogger<WhisperService> _logger;
    private readonly SemaphoreSlim _sessionLimitSemaphore;
    private bool _disposed;

    public WhisperService(IConfiguration configuration, ILogger<WhisperService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _modelsDirectory = configuration["WhisperModelsPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "ai-models");

        var maxConcurrentSessions = configuration.GetValue<int>("MaxConcurrentTranscriptionSessions", 5);
        _sessionLimitSemaphore = new SemaphoreSlim(maxConcurrentSessions, maxConcurrentSessions);

        Directory.CreateDirectory(_modelsDirectory);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing Whisper service. Models directory: {ModelsDirectory}", _modelsDirectory);

        // Ensure default model is downloaded at startup
        var defaultModel = _configuration["DefaultWhisperModel"] ?? "ggml-base.bin";
        _logger.LogInformation("Ensuring default model is available: {DefaultModel}", defaultModel);

        try
        {
            await EnsureModelExistsAsync(defaultModel, cancellationToken);
            _logger.LogInformation("Whisper service initialized successfully. Default model available: {DefaultModel}", defaultModel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download default model {DefaultModel}", defaultModel);
        }
    }

    public async Task<string[]> GetAvailableModelsAsync()
    {
        return await Task.Run(() =>
        {
            var modelFiles = Directory.GetFiles(_modelsDirectory, "ggml-*.bin");
            return modelFiles.Select(Path.GetFileName).Where(f => f != null).Select(f => f!).ToArray();
        });
    }

    public async Task<TranscriptionSession> CreateSessionAsync(string modelName = "ggml-small.bin", string language = "auto", CancellationToken cancellationToken = default)
    {
        await _sessionLimitSemaphore.WaitAsync(cancellationToken);

        try
        {
            await EnsureModelExistsAsync(modelName, cancellationToken);

            // Load factory fresh for each session (WhisperFactory cannot be reused for multiple processors)
            var modelPath = Path.Combine(_modelsDirectory, modelName);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Creating WhisperFactory from {ModelPath} for new session", modelPath);
            }

            var factory = WhisperFactory.FromPath(modelPath);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Creating transcription session with language: {Language}", language);
            }

            var processor = factory.CreateBuilder()
                .WithLanguage(language)
                .WithPrompt("")
                .Build();

            return new TranscriptionSession(processor, factory, _sessionLimitSemaphore, _logger);
        }
        catch
        {
            _sessionLimitSemaphore.Release();
            throw;
        }
    }

    private async Task EnsureModelExistsAsync(string modelName, CancellationToken cancellationToken)
    {
        var modelPath = Path.Combine(_modelsDirectory, modelName);

        if (File.Exists(modelPath))
        {
            _logger.LogDebug("Model {ModelName} already exists at {ModelPath}", modelName, modelPath);
            return;
        }

        _logger.LogInformation("Model {ModelName} not found. Downloading...", modelName);

        var modelType = modelName switch
        {
            "ggml-tiny.bin" => GgmlType.Tiny,
            "ggml-tiny.en.bin" => GgmlType.TinyEn,
            "ggml-base.bin" => GgmlType.Base,
            "ggml-base.en.bin" => GgmlType.BaseEn,
            "ggml-small.bin" => GgmlType.Small,
            "ggml-small.en.bin" => GgmlType.SmallEn,
            "ggml-medium.bin" => GgmlType.Medium,
            "ggml-medium.en.bin" => GgmlType.MediumEn,
            "ggml-large-v1.bin" => GgmlType.LargeV1,
            "ggml-large-v2.bin" => GgmlType.LargeV2,
            "ggml-large-v3.bin" => GgmlType.LargeV3,
            _ => throw new ArgumentException($"Unknown model name: {modelName}")
        };

        var downloader = WhisperGgmlDownloader.Default;

        using var modelStream = await downloader.GetGgmlModelAsync(modelType, QuantizationType.NoQuantization, cancellationToken);
        using var fileStream = File.Create(modelPath);
        await modelStream.CopyToAsync(fileStream, cancellationToken);

        _logger.LogInformation("Model {ModelName} downloaded successfully to {ModelPath}", modelName, modelPath);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _sessionLimitSemaphore.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }
}
