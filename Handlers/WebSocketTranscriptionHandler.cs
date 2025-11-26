using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Barid.Fonix.AI.Whisper.Services;

namespace Barid.Fonix.AI.Whisper.Handlers;

public static class WebSocketTranscriptionHandler
{
    private const int MaxMessageSize = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private class SessionHolder
    {
        public TranscriptionSession? Session { get; set; }
    }

    public static async Task HandleWebSocketAsync(
        WebSocket webSocket,
        WhisperService whisperService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sessionHolder = new SessionHolder();
        var buffer = new byte[MaxMessageSize];
        var messageBuffer = new List<byte>();

        try
        {
            logger.LogInformation("WebSocket connection established");

            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var receiveResult = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Connection closed by client",
                        cancellationToken);
                    break;
                }

                messageBuffer.AddRange(buffer.Take(receiveResult.Count));

                if (!receiveResult.EndOfMessage)
                {
                    continue;
                }

                var messageBytes = messageBuffer.ToArray();
                messageBuffer.Clear();

                if (receiveResult.MessageType == WebSocketMessageType.Text)
                {
                    var controlMessage = Encoding.UTF8.GetString(messageBytes);
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Received TEXT message, length: {Length} bytes", messageBytes.Length);
                    }
                    await HandleControlMessageAsync(
                        controlMessage,
                        webSocket,
                        whisperService,
                        sessionHolder,
                        logger,
                        cancellationToken);
                }
                else if (receiveResult.MessageType == WebSocketMessageType.Binary)
                {
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Received BINARY message, length: {Length} bytes, Session exists: {SessionExists}",
                            messageBytes.Length,
                            sessionHolder.Session != null);
                    }

                    if (sessionHolder.Session == null)
                    {
                        logger.LogWarning("Rejecting binary message - session not initialized");
                        await SendErrorAsync(webSocket, "Session not initialized. Send a control message first.", cancellationToken);
                        continue;
                    }

                    await HandleAudioDataAsync(messageBytes, sessionHolder.Session, webSocket, logger, cancellationToken);
                }
            }
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "WebSocket error occurred");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling WebSocket connection");
            await SendErrorAsync(webSocket, $"Internal error: {ex.Message}", cancellationToken);
        }
        finally
        {
            sessionHolder.Session?.Dispose();
            logger.LogInformation("WebSocket connection closed");
        }
    }

    private static async Task HandleControlMessageAsync(
        string message,
        WebSocket webSocket,
        WhisperService whisperService,
        SessionHolder sessionHolder,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Received control message: {Message}", message);
            }

            var controlMessage = JsonSerializer.Deserialize<ControlMessage>(message, JsonOptions);

            if (controlMessage == null)
            {
                await SendErrorAsync(webSocket, "Invalid control message format", cancellationToken);
                return;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Parsed command: {Command}, Model: {Model}",
                    controlMessage.Command ?? "null",
                    controlMessage.ModelName ?? "null");
            }

            switch (controlMessage.Command?.ToLowerInvariant())
            {
                case "start":
                    if (sessionHolder.Session != null)
                    {
                        await SendErrorAsync(webSocket, "Session already started", cancellationToken);
                        return;
                    }

                    var modelName = controlMessage.ModelName ?? "ggml-base.bin";
                    var language = controlMessage.Language ?? "auto";

                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Starting transcription session with model: {ModelName}, language: {Language}",
                            modelName, language);
                    }

                    sessionHolder.Session = await whisperService.CreateSessionAsync(modelName, language, cancellationToken);

                    await SendResponseAsync(webSocket, new
                    {
                        type = "started",
                        model = modelName,
                        language = language,
                        message = "Transcription session started"
                    }, cancellationToken);
                    break;

                case "stop":
                    if (sessionHolder.Session != null)
                    {
                        sessionHolder.Session.Dispose();
                        sessionHolder.Session = null;

                        await SendResponseAsync(webSocket, new
                        {
                            type = "stopped",
                            message = "Transcription session stopped"
                        }, cancellationToken);
                    }
                    break;

                default:
                    await SendErrorAsync(webSocket, $"Unknown command: {controlMessage.Command}", cancellationToken);
                    break;
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse control message");
            await SendErrorAsync(webSocket, "Invalid JSON format", cancellationToken);
        }
    }

    private static async Task HandleAudioDataAsync(
        byte[] audioData,
        TranscriptionSession session,
        WebSocket webSocket,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!AudioUtils.ValidateWavHeader(audioData))
            {
                await SendErrorAsync(webSocket, "Invalid WAV format", cancellationToken);
                return;
            }

            var samples = AudioUtils.ConvertWavBytesToFloatSamples(audioData);
            logger.LogDebug("Processing {SampleCount} audio samples", samples.Length);

            var result = await session.ProcessAudioChunkAsync(samples, cancellationToken);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Transcription result: IsPartial={IsPartial}, Text length={Length}, Text='{Text}'",
                    result.IsPartial, result.Text.Length, result.Text);
            }

            // Send result even if empty (for interim results), but only if there's meaningful content or it's partial
            if (!string.IsNullOrWhiteSpace(result.Text) || result.IsPartial)
            {
                await SendResponseAsync(webSocket, new
                {
                    type = "result",
                    text = result.Text,
                    isFinal = !result.IsPartial,
                    segments = result.Segments.Select(s => new
                    {
                        start = s.Start,
                        end = s.End,
                        text = s.Text
                    })
                }, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing audio data");
            await SendErrorAsync(webSocket, $"Error processing audio: {ex.Message}", cancellationToken);
        }
    }

    private static async Task SendResponseAsync(WebSocket webSocket, object response, CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(response);
        var bytes = Encoding.UTF8.GetBytes(json);

        await webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private static async Task SendErrorAsync(WebSocket webSocket, string error, CancellationToken cancellationToken)
    {
        await SendResponseAsync(webSocket, new
        {
            type = "error",
            error
        }, cancellationToken);
    }

    private class ControlMessage
    {
        public string? Command { get; set; }
        public string? ModelName { get; set; }
        public string? Language { get; set; }
    }
}
