using Barid.Fonix.AI.Whisper.Services;
using Barid.Fonix.AI.Whisper.Handlers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddSingleton<WhisperRuntimeDetector>();
builder.Services.AddSingleton<WhisperService>();

builder.Services.Configure<IConfiguration>(config =>
{
    config["MaxConcurrentTranscriptionSessions"] = "5";
});

var app = builder.Build();

// Detect and log runtime information
var runtimeDetector = app.Services.GetRequiredService<WhisperRuntimeDetector>();
runtimeDetector.LogRuntimeInfo();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

var whisperService = app.Services.GetRequiredService<WhisperService>();
await whisperService.InitializeAsync();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws-transcribe")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

            await WebSocketTranscriptionHandler.HandleWebSocketAsync(
                webSocket,
                whisperService,
                logger,
                context.RequestAborted);
        }
        else
        {
            context.Response.StatusCode = 400;
        }
    }
    else
    {
        await next();
    }
});

app.UseStaticFiles();
app.MapControllers();

app.MapGet("/", () => Results.Content(@"
<!DOCTYPE html>
<html>
<head>
    <title>Whisper Transcription API</title>
    <style>
        body { font-family: Arial, sans-serif; max-width: 800px; margin: 50px auto; padding: 20px; }
        h1 { color: #333; }
        .endpoint { background: #f5f5f5; padding: 15px; margin: 10px 0; border-radius: 5px; }
        code { background: #e0e0e0; padding: 2px 6px; border-radius: 3px; }
    </style>
</head>
<body>
    <h1>Whisper Transcription API</h1>
    <p>Welcome to the live transcription service powered by OpenAI Whisper.</p>

    <h2>Endpoints</h2>

    <div class='endpoint'>
        <h3>WebSocket Live Transcription</h3>
        <p><code>WS /ws-transcribe</code></p>
        <p>Real-time audio transcription via WebSocket. Send audio chunks as WAV binary data.</p>
    </div>

    <div class='endpoint'>
        <h3>File Upload Transcription</h3>
        <p><code>POST /api/transcribe/file</code></p>
        <p>Upload an audio file for transcription. Supports WAV format.</p>
    </div>

    <div class='endpoint'>
        <h3>Available Models</h3>
        <p><code>GET /api/transcribe/models</code></p>
        <p>List available Whisper models and their characteristics.</p>
    </div>

    <h2>Client Library</h2>
    <p>Include <code>/js/whisper-client.js</code> in your web page for a Web Speech API-like interface.</p>
</body>
</html>
", "text/html"));

app.Run();
