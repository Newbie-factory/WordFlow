namespace WordFlow.App.ViewModels;

public sealed class LearningDataChangeNotifier
{
    public event EventHandler? CommittedDataChanged;

    public void PublishCommitted()
    {
        if (CommittedDataChanged is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch { }
        }
    }
}
