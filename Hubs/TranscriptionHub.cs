using System.Collections.Concurrent;
using Barid.Fonix.AI.Whisper.Services;
using Barid.Fonix.AI.Whisper.Utils;
using Microsoft.AspNetCore.SignalR;

namespace Barid.Fonix.AI.Whisper.Hubs;

public class TranscriptionHub : Hub
{
    private readonly WhisperService _whisperService;
    private readonly ILogger<TranscriptionHub> _logger;

    // Session management using Context.ConnectionId
    private static readonly ConcurrentDictionary<string, TranscriptionSession> _sessions = new();

    public TranscriptionHub(WhisperService whisperService, ILogger<TranscriptionHub> logger)
    {
        _whisperService = whisperService;
        _logger = logger;
    }

    public async Task StartSession(string modelName, string language, string? sessionId = null)
    {
        try
        {
            _logger.LogInformation("Starting session for connection {ConnectionId}, model: {ModelName}, language: {Language}",
                Context.ConnectionId, modelName, language);

            var session = await _whisperService.CreateSessionAsync(modelName, language);
            _sessions[Context.ConnectionId] = session;

            // Support for future multi-user collaboration
            if (!string.IsNullOrEmpty(sessionId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
                _logger.LogInformation("Client {ConnectionId} joined session {SessionId}",
                    Context.ConnectionId, sessionId);
            }

            await Clients.Caller.SendAsync("SessionStarted", new
            {
                model = modelName,
                language = language,
                message = "Transcription session started",
                sessionId = sessionId
            });

            // Notify other users in the group that someone joined (future feature)
            if (!string.IsNullOrEmpty(sessionId))
            {
                await Clients.OthersInGroup(sessionId).SendAsync("ParticipantJoined", new
                {
                    connectionId = Context.ConnectionId,
                    timestamp = DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start session for connection {ConnectionId}", Context.ConnectionId);
            await Clients.Caller.SendAsync("Error", ex.Message);
        }
    }

    public async Task SendAudioChunk(byte[] audioData)
    {
        if (!_sessions.TryGetValue(Context.ConnectionId, out var session))
        {
            await Clients.Caller.SendAsync("Error", "No active session");
            return;
        }

        try
        {
            // Validate WAV header
            if (!AudioUtils.ValidateWavHeader(audioData))
            {
                await Clients.Caller.SendAsync("Error", "Invalid audio format");
                return;
            }

            // Convert to float samples
            var samples = AudioUtils.ConvertBytesToFloatSamples(audioData);

            // Process audio
            var result = await session.ProcessAudioChunkAsync(samples);

            if (!string.IsNullOrWhiteSpace(result.Text) || result.IsPartial)
            {
                await Clients.Caller.SendAsync("TranscriptionResult", new
                {
                    text = result.Text,
                    isFinal = !result.IsPartial,
                    segments = result.Segments?.Select(s => new
                    {
                        start = s.Start.TotalSeconds,
                        end = s.End.TotalSeconds,
                        text = s.Text
                    })
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio chunk for connection {ConnectionId}", Context.ConnectionId);
            await Clients.Caller.SendAsync("Error", "Error processing audio");
        }
    }

    public async Task StopSession()
    {
        if (_sessions.TryRemove(Context.ConnectionId, out var session))
        {
            session.Dispose();
            _logger.LogInformation("Session stopped for connection {ConnectionId}", Context.ConnectionId);

            await Clients.Caller.SendAsync("SessionStopped", new
            {
                message = "Transcription session stopped"
            });
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Clean up session on disconnect
        if (_sessions.TryRemove(Context.ConnectionId, out var session))
        {
            session.Dispose();
            _logger.LogInformation("Session disposed for disconnected client {ConnectionId}. Reason: {Reason}",
                Context.ConnectionId, exception?.Message ?? "Normal disconnect");
        }

        // Note: Groups are automatically cleaned up by SignalR on disconnect
        // Future enhancement: notify other group members about participant leaving

        await base.OnDisconnectedAsync(exception);
    }

    // Future collaboration methods (examples for reference)
    /*
    public async Task BroadcastToSession(string sessionId, string message)
    {
        await Clients.Group(sessionId).SendAsync("Broadcast", message);
    }

    public async Task SendToUser(string targetConnectionId, string message)
    {
        await Clients.Client(targetConnectionId).SendAsync("DirectMessage", message);
    }
    */
}
