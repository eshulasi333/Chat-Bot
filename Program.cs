using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);

// CORS for Angular
builder.Services.AddCors(p => p.AddPolicy("dev",
    x => x.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));

// HttpClient for Gemini
builder.Services.AddHttpClient<GeminiService>();

// JSON camelCase
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseCors("dev");
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/api/diag/ping", () => Results.Ok("RuleBot API running"));

app.MapGet("/api/history/{sessionId}", async (string sessionId, IConfiguration cfg) =>
{
    await using var db = new MySqlConnection(cfg.GetConnectionString("Default"));
    var rows = await db.QueryAsync<ChatMessage>(
        @"SELECT id, session_id AS sessionId, role, content, created_at AS createdAt
          FROM messages WHERE session_id=@sessionId ORDER BY id ASC", new { sessionId });
    return Results.Ok(rows);
});

app.MapPost("/api/chat", async (ChatRequest req, IConfiguration cfg, GeminiService gemini) =>
{
    try
    {
        var sid = string.IsNullOrWhiteSpace(req.SessionId) ? Guid.NewGuid().ToString("N") : req.SessionId;

        await using var db = new MySqlConnection(cfg.GetConnectionString("Default"));

        // Save user message
        await db.ExecuteAsync(
            "INSERT INTO messages(session_id, role, content) VALUES (@sid, 'user', @msg)",
            new { sid, msg = req.UserMessage });

        // Load history (give Gemini context)
        var history = await db.QueryAsync<ChatMessage>(
            @"SELECT id, session_id AS sessionId, role, content, created_at AS createdAt
              FROM messages WHERE session_id=@sid ORDER BY id ASC LIMIT 20", new { sid });

        // Ask Gemini
        var reply = await gemini.GenerateAsync(history, req.UserMessage);

        // Save assistant reply
        await db.ExecuteAsync(
            "INSERT INTO messages(session_id, role, content) VALUES (@sid, 'assistant', @reply)",
            new { sid, reply });

        return Results.Ok(new ChatResponse(reply, DateTime.UtcNow.ToString("o"), sid));
    }
    catch (Exception ex)
    {
        // Bubble up the true reason (helps when testing in Swagger)
        return Results.Problem(ex.ToString());
    }
});

app.Run();

// DTOs
public record ChatRequest(string? SessionId, string UserMessage);
public record ChatResponse([property: JsonPropertyName("botMessage")] string BotMessage,
                           [property: JsonPropertyName("timestamp")] string Timestamp,
                           [property: JsonPropertyName("sessionId")] string SessionId);

public class ChatMessage
{
    public long id { get; set; }
    public string sessionId { get; set; } = "";
    public string role { get; set; } = "";      // "user" or "assistant"
    public string content { get; set; } = "";
    public DateTime createdAt { get; set; }
}
