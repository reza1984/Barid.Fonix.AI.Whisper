using Microsoft.AspNetCore.Mvc;
using Barid.Fonix.AI.Whisper.Services;

namespace Barid.Fonix.AI.Whisper.Controllers;

[ApiController]
[Route("api/transcribe")]
public class TranscriptionController : ControllerBase
{
    private readonly WhisperService _whisperService;
    private readonly ILogger<TranscriptionController> _logger;

    public TranscriptionController(WhisperService whisperService, ILogger<TranscriptionController> logger)
    {
        _whisperService = whisperService;
        _logger = logger;
    }

    [HttpPost("file")]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<IActionResult> TranscribeFile(
        IFormFile file,
        [FromQuery] string model = "ggml-base.bin",
        [FromQuery] string language = "auto",
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No file uploaded" });
        }

        if (!file.ContentType.Contains("audio") && !file.ContentType.Contains("wav"))
        {
            return BadRequest(new { error = "File must be an audio file" });
        }

        TranscriptionSession? session = null;

        try
        {
            _logger.LogInformation("Transcribing file: {FileName}, Size: {FileSize} bytes, Model: {Model}",
                file.FileName, file.Length, model);

            session = await _whisperService.CreateSessionAsync(model, language, cancellationToken);

            using var audioStream = file.OpenReadStream();
            var result = await session.ProcessAudioFileAsync(audioStream, cancellationToken);

            return Ok(new
            {
                text = result.Text,
                segments = result.Segments.Select(s => new
                {
                    start = s.Start.TotalSeconds,
                    end = s.End.TotalSeconds,
                    text = s.Text
                }),
                duration = result.Segments.Any() ? result.Segments.Last().End.TotalSeconds : 0
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transcribing file: {FileName}", file.FileName);
            return StatusCode(500, new { error = $"Transcription failed: {ex.Message}" });
        }
        finally
        {
            session?.Dispose();
        }
    }

    [HttpGet("models")]
    public async Task<IActionResult> GetModels()
    {
        try
        {
            var models = await _whisperService.GetAvailableModelsAsync();

            return Ok(new
            {
                models,
                recommended = "ggml-base.bin",
                available = new[]
                {
                    new { name = "ggml-tiny.bin", size = "tiny", description = "Fastest, least accurate" },
                    new { name = "ggml-base.bin", size = "base", description = "Balanced speed and accuracy" },
                    new { name = "ggml-small.bin", size = "small", description = "Good accuracy, slower" },
                    new { name = "ggml-medium.bin", size = "medium", description = "High accuracy, slow" }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving models");
            return StatusCode(500, new { error = $"Failed to retrieve models: {ex.Message}" });
        }
    }
}
