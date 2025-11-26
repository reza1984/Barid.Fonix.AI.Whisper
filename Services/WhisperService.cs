using Whisper.net;
using Whisper.net.Ggml;

namespace Barid.Fonix.AI.Whisper.Services;

public class WhisperService : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly string _modelsDirectory;
    private readonly ILogger<WhisperService> _logger;
    private readonly Dictionary<string, WhisperFactory> _loadedModels = new();
    private readonly SemaphoreSlim _modelLoadLock = new(1, 1);
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

        var defaultModel = _configuration["DefaultWhisperModel"] ?? "ggml-base.bin";
        _logger.LogInformation("Default model: {DefaultModel}", defaultModel);

        await EnsureModelExistsAsync(defaultModel, cancellationToken);
        await LoadModelAsync(defaultModel, cancellationToken);

        _logger.LogInformation("Whisper service initialized successfully");
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
            var factory = await GetOrLoadModelAsync(modelName, cancellationToken);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Creating transcription session with language: {Language}", language);
            }

            var processor = factory.CreateBuilder()
                .WithLanguage(language)
                .WithPrompt("")
                .Build();

            return new TranscriptionSession(processor, _sessionLimitSemaphore, _logger);
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

    private async Task<WhisperFactory> GetOrLoadModelAsync(string modelName, CancellationToken cancellationToken)
    {
        if (_loadedModels.TryGetValue(modelName, out var factory))
        {
            return factory;
        }

        return await LoadModelAsync(modelName, cancellationToken);
    }

    private async Task<WhisperFactory> LoadModelAsync(string modelName, CancellationToken cancellationToken)
    {
        await _modelLoadLock.WaitAsync(cancellationToken);
        try
        {
            if (_loadedModels.TryGetValue(modelName, out var existingFactory))
            {
                return existingFactory;
            }

            var modelPath = Path.Combine(_modelsDirectory, modelName);
            _logger.LogInformation("Loading model from {ModelPath}", modelPath);

            var factory = WhisperFactory.FromPath(modelPath);
            _loadedModels[modelName] = factory;

            _logger.LogInformation("Model {ModelName} loaded successfully", modelName);
            return factory;
        }
        finally
        {
            _modelLoadLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var factory in _loadedModels.Values)
        {
            factory.Dispose();
        }

        _loadedModels.Clear();
        _modelLoadLock.Dispose();
        _sessionLimitSemaphore.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }
}
