using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;
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

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<HistoryNotifier>();
builder.Services.AddSingleton<ActiveSessionTracker>();
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
builder.Services.AddSingleton<GitCompareService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    RequireHeaderSymmetry = false
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

var configuredPathBase = builder.Configuration["PathBase"] ?? Environment.GetEnvironmentVariable("OMP_HISTORY_PATH_BASE");
if (!string.IsNullOrWhiteSpace(configuredPathBase))
{
    var normalizedPathBase = "/" + configuredPathBase.Trim().Trim('/');
    app.UsePathBase(normalizedPathBase);
}

app.Use(async (context, next) =>
{
    var forwardedPrefix = context.Request.Headers["X-Forwarded-Prefix"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(forwardedPrefix))
    {
        context.Request.PathBase = new PathString("/" + forwardedPrefix.Trim().Trim('/'));
    }
    await next();
});

var configuredAuth = (builder.Configuration["BasicAuth"] ?? Environment.GetEnvironmentVariable("OMP_HISTORY_AUTH"))?.Trim();
if (!string.IsNullOrWhiteSpace(configuredAuth))
{
    var expectedCredentials = configuredAuth.Contains(':') ? configuredAuth : $"omp:{configuredAuth}";
    var expectedBytes = Encoding.UTF8.GetBytes(expectedCredentials);

    app.Use(async (context, next) =>
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Basic ".Length..].Trim();
            try
            {
                var decodedBytes = Convert.FromBase64String(token);
                if (CryptographicOperations.FixedTimeEquals(decodedBytes, expectedBytes))
                {
                    await next();
                    return;
                }
            }
            catch
            {
                // Invalid base64
            }
        }

        context.Response.Headers.WWWAuthenticate = "Basic realm=\"OMP History\", charset=\"UTF-8\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Unauthorized");
    });
}

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
    return Results.Ok(repository.GetAllSessions());
});

api.MapGet("/sessions/{sessionId}", (string sessionId, HistoryRepository repo) =>
{
    var session = repo.GetSession(sessionId);
    return session is not null ? Results.Ok(session) : Results.NotFound(new { error = "Session not found" });
});

api.MapGet("/sessions/{sessionId}/export-jsonl", (string sessionId, string? cwd, HistoryRepository repo) =>
{
    var jsonl = repo.ExportSessionJsonl(sessionId, cwd);
    if (jsonl is null)
    {
        return Results.NotFound(new { error = "Session not found" });
    }
    return Results.Text(jsonl, "application/x-ndjson", Encoding.UTF8);
});

api.MapPost("/sessions", (CreateSessionRequest req, HistoryRepository repo, ActiveSessionTracker tracker) =>
{
    if (string.IsNullOrWhiteSpace(req.SessionId))
    {
        return Results.BadRequest(new { error = "sessionId is required" });
    }

    repo.UpsertSession(req.SessionId, req.Repo, req.Name, req.RepoPath);
    tracker.RecordHeartbeat(req.SessionId);
    return Results.Created($"/api/sessions/{req.SessionId}", new
    {
        sessionId = req.SessionId,
        repo = req.Repo,
        name = req.Name
    });
});

api.MapPut("/sessions/{sessionId}", (string sessionId, RenameSessionRequest req, HistoryRepository repo, ActiveSessionTracker tracker) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
    {
        return Results.BadRequest(new { error = "name is required" });
    }

    var updated = repo.RenameSession(sessionId, req.Name, req.Repo, req.RepoPath);
    tracker.RecordHeartbeat(sessionId);
    return updated ? Results.Ok(new { sessionId, name = req.Name }) : Results.NotFound(new { error = "Session not found" });
});

api.MapDelete("/sessions/{sessionId}", (string sessionId, HistoryRepository repo) =>
{
    var deleted = repo.DeleteSession(sessionId);
    return deleted ? Results.NoContent() : Results.NotFound(new { error = "Session not found" });
});

api.MapGet("/deleted-sessions", (HistoryRepository repo) =>
    Results.Ok(repo.GetDeletedSessionIds()));

api.MapGet("/sessions/{sessionId}/conversations", (string sessionId, HistoryRepository repo) =>
    Results.Ok(repo.GetConversations(sessionId)));

api.MapPost("/conversations", (CreateConversationRequest req, HistoryRepository repo, ActiveSessionTracker tracker) =>
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
        req.SessionName,
        req.RepoPath,
        req.DiffPatch,
        req.GitTreeBefore,
        req.GitTreeAfter);
    tracker.RecordHeartbeat(req.SessionId);

    return Results.Created($"/api/sessions/{req.SessionId}/conversations/{record.Id}", record);
});

api.MapGet("/active-sessions", (ActiveSessionTracker tracker) =>
    Results.Ok(tracker.GetActiveSessionIds()));

api.MapPost("/sessions/{sessionId}/heartbeat", async (string sessionId, HttpRequest httpRequest, HistoryRepository repo, ActiveSessionTracker tracker) =>
{
    if (httpRequest.ContentLength is > 0)
    {
        try
        {
            var hb = await httpRequest.ReadFromJsonAsync<ActiveSessionHeartbeatRequest>();
            if (!string.IsNullOrWhiteSpace(hb?.RepoPath))
            {
                repo.UpdateSessionRepoPath(sessionId, hb.RepoPath);
            }
        }
        catch
        {
            // ignore optional heartbeat body parse errors
        }
    }

    tracker.RecordHeartbeat(sessionId);
    var items = tracker.DequeuePendingFeedback(sessionId);
    var deletedSessionIds = repo.GetDeletedSessionIds();
    return Results.Ok(new { sessionId, items, deletedSessionIds });
});

api.MapGet("/sessions/{sessionId}/feedback/pending", (string sessionId, ActiveSessionTracker tracker) =>
{
    var items = tracker.DequeuePendingFeedback(sessionId);
    return Results.Ok(new { sessionId, items });
});

api.MapPost("/sessions/{sessionId}/feedback", (string sessionId, CreateFeedbackRequest req, ActiveSessionTracker tracker) =>
{
    if (string.IsNullOrWhiteSpace(req.Comment))
    {
        return Results.BadRequest(new { error = "comment is required" });
    }

    var item = tracker.EnqueueFeedback(sessionId, req.Comment, req.QuotedText, req.ConversationId);
    return Results.Accepted($"/api/sessions/{sessionId}/feedback/{item.Id}", new
    {
        item,
        isSessionActive = tracker.IsSessionActive(sessionId)
    });
});

api.MapGet("/repos/{repoName}/git/overview", (string repoName, string? repoPath, GitCompareService gitService) =>
    Results.Ok(gitService.GetRepoOverview(repoName, repoPath)));

api.MapGet("/repos/{repoName}/git/diff", (
    string repoName,
    string? mode,
    string? baseRef,
    string? targetRef,
    string? fileFilter,
    string? repoPath,
    GitCompareService gitService) =>
    Results.Ok(gitService.GetDiff(repoName, mode ?? "working", baseRef, targetRef, fileFilter, repoPath)));

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
