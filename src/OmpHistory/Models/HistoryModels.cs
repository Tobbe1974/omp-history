namespace OmpHistory.Models;

public sealed record RepoSummary(
    string Repo,
    int SessionCount,
    string LastUpdatedAt,
    string? RepoPath = null);

public sealed record SessionRecord(
    string SessionId,
    string Repo,
    string Name,
    string CreatedAt,
    string UpdatedAt,
    int ConversationCount);

public sealed record ConversationRecord(
    long Id,
    string SessionId,
    string Prompt,
    string ResponseMd,
    string CreatedAt);

public sealed record CreateSessionRequest(
    string SessionId,
    string Repo,
    string Name,
    string? RepoPath = null);

public sealed record RenameSessionRequest(
    string Name,
    string? Repo = null,
    string? RepoPath = null);

public sealed record CreateConversationRequest(
    string SessionId,
    string Prompt,
    string ResponseMd,
    string? Repo = null,
    string? SessionName = null,
    string? RepoPath = null);
public sealed record FeedbackItem(
    string Id,
    string SessionId,
    long? ConversationId,
    string? QuotedText,
    string Comment,
    string FormattedMessage,
    string CreatedAt);

public sealed record CreateFeedbackRequest(
    string Comment,
    string? QuotedText = null,
    long? ConversationId = null);

public sealed record ActiveSessionHeartbeatRequest(
    string? Repo = null,
    string? Name = null,
    string? RepoPath = null);

public sealed record GitCommitInfo(
    string Hash,
    string ShortHash,
    string Subject,
    string Author,
    string RelativeDate);

public sealed record GitFileChange(
    string Status,
    string Path,
    int Additions,
    int Deletions);

public sealed record GitRepoOverview(
    bool IsAvailable,
    string? RepoPath,
    string? CurrentBranch,
    string? HeadCommit,
    int UncommittedFileCount,
    IReadOnlyList<string> Branches,
    IReadOnlyList<GitCommitInfo> RecentCommits,
    string? Error = null);

public sealed record GitDiffResult(
    bool IsAvailable,
    string Mode,
    string Title,
    string? RepoPath,
    string RawDiff,
    int TotalAdditions,
    int TotalDeletions,
    IReadOnlyList<GitFileChange> ChangedFiles,
    string? Error = null);
