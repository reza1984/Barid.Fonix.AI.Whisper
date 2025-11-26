using Whisper.net;

namespace Barid.Fonix.AI.Whisper.Services;

public class TranscriptionSession : IDisposable
{
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _sessionLimitSemaphore;
    private readonly ILogger _logger;
    private readonly List<float> _audioBuffer = [];
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private bool _disposed;
    private int _totalSamplesProcessed;
    private const int MinSamplesForTranscription = 16000 * 2; // 2 seconds at 16kHz

    public TranscriptionSession(WhisperProcessor processor, SemaphoreSlim sessionLimitSemaphore, ILogger logger)
    {
        _processor = processor;
        _sessionLimitSemaphore = sessionLimitSemaphore;
        _logger = logger;
    }

    public async Task<TranscriptionResult> ProcessAudioChunkAsync(float[] audioSamples, CancellationToken cancellationToken = default)
    {
        await _processingLock.WaitAsync(cancellationToken);
        try
        {
            // Add new samples to buffer
            _audioBuffer.AddRange(audioSamples);
            _totalSamplesProcessed += audioSamples.Length;

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Received audio chunk: {SampleCount} samples ({Duration:F2}s), Buffer size: {BufferSize} samples ({BufferDuration:F2}s)",
                    audioSamples.Length, audioSamples.Length / 16000.0, _audioBuffer.Count, _audioBuffer.Count / 16000.0);
            }

            // Only process if we have enough audio (at least 2 seconds)
            if (_audioBuffer.Count < MinSamplesForTranscription)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Not enough audio buffered yet. Need {MinSamples}, have {CurrentSamples}",
                        MinSamplesForTranscription, _audioBuffer.Count);
                }
                return new TranscriptionResult
                {
                    Text = "",
                    Segments = [],
                    IsPartial = true
                };
            }

            // Process buffered audio
            var samplesToProcess = _audioBuffer.ToArray();
            _audioBuffer.Clear(); // Clear buffer after copying

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Processing buffered audio: {SampleCount} samples ({Duration:F2}s)",
                    samplesToProcess.Length, samplesToProcess.Length / 16000.0);
            }

            var segments = new List<SegmentData>();
            var fullText = "";
            var segmentCount = 0;

            await foreach (var segment in _processor.ProcessAsync(samplesToProcess, cancellationToken))
            {
                segmentCount++;
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Segment {Index}: [{Start:F2}s - {End:F2}s] Text: '{Text}'",
                        segmentCount, segment.Start.TotalSeconds, segment.End.TotalSeconds, segment.Text);
                }
                segments.Add(segment);
                fullText += segment.Text;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Transcription complete: {SegmentCount} segments, {TextLength} chars, Text: '{Text}'",
                    segmentCount, fullText.Length, fullText.Trim());
            }

            return new TranscriptionResult
            {
                Text = fullText.Trim(),
                Segments = segments,
                IsPartial = false
            };
        }
        finally
        {
            _processingLock.Release();
        }
    }

    public async Task<TranscriptionResult> ProcessAudioFileAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        await _processingLock.WaitAsync(cancellationToken);
        try
        {
            var segments = new List<SegmentData>();
            var fullText = "";

            await foreach (var segment in _processor.ProcessAsync(audioStream, cancellationToken))
            {
                segments.Add(segment);
                fullText += segment.Text;
            }

            return new TranscriptionResult
            {
                Text = fullText.Trim(),
                Segments = segments,
                IsPartial = false
            };
        }
        finally
        {
            _processingLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _processor?.Dispose();
        _processingLock?.Dispose();
        _sessionLimitSemaphore?.Release();
        _disposed = true;

        GC.SuppressFinalize(this);
    }
}

public class TranscriptionResult
{
    public string Text { get; set; } = "";
    public List<SegmentData> Segments { get; set; } = new();
    public bool IsPartial { get; set; }
}
