namespace OmpHistory.Services;

public sealed class HistoryNotifier
{
    public event Action? OnChanged;

    public void NotifyChanged()
    {
        OnChanged?.Invoke();
    }
}
