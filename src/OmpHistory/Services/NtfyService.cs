using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using OmpHistory.Data;
using OmpHistory.Models;

namespace OmpHistory.Services;

public sealed class NtfyService : BackgroundService
{
    private const string OutboundTag = "omp-out";
    private const string DefaultPublicAppBaseUrl = "https://skegriebyavag13.duckdns.org/omp-history";

    private readonly HistoryRepository _repository;
    private readonly ActiveSessionTracker _sessionTracker;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NtfyService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, byte> _sentMessageIds = new(StringComparer.Ordinal);
    private string? _lastNotifiedSessionId;
    private long? _lastNotifiedConversationId;

    public NtfyService(
        HistoryRepository repository,
        ActiveSessionTracker sessionTracker,
        IConfiguration configuration,
        ILogger<NtfyService> logger)
    {
        _repository = repository;
        _sessionTracker = sessionTracker;
        _configuration = configuration;
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task NotifyTurnCompletedAsync(
        ConversationRecord record,
        string? repoName,
        string? sessionName,
        string? publicAppBaseUrl = null)
    {
        try
        {
            var settings = _repository.GetAppSettings();
            if (!settings.NtfyEnabled)
            {
                return;
            }

            _lastNotifiedSessionId = record.SessionId;
            _lastNotifiedConversationId = record.Id;

            var turnDetails = _repository.GetTurnDetails(record.Id);
            var effectiveRepo = !string.IsNullOrWhiteSpace(repoName)
                ? repoName.Trim()
                : (turnDetails?.Session.Repo ?? "omp");
            var effectiveSessionName = !string.IsNullOrWhiteSpace(sessionName)
                ? sessionName.Trim()
                : (turnDetails?.Session.Name ?? $"Session {record.SessionId[..Math.Min(8, record.SessionId.Length)]}");
            var turnNum = turnDetails?.TurnNumber ?? 1;

            var resolvedAppBaseUrl = ResolvePublicAppBaseUrl(publicAppBaseUrl, settings.NtfyServerUrl);
            var turnUrl = $"{resolvedAppBaseUrl}/turn/{record.Id}";

            var title = $"[{effectiveRepo}] {TruncateSingleLine(effectiveSessionName, 48)} (#{turnNum})";
            var promptPreview = TruncateSingleLine(record.Prompt, 110);
            var responsePreview = TruncateMultiLine(record.ResponseMd, 260);
            var creditsSuffix = record.CreditsUsed.HasValue ? $" • Cr: {record.CreditsUsed.Value:0.#}" : string.Empty;

            var bodyBuilder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(promptPreview))
            {
                bodyBuilder.AppendLine($"Q: {promptPreview}");
            }
            if (!string.IsNullOrWhiteSpace(responsePreview))
            {
                bodyBuilder.Append(responsePreview);
            }
            if (!string.IsNullOrEmpty(creditsSuffix))
            {
                bodyBuilder.AppendLine();
                bodyBuilder.Append($"Done{creditsSuffix}");
            }

            var messageBody = bodyBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(messageBody))
            {
                messageBody = "Task completed.";
            }

            await PublishNotificationAsync(settings.NtfyServerUrl, settings.NtfyTopic, title, messageBody, turnUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send ntfy turn notification for conversation {ConversationId}", record.Id);
        }
    }

    public async Task<(bool Ok, string Message)> SendTestNotificationAsync(string? publicAppBaseUrl = null)
    {
        try
        {
            var settings = _repository.GetAppSettings();
            var resolvedAppBaseUrl = ResolvePublicAppBaseUrl(publicAppBaseUrl, settings.NtfyServerUrl);
            var sessions = _repository.GetAllSessions(1);
            var latestTurnId = _lastNotifiedConversationId;
            if (!latestTurnId.HasValue && sessions.Count > 0)
            {
                var convs = _repository.GetConversations(sessions[0].SessionId);
                if (convs.Count > 0)
                {
                    latestTurnId = convs[^1].Id;
                    _lastNotifiedSessionId = sessions[0].SessionId;
                    _lastNotifiedConversationId = latestTurnId;
                }
            }

            var targetUrl = latestTurnId.HasValue
                ? $"{resolvedAppBaseUrl}/turn/{latestTurnId.Value}"
                : resolvedAppBaseUrl;

            var title = "OMP History • Test Notification";
            var body = $"Connected to topic '{settings.NtfyTopic}' on {settings.NtfyServerUrl}.\nTap to open Turn View, or reply in this topic to send instructions to your active OMP session.";

            await PublishNotificationAsync(settings.NtfyServerUrl, settings.NtfyTopic, title, body, targetUrl);
            return (true, $"Sent test notification to {settings.NtfyServerUrl}/{settings.NtfyTopic}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send test ntfy notification");
            return (false, $"ntfy error: {ex.Message}");
        }
    }

    private async Task PublishNotificationAsync(
        string serverUrl,
        string topic,
        string title,
        string body,
        string clickUrl)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        var cleanTopic = NormalizeTopic(topic);
        var publishUrl = $"{baseUrl}/{Uri.EscapeDataString(cleanTopic)}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var req = new HttpRequestMessage(HttpMethod.Post, publishUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };

        req.Headers.TryAddWithoutValidation("Title", SanitizeHeaderAscii(title));
        req.Headers.TryAddWithoutValidation("Tags", $"robot,{OutboundTag}");
        req.Headers.TryAddWithoutValidation("Click", clickUrl);
        req.Headers.TryAddWithoutValidation("Actions", $"view, Open & Reply, {clickUrl}, clear=true");

        using var res = await _httpClient.SendAsync(req, cts.Token);
        var respText = await res.Content.ReadAsStringAsync(cts.Token);
        res.EnsureSuccessStatusCode();

        try
        {
            using var doc = JsonDocument.Parse(respText);
            if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                var id = idProp.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _sentMessageIds[id] = 1;
                }
            }
        }
        catch
        {
            // ignore json parse
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = _repository.GetAppSettings();
                var serverUrl = NormalizeServerUrl(settings.NtfyServerUrl);
                var topic = NormalizeTopic(settings.NtfyTopic);

                if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(topic))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                // Prefer loopback when subscribing to our local ntfy server on the Pi
                var streamBaseUrl = IsSelfHostedPiUrl(serverUrl)
                    ? "http://127.0.0.1:8090"
                    : serverUrl;
                var streamUrl = $"{streamBaseUrl}/{Uri.EscapeDataString(topic)}/json";

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                using var req = new HttpRequestMessage(HttpMethod.Get, streamUrl);
                using var response = await _httpClient.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (!stoppingToken.IsCancellationRequested)
                {
                    // Re-check if user changed server or topic in settings
                    var currentSettings = _repository.GetAppSettings();
                    if (!string.Equals(NormalizeServerUrl(currentSettings.NtfyServerUrl), serverUrl, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(NormalizeTopic(currentSettings.NtfyTopic), topic, StringComparison.Ordinal))
                    {
                        break;
                    }

                    var line = await reader.ReadLineAsync(stoppingToken);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    HandleIncomingNtfyJsonLine(line);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ntfy subscription loop reconnecting after error");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void HandleIncomingNtfyJsonLine(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            if (!root.TryGetProperty("event", out var eventProp)
                || !string.Equals(eventProp.GetString(), "message", StringComparison.Ordinal))
            {
                return;
            }

            var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && _sentMessageIds.ContainsKey(id))
            {
                return;
            }

            if (root.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var tagEl in tagsProp.EnumerateArray())
                {
                    if (string.Equals(tagEl.GetString(), OutboundTag, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }

            if (root.TryGetProperty("click", out var clickProp)
                && clickProp.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(clickProp.GetString()))
            {
                return;
            }

            var message = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var targetSessionId = ResolveTargetSessionIdForReply();
            if (string.IsNullOrWhiteSpace(targetSessionId))
            {
                _logger.LogWarning("Received ntfy reply '{Message}', but no OMP session was found to route it to", message);
                return;
            }

            _sessionTracker.EnqueueFeedback(
                sessionId: targetSessionId,
                comment: message,
                quotedText: null,
                conversationId: _lastNotifiedConversationId,
                locationContext: "Reply via ntfy");

            _logger.LogInformation("Queued ntfy reply for session {SessionId}", targetSessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse incoming ntfy JSON event");
        }
    }

    private string? ResolveTargetSessionIdForReply()
    {
        if (!string.IsNullOrWhiteSpace(_lastNotifiedSessionId)
            && _sessionTracker.IsSessionActive(_lastNotifiedSessionId))
        {
            return _lastNotifiedSessionId;
        }

        var activeIds = _sessionTracker.GetActiveSessionIds();
        if (activeIds.Count > 0)
        {
            var allSessions = _repository.GetAllSessions(50);
            var latestActive = allSessions.FirstOrDefault(s => activeIds.Contains(s.SessionId));
            if (latestActive is not null)
            {
                return latestActive.SessionId;
            }
            return activeIds.First();
        }

        if (!string.IsNullOrWhiteSpace(_lastNotifiedSessionId))
        {
            return _lastNotifiedSessionId;
        }

        var recent = _repository.GetAllSessions(1);
        return recent.Count > 0 ? recent[0].SessionId : null;
    }

    private string ResolvePublicAppBaseUrl(string? candidate, string ntfyServerUrl)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate.Trim().TrimEnd('/');
        }

        var envPublic = _configuration["PublicBaseUrl"]
            ?? Environment.GetEnvironmentVariable("OMP_HISTORY_PUBLIC_URL");
        if (!string.IsNullOrWhiteSpace(envPublic))
        {
            return envPublic.Trim().TrimEnd('/');
        }

        if (ntfyServerUrl.Contains("skegriebyavag13.duckdns.org", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultPublicAppBaseUrl;
        }

        return DefaultPublicAppBaseUrl;
    }

    private static bool IsSelfHostedPiUrl(string serverUrl) =>
        serverUrl.Contains("skegriebyavag13.duckdns.org", StringComparison.OrdinalIgnoreCase)
        || serverUrl.Contains("127.0.0.1:8090", StringComparison.OrdinalIgnoreCase)
        || serverUrl.Contains("localhost:8090", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeServerUrl(string? url) =>
        string.IsNullOrWhiteSpace(url)
            ? "https://skegriebyavag13.duckdns.org"
            : url.Trim().TrimEnd('/');

    private static string NormalizeTopic(string? topic) =>
        string.IsNullOrWhiteSpace(topic)
            ? "omp-history"
            : topic.Trim().Trim('/');

    private static string SanitizeHeaderAscii(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "OMP History";
        }

        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (ch >= 32 && ch <= 126)
            {
                sb.Append(ch);
            }
            else if (ch == '\r' || ch == '\n')
            {
                sb.Append(' ');
            }
        }
        var res = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(res) ? "OMP History" : res;
    }

    private static string TruncateSingleLine(string? text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var firstLine = text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

        return firstLine.Length <= maxLen ? firstLine : firstLine[..maxLen] + "…";
    }

    private static string TruncateMultiLine(string? text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = text.Replace("\r\n", "\n").Trim();
        return cleaned.Length <= maxLen ? cleaned : cleaned[..maxLen] + "…";
    }
}
