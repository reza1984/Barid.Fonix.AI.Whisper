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
    private const int MinSamplesForTranscription = 16000; // 1 second at 16kHz
    private const int MaxBufferSamples = 16000 * 3; // 3 seconds max buffer (reduced for less overlap)
    private const int OverlapSamples = 16000 / 8; // 0.125 second overlap (minimal)
    private const float VadThreshold = 0.02f; // Voice activity detection threshold
    private const int SilenceThreshold = 16000; // 1 second of silence
    private string _cumulativeTranscript = ""; // Full transcript so far
    private string _lastProcessedText = ""; // Last text we got from Whisper
    private int _silenceSamples = 0;

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
            _audioBuffer.AddRange(audioSamples);
            _totalSamplesProcessed += audioSamples.Length;

            // Voice Activity Detection - check if there's actual speech
            var hasVoice = DetectVoiceActivity(audioSamples);
            if (!hasVoice)
            {
                _silenceSamples += audioSamples.Length;
            }
            else
            {
                _silenceSamples = 0;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Received audio chunk: {SampleCount} samples ({Duration:F2}s), Buffer size: {BufferSize} samples ({BufferDuration:F2}s), Voice: {HasVoice}",
                    audioSamples.Length, audioSamples.Length / 16000.0, _audioBuffer.Count, _audioBuffer.Count / 16000.0, hasVoice);
            }

            // Check if we have enough audio for processing
            if (_audioBuffer.Count < MinSamplesForTranscription)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Not enough audio buffered yet. Need {MinSamples}, have {CurrentSamples}",
                        MinSamplesForTranscription, _audioBuffer.Count);
                }
                return new TranscriptionResult
                {
                    Text = "",
                    Segments = [],
                    IsPartial = true
                };
            }

            // Process current buffer
            var samplesToProcess = _audioBuffer.ToArray();

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Processing audio: {SampleCount} samples ({Duration:F2}s)",
                    samplesToProcess.Length, samplesToProcess.Length / 16000.0);
            }

            var segments = new List<SegmentData>();
            var fullText = "";
            var segmentCount = 0;

            // Process audio
            await foreach (var segment in _processor.ProcessAsync(samplesToProcess, cancellationToken))
            {
                segmentCount++;
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Segment {Index}: [{Start:F2}s - {End:F2}s] Text: '{Text}'",
                        segmentCount, segment.Start.TotalSeconds, segment.End.TotalSeconds, segment.Text);
                }
                segments.Add(segment);
                fullText += segment.Text;
            }

            var currentTranscript = CleanTranscript(fullText);

            // Determine if this is interim or final based on silence detection or buffer fullness
            bool hasSilence = _silenceSamples >= SilenceThreshold;
            bool bufferFull = _audioBuffer.Count >= MaxBufferSamples;
            bool isFinal = hasSilence || bufferFull;

            string textToSend;

            if (isFinal)
            {
                // For final results: send the current full transcript
                // This becomes the finalized text
                textToSend = currentTranscript;

                // Update cumulative transcript
                if (!string.IsNullOrWhiteSpace(currentTranscript))
                {
                    if (!string.IsNullOrWhiteSpace(_cumulativeTranscript))
                    {
                        _cumulativeTranscript += " " + currentTranscript;
                    }
                    else
                    {
                        _cumulativeTranscript = currentTranscript;
                    }

                    // Clear buffer but keep small overlap for continuity
                    if (_audioBuffer.Count > OverlapSamples)
                    {
                        var overlap = _audioBuffer.Skip(_audioBuffer.Count - OverlapSamples).ToList();
                        _audioBuffer.Clear();
                        _audioBuffer.AddRange(overlap);
                    }
                    else
                    {
                        _audioBuffer.Clear();
                    }

                    // Reset for next phrase
                    _lastProcessedText = "";
                    _silenceSamples = 0; // Reset silence counter
                }
            }
            else
            {
                // For interim results: send FULL current transcript
                // This allows client to replace the interim display completely
                textToSend = currentTranscript;
                _lastProcessedText = currentTranscript; // Track what we sent
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Transcription: IsFinal={IsFinal}, HasSilence={HasSilence}, BufferFull={BufferFull}, TextToSend='{Text}', Cumulative='{Cumulative}'",
                    isFinal, hasSilence, bufferFull, textToSend, _cumulativeTranscript);
            }

            return new TranscriptionResult
            {
                Text = textToSend,
                Segments = segments,
                IsPartial = !isFinal
            };
        }
        finally
        {
            _processingLock.Release();
        }
    }

    private static string CleanTranscript(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        // Remove common Whisper artifacts
        text = text.Replace("[BLANK_AUDIO]", "")
                   .Replace("[MUSIC]", "")
                   .Replace("[NOISE]", "")
                   .Replace("[SILENCE]", "")
                   .Replace("(BLANK_AUDIO)", "")
                   .Replace("(MUSIC)", "")
                   .Replace("(NOISE)", "")
                   .Replace("(SILENCE)", "")
                   .Replace("  ", " ") // Clean up double spaces
                   .Trim();

        return text;
    }

    private static string ExtractNewText(string oldText, string newText)
    {
        if (string.IsNullOrWhiteSpace(oldText))
            return newText ?? "";

        if (string.IsNullOrWhiteSpace(newText))
            return "";

        // Normalize whitespace
        oldText = oldText.Trim();
        newText = newText.Trim();

        // If texts are identical, no new content
        if (oldText.Equals(newText, StringComparison.OrdinalIgnoreCase))
            return "";

        // If new text starts with old text, return the difference
        if (newText.StartsWith(oldText, StringComparison.OrdinalIgnoreCase))
        {
            return newText[oldText.Length..].Trim();
        }

        // Find longest common suffix of old text and prefix of new text
        // This handles cases where transcription refines earlier words
        var oldWords = oldText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var newWords = newText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Find where old text ends in new text (common overlap)
        int commonPrefixLength = 0;
        int maxCommon = Math.Min(oldWords.Length, newWords.Length);

        for (int i = 0; i < maxCommon; i++)
        {
            if (oldWords[i].Equals(newWords[i], StringComparison.OrdinalIgnoreCase))
            {
                commonPrefixLength = i + 1;
            }
            else
            {
                break;
            }
        }

        // If we found common words at the start, return what's after them
        if (commonPrefixLength > 0 && commonPrefixLength < newWords.Length)
        {
            return string.Join(" ", newWords.Skip(commonPrefixLength));
        }

        // If no overlap found but there's new text, check if it's a continuation
        // (old text might be a subset of new text somewhere in the middle)
        if (newText.Contains(oldText, StringComparison.OrdinalIgnoreCase))
        {
            var index = newText.IndexOf(oldText, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var afterOld = newText[(index + oldText.Length)..].Trim();
                if (!string.IsNullOrEmpty(afterOld))
                    return afterOld;
            }
        }

        // If completely different, return all new text
        // This happens when buffer was cleared and we start fresh
        return newText;
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

    private static bool DetectVoiceActivity(float[] samples)
    {
        if (samples.Length == 0) return false;

        // Calculate RMS (Root Mean Square) energy
        var sumSquares = 0.0;
        foreach (var sample in samples)
        {
            sumSquares += sample * sample;
        }
        var rms = Math.Sqrt(sumSquares / samples.Length);

        // Check if energy exceeds threshold
        return rms > VadThreshold;
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
