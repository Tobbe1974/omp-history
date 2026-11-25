using System.Collections.Concurrent;
using OmpHistory.Models;

namespace OmpHistory.Services;

public sealed class ActiveSessionTracker
{
    private static readonly TimeSpan ActiveTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProcCacheDuration = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _heartbeats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionRepos = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<FeedbackItem>> _pendingFeedback = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _pendingResumeBySession = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _pendingResumeByRepo = new(StringComparer.OrdinalIgnoreCase);
    private readonly HistoryNotifier _notifier;
    private readonly object _procCacheLock = new();
    private DateTimeOffset _lastProcScan = DateTimeOffset.MinValue;
    private HashSet<string> _cachedProcSessionIds = new(StringComparer.Ordinal);
    private CopilotQuotaSnapshot? _latestCopilotQuota;

    public ActiveSessionTracker(HistoryNotifier notifier)
    {
        _notifier = notifier;
    }

    public CopilotQuotaSnapshot? LatestCopilotQuota => _latestCopilotQuota;

    public void UpdateCopilotQuota(CopilotQuotaSnapshot? quota)
    {
        if (quota is null || quota.Entitlement <= 0)
        {
            return;
        }

        var stamped = string.IsNullOrWhiteSpace(quota.UpdatedAt)
            ? quota with { UpdatedAt = DateTime.UtcNow.ToString("O") }
            : quota;
        var prev = _latestCopilotQuota;
        _latestCopilotQuota = stamped;

        if (prev is null
            || Math.Abs(prev.CreditsUsed - stamped.CreditsUsed) >= 0.1
            || Math.Abs(prev.Remaining - stamped.Remaining) >= 0.1)
        {
            _notifier.NotifyChanged();
        }
    }

    public void RecordHeartbeat(string sessionId, string? repo = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var id = sessionId.Trim();
        var now = DateTimeOffset.UtcNow;
        var wasActive = IsSessionActive(id, now);
        _heartbeats[id] = now;
        if (!string.IsNullOrWhiteSpace(repo))
        {
            _sessionRepos[id] = repo.Trim();
        }

        if (!wasActive)
        {
            _notifier.NotifyChanged();
        }
    }

    public bool IsSessionActive(string? sessionId) => IsSessionActive(sessionId, DateTimeOffset.UtcNow);

    private bool IsSessionActive(string? sessionId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var trimmed = sessionId.Trim();
        if (_heartbeats.TryGetValue(trimmed, out var lastSeen) && (now - lastSeen) <= ActiveTimeout)
        {
            return true;
        }

        return GetProcActiveSessionIds(now).Contains(trimmed);
    }

    public IReadOnlySet<string> GetActiveSessionIds()
    {
        var now = DateTimeOffset.UtcNow;
        var active = new HashSet<string>(GetProcActiveSessionIds(now), StringComparer.Ordinal);
        foreach (var kvp in _heartbeats)
        {
            if ((now - kvp.Value) <= ActiveTimeout)
            {
                active.Add(kvp.Key);
            }
        }
        return active;
    }

    private HashSet<string> GetProcActiveSessionIds(DateTimeOffset now)
    {
        lock (_procCacheLock)
        {
            if ((now - _lastProcScan) <= ProcCacheDuration)
            {
                return _cachedProcSessionIds;
            }

            var found = ScanProcForActiveOmpSessions();
            _cachedProcSessionIds = found;
            _lastProcScan = now;
            return _cachedProcSessionIds;
        }
    }

    private static HashSet<string> ScanProcForActiveOmpSessions()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists("/proc"))
        {
            return result;
        }

        try
        {
            foreach (var procDir in Directory.EnumerateDirectories("/proc"))
            {
                var pidStr = Path.GetFileName(procDir);
                if (string.IsNullOrEmpty(pidStr) || !char.IsAsciiDigit(pidStr[0]))
                {
                    continue;
                }

                try
                {
                    var cmdlinePath = Path.Combine(procDir, "cmdline");
                    if (!File.Exists(cmdlinePath))
                    {
                        continue;
                    }

                    var cmdBytes = File.ReadAllBytes(cmdlinePath);
                    if (cmdBytes.Length == 0)
                    {
                        continue;
                    }

                    var args = System.Text.Encoding.UTF8.GetString(cmdBytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    if (args.Length == 0)
                    {
                        continue;
                    }

                    var exeName = Path.GetFileName(args[0]);
                    if (!string.Equals(exeName, "omp", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (args.Any(a => a.StartsWith("__omp_worker", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    // 1. Prefer open session .jsonl file descriptor (reflects /new or switched sessions)
                    string? sessionFromFd = null;
                    var fdDir = Path.Combine(procDir, "fd");
                    if (Directory.Exists(fdDir))
                    {
                        foreach (var fdPath in Directory.EnumerateFileSystemEntries(fdDir))
                        {
                            try
                            {
                                var target = File.ResolveLinkTarget(fdPath, returnFinalTarget: false)?.FullName;
                                var extracted = TryExtractSessionIdFromPath(target);
                                if (!string.IsNullOrEmpty(extracted))
                                {
                                    sessionFromFd = extracted;
                                }
                            }
                            catch
                            {
                                // ignore inaccessible fd
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(sessionFromFd))
                    {
                        result.Add(sessionFromFd);
                        continue;
                    }

                    // 2. Fallback to --resume=<path> argument on cmdline
                    foreach (var arg in args)
                    {
                        if (arg.StartsWith("--resume=", StringComparison.Ordinal))
                        {
                            var extracted = TryExtractSessionIdFromPath(arg["--resume=".Length..]);
                            if (!string.IsNullOrEmpty(extracted))
                            {
                                result.Add(extracted);
                            }
                        }
                    }
                }
                catch
                {
                    // ignore processes that exited or belong to another user
                }
            }
        }
        catch
        {
            // ignore top-level /proc enumeration errors
        }

        return result;
    }

    private static string? TryExtractSessionIdFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            || !path.Contains("/sessions/", StringComparison.Ordinal))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(path);
        var underscoreIdx = fileName.LastIndexOf('_');
        var candidate = underscoreIdx >= 0 ? fileName[(underscoreIdx + 1)..] : fileName;
        return candidate.Length >= 8 ? candidate : null;
    }


    public FeedbackItem EnqueueFeedback(
        string sessionId,
        string comment,
        string? quotedText,
        long? conversationId,
        string? locationContext = null)
    {
        var trimmedSessionId = sessionId.Trim();
        var trimmedComment = comment.Trim();
        var trimmedQuote = string.IsNullOrWhiteSpace(quotedText) ? null : quotedText.Trim();
        var trimmedLocation = string.IsNullOrWhiteSpace(locationContext) ? null : locationContext.Trim();

        var formattedMessage = BuildFormattedMessage(trimmedComment, trimmedQuote, trimmedLocation);
        var item = new FeedbackItem(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: trimmedSessionId,
            ConversationId: conversationId,
            QuotedText: trimmedQuote,
            Comment: trimmedComment,
            FormattedMessage: formattedMessage,
            CreatedAt: DateTime.UtcNow.ToString("O"),
            LocationContext: trimmedLocation);

        var queue = _pendingFeedback.GetOrAdd(trimmedSessionId, _ => new ConcurrentQueue<FeedbackItem>());
        queue.Enqueue(item);
        _notifier.NotifyChanged();
        return item;
    }

    public FeedbackItem EnqueueFeedbackBatch(
        string sessionId,
        IReadOnlyList<FeedbackDraftItem> drafts)
    {
        var trimmedSessionId = sessionId.Trim();
        var formattedMessage = BuildBatchFormattedMessage(drafts);
        var first = drafts.Count > 0 ? drafts[0] : null;

        var item = new FeedbackItem(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: trimmedSessionId,
            ConversationId: first?.ConversationId,
            QuotedText: first?.QuotedText,
            Comment: formattedMessage,
            FormattedMessage: formattedMessage,
            CreatedAt: DateTime.UtcNow.ToString("O"),
            LocationContext: first?.LocationContext);

        var queue = _pendingFeedback.GetOrAdd(trimmedSessionId, _ => new ConcurrentQueue<FeedbackItem>());
        queue.Enqueue(item);
        _notifier.NotifyChanged();
        return item;
    }

    public FeedbackItem EnqueueSuggestionChange(string sessionId, SuggestionRecord suggestion)
    {
        var trimmedSessionId = sessionId.Trim();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"### Change suggestion: {suggestion.Title}");
        sb.AppendLine();
        sb.AppendLine("How would we fix this? Please review the suggestion below and propose a fix before making changes.");
        if (!string.IsNullOrWhiteSpace(suggestion.FilePaths))
        {
            sb.AppendLine();
            sb.AppendLine($"**Files:** `{suggestion.FilePaths.Trim()}`");
        }
        if (!string.IsNullOrWhiteSpace(suggestion.HumanSummary))
        {
            sb.AppendLine();
            sb.AppendLine("#### Summary");
            sb.AppendLine(suggestion.HumanSummary.Trim());
        }
        if (!string.IsNullOrWhiteSpace(suggestion.AgentContext))
        {
            sb.AppendLine();
            sb.AppendLine("#### Technical Context");
            sb.AppendLine(suggestion.AgentContext.Trim());
        }

        var formattedMessage = sb.ToString().Trim();
        var item = new FeedbackItem(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: trimmedSessionId,
            ConversationId: suggestion.ConversationId,
            QuotedText: null,
            Comment: formattedMessage,
            FormattedMessage: formattedMessage,
            CreatedAt: DateTime.UtcNow.ToString("O"),
            LocationContext: suggestion.FilePaths);

        var queue = _pendingFeedback.GetOrAdd(trimmedSessionId, _ => new ConcurrentQueue<FeedbackItem>());
        queue.Enqueue(item);
        _notifier.NotifyChanged();
        return item;
    }

    public IReadOnlyList<FeedbackItem> DequeuePendingFeedback(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        var trimmedSessionId = sessionId.Trim();
        RecordHeartbeat(trimmedSessionId);

        if (!_pendingFeedback.TryGetValue(trimmedSessionId, out var queue))
        {
            return [];
        }

        var list = new List<FeedbackItem>();
        while (queue.TryDequeue(out var item))
        {
            list.Add(item);
        }

        if (list.Count > 0)
        {
            _notifier.NotifyChanged();
        }

        return list;
    }

    public bool RequestResumeSession(string targetSessionId, string? repo = null)
    {
        if (string.IsNullOrWhiteSpace(targetSessionId))
        {
            return false;
        }

        var target = targetSessionId.Trim();
        var now = DateTimeOffset.UtcNow;
        string? chosenActiveId = null;

        if (!string.IsNullOrWhiteSpace(repo))
        {
            var trimmedRepo = repo.Trim();
            foreach (var kvp in _heartbeats.OrderByDescending(x => x.Value))
            {
                if ((now - kvp.Value) <= ActiveTimeout
                    && _sessionRepos.TryGetValue(kvp.Key, out var activeRepo)
                    && string.Equals(activeRepo, trimmedRepo, StringComparison.OrdinalIgnoreCase))
                {
                    chosenActiveId = kvp.Key;
                    break;
                }
            }
        }

        if (chosenActiveId is null)
        {
            var latest = _heartbeats
                .Where(kvp => (now - kvp.Value) <= ActiveTimeout)
                .OrderByDescending(kvp => kvp.Value)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(latest.Key))
            {
                chosenActiveId = latest.Key;
            }
        }

        if (chosenActiveId is not null)
        {
            _pendingResumeBySession[chosenActiveId] = target;
            _notifier.NotifyChanged();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(repo))
        {
            _pendingResumeByRepo[repo.Trim()] = target;
        }
        _pendingResumeByRepo["*"] = target;
        _notifier.NotifyChanged();
        return false;
    }

    public string? DequeuePendingResume(string sessionId, string? repo = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var id = sessionId.Trim();
        if (_pendingResumeBySession.TryRemove(id, out var target))
        {
            return target;
        }

        if (!string.IsNullOrWhiteSpace(repo) && _pendingResumeByRepo.TryRemove(repo.Trim(), out var repoTarget))
        {
            _pendingResumeByRepo.TryRemove("*", out _);
            return repoTarget;
        }

        if (_pendingResumeByRepo.TryRemove("*", out var wildcardTarget))
        {
            return wildcardTarget;
        }

        return null;
    }

    public static string BuildFormattedMessage(string comment, string? quotedText, string? locationContext = null)
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(locationContext))
        {
            parts.Add($"**[{locationContext.Trim()}]**");
        }

        if (!string.IsNullOrWhiteSpace(quotedText))
        {
            var quoteLines = quotedText
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(line => $"> {line}");
            parts.Add(string.Join("\n", quoteLines));
        }

        parts.Add(comment.Trim());
        return string.Join("\n\n", parts);
    }

    public static string BuildBatchFormattedMessage(IReadOnlyList<FeedbackDraftItem> drafts)
    {
        if (drafts.Count == 0)
        {
            return string.Empty;
        }

        if (drafts.Count == 1)
        {
            var single = drafts[0];
            var loc = BuildItemLocationHeader(single);
            return BuildFormattedMessage(single.Comment, single.QuotedText, loc);
        }

        var blocks = new List<string>(drafts.Count + 1)
        {
            $"### Feedback ({drafts.Count} items)"
        };

        for (var i = 0; i < drafts.Count; i++)
        {
            var d = drafts[i];
            var header = BuildItemLocationHeader(d);
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(header))
            {
                sb.AppendLine($"#### {i + 1}. [{header}]");
            }
            else
            {
                sb.AppendLine($"#### {i + 1}. Comment");
            }

            if (!string.IsNullOrWhiteSpace(d.QuotedText))
            {
                var quoteLines = d.QuotedText
                    .Replace("\r\n", "\n")
                    .Split('\n')
                    .Select(line => $"> {line}");
                sb.AppendLine(string.Join("\n", quoteLines));
                sb.AppendLine();
            }

            sb.Append(d.Comment.Trim());
            blocks.Add(sb.ToString());
        }

        return string.Join("\n\n---\n\n", blocks);
    }

    private static string? BuildItemLocationHeader(FeedbackDraftItem d)
    {
        var hasTurn = d.TurnNumber is > 0;
        var hasLoc = !string.IsNullOrWhiteSpace(d.LocationContext);
        if (hasTurn && hasLoc)
        {
            if (d.LocationContext!.StartsWith("Turn #", StringComparison.OrdinalIgnoreCase))
            {
                return d.LocationContext.Trim();
            }
            return $"Turn #{d.TurnNumber} • {d.LocationContext.Trim()}";
        }
        if (hasLoc)
        {
            return d.LocationContext!.Trim();
        }
        if (hasTurn)
        {
            return $"Turn #{d.TurnNumber}";
        }
        return null;
    }
}
