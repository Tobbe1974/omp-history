using OmpHistory.Components;
using OmpHistory.Data;
using OmpHistory.Models;
using OmpHistory.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

var urls = builder.Configuration["Urls"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(urls))
{
    var envUrl = Environment.GetEnvironmentVariable("OMP_HISTORY_URL")?.Trim().TrimEnd('/');
    var envPort = Environment.GetEnvironmentVariable("OMP_HISTORY_PORT")?.Trim();
    if (!string.IsNullOrWhiteSpace(envUrl))
    {
        builder.WebHost.UseUrls(envUrl);
    }
    else if (!string.IsNullOrWhiteSpace(envPort))
    {
        builder.WebHost.UseUrls($"http://localhost:{envPort}");
    }
    else
    {
        builder.WebHost.UseUrls("http://localhost:5180");
    }
}

builder.Services.AddSingleton<HistoryNotifier>();
builder.Services.AddSingleton<HistoryRepository>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var notifier = sp.GetRequiredService<HistoryNotifier>();
    var dbPath = config["Database:Path"];
    if (string.IsNullOrWhiteSpace(dbPath))
    {
        dbPath = Path.Combine(builder.Environment.ContentRootPath, "omp-history.db");
    }
    return new HistoryRepository(dbPath, notifier);
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

var api = app.MapGroup("/api");

api.MapGet("/repos", (HistoryRepository repo) =>
    Results.Ok(repo.GetRepos()));

api.MapGet("/repos/{repoName}/sessions", (string repoName, HistoryRepository repo) =>
    Results.Ok(repo.GetSessionsByRepo(repoName)));

api.MapGet("/sessions", (string? repo, HistoryRepository repository) =>
{
    if (!string.IsNullOrWhiteSpace(repo))
    {
        return Results.Ok(repository.GetSessionsByRepo(repo));
    }
    return Results.Ok(Array.Empty<SessionRecord>());
});

api.MapPost("/sessions", (CreateSessionRequest req, HistoryRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(req.SessionId))
    {
        return Results.BadRequest(new { error = "sessionId is required" });
    }

    repo.UpsertSession(req.SessionId, req.Repo, req.Name);
    return Results.Created($"/api/sessions/{req.SessionId}", new
    {
        sessionId = req.SessionId,
        repo = req.Repo,
        name = req.Name
    });
});

api.MapPut("/sessions/{sessionId}", (string sessionId, RenameSessionRequest req, HistoryRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
    {
        return Results.BadRequest(new { error = "name is required" });
    }

    var updated = repo.RenameSession(sessionId, req.Name, req.Repo);
    return updated ? Results.Ok(new { sessionId, name = req.Name }) : Results.NotFound(new { error = "Session not found" });
});

api.MapDelete("/sessions/{sessionId}", (string sessionId, HistoryRepository repo) =>
{
    var deleted = repo.DeleteSession(sessionId);
    return deleted ? Results.NoContent() : Results.NotFound(new { error = "Session not found" });
});

api.MapGet("/sessions/{sessionId}/conversations", (string sessionId, HistoryRepository repo) =>
    Results.Ok(repo.GetConversations(sessionId)));

api.MapPost("/conversations", (CreateConversationRequest req, HistoryRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(req.SessionId))
    {
        return Results.BadRequest(new { error = "sessionId is required" });
    }

    var record = repo.AddConversation(
        req.SessionId,
        req.Prompt ?? string.Empty,
        req.ResponseMd ?? string.Empty,
        req.Repo,
        req.SessionName);

    return Results.Created($"/api/sessions/{req.SessionId}/conversations/{record.Id}", record);
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
