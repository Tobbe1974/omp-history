using System.Collections.Concurrent;
using OmpHistory.Models;

namespace OmpHistory.Services;

public sealed class ActiveSessionTracker
{
    private static readonly TimeSpan ActiveTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProcCacheDuration = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _heartbeats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<FeedbackItem>> _pendingFeedback = new(StringComparer.Ordinal);
    private readonly HistoryNotifier _notifier;
    private readonly object _procCacheLock = new();
    private DateTimeOffset _lastProcScan = DateTimeOffset.MinValue;
    private HashSet<string> _cachedProcSessionIds = new(StringComparer.Ordinal);

    public ActiveSessionTracker(HistoryNotifier notifier)
    {
        _notifier = notifier;
    }

    public void RecordHeartbeat(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var id = sessionId.Trim();
        var now = DateTimeOffset.UtcNow;
        var wasActive = IsSessionActive(id, now);
        _heartbeats[id] = now;

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


    public FeedbackItem EnqueueFeedback(string sessionId, string comment, string? quotedText, long? conversationId)
    {
        var trimmedSessionId = sessionId.Trim();
        var trimmedComment = comment.Trim();
        var trimmedQuote = string.IsNullOrWhiteSpace(quotedText) ? null : quotedText.Trim();

        var formattedMessage = BuildFormattedMessage(trimmedComment, trimmedQuote);
        var item = new FeedbackItem(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: trimmedSessionId,
            ConversationId: conversationId,
            QuotedText: trimmedQuote,
            Comment: trimmedComment,
            FormattedMessage: formattedMessage,
            CreatedAt: DateTime.UtcNow.ToString("O"));

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

    public static string BuildFormattedMessage(string comment, string? quotedText)
    {
        if (string.IsNullOrWhiteSpace(quotedText))
        {
            return comment;
        }

        var quoteLines = quotedText
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => $"> {line}");

        return $"{string.Join("\n", quoteLines)}\n\n{comment}";
    }
}
