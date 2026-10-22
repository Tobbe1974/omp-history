namespace OmpHistory.Services;

public sealed record ConversationAddedEvent(
    string Repo,
    string SessionId,
    long ConversationId);

public sealed class HistoryNotifier
{
    public event Action? OnChanged;
    public event Action<ConversationAddedEvent>? OnConversationAdded;

    public void NotifyChanged()
    {
        OnChanged?.Invoke();
    }

    public void NotifyConversationAdded(string repo, string sessionId, long conversationId)
    {
        OnConversationAdded?.Invoke(new ConversationAddedEvent(repo, sessionId, conversationId));
        OnChanged?.Invoke();
    }
}
