namespace OmpHistory.Models;

public sealed record RepoSummary(
    string Repo,
    int SessionCount,
    string LastUpdatedAt);

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
    string Name);

public sealed record RenameSessionRequest(
    string Name,
    string? Repo = null);

public sealed record CreateConversationRequest(
    string SessionId,
    string Prompt,
    string ResponseMd,
    string? Repo = null,
    string? SessionName = null);
