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
    int ConversationCount,
    double? TotalCredits = null,
    double? TotalCostUsd = null,
    long? TotalTokens = null);

public sealed record ConversationRecord(
    long Id,
    string SessionId,
    string Prompt,
    string ResponseMd,
    string CreatedAt,
    string? DiffPatch = null,
    string? GitTreeBefore = null,
    string? GitTreeAfter = null,
    double? CreditsUsed = null,
    double? CostUsd = null,
    long? TotalTokens = null,
    string? Model = null);
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
    string? RepoPath = null,
    string? DiffPatch = null,
    string? GitTreeBefore = null,
    string? GitTreeAfter = null,
    double? CreditsUsed = null,
    double? CostUsd = null,
    long? TotalTokens = null,
    string? Model = null,
    CopilotQuotaSnapshot? CopilotQuota = null);
public sealed record FeedbackItem(
    string Id,
    string SessionId,
    long? ConversationId,
    string? QuotedText,
    string Comment,
    string FormattedMessage,
    string CreatedAt,
    string? LocationContext = null);

public sealed record CreateFeedbackRequest(
    string Comment,
    string? QuotedText = null,
    long? ConversationId = null,
    string? LocationContext = null);

public sealed record FeedbackDraftItem(
    string Id,
    long? ConversationId,
    int? TurnNumber,
    string? LocationContext,
    string? QuotedText,
    string Comment);

public sealed record CopilotQuotaSnapshot(
    double CreditsUsed,
    double Remaining,
    double Entitlement,
    double PercentRemaining,
    string? ResetDate = null,
    string? Plan = null,
    string? UpdatedAt = null);

public sealed record ActiveSessionHeartbeatRequest(
    string? Repo = null,
    string? Name = null,
    string? RepoPath = null,
    CopilotQuotaSnapshot? CopilotQuota = null);


public sealed record AppSettingsRecord(
    bool AutoSelectNewConversation,
    bool PinLists,
    bool NtfyEnabled,
    string NtfyServerUrl,
    string NtfyTopic);

public sealed record TurnDetailsRecord(
    ConversationRecord Conversation,
    SessionRecord Session,
    int TurnNumber,
    int TotalTurns);
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
