namespace WordFlow.App.ViewModels;

public interface IIdleWakeTickSource : IDisposable
{
    event EventHandler? Tick;
    void Start(TimeSpan interval);
    void Stop();
}

public sealed class IdleWakeController : IDisposable
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromDays(1);
    private readonly IIdleWakeTickSource ticks;
    private readonly TimeProvider timeProvider;
    private DateTimeOffset? dueAtUtc;
    private bool visible = true;
    private bool paused;
    private bool disposed;

    public IdleWakeController(IIdleWakeTickSource ticks, TimeProvider timeProvider)
    {
        this.ticks = ticks ?? throw new ArgumentNullException(nameof(ticks));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ticks.Tick += OnTick;
    }

    public event EventHandler? Wake;

    public bool Visible
    {
        get => visible;
        set
        {
            if (disposed || visible == value) return;
            visible = value;
            ArmOrStop();
        }
    }

    public bool Paused
    {
        get => paused;
        set
        {
            if (disposed || paused == value) return;
            paused = value;
            ArmOrStop();
        }
    }

    public void Schedule(DateTimeOffset? dueAt)
    {
        if (disposed) return;
        dueAtUtc = dueAt?.ToUniversalTime();
        ArmOrStop();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        dueAtUtc = null;
        ticks.Tick -= OnTick;
        ticks.Dispose();
        Wake = null;
    }

    private void OnTick(object? sender, EventArgs args)
    {
        if (disposed || paused || !visible || dueAtUtc is not { } due)
        {
            ticks.Stop();
            return;
        }
        if (timeProvider.GetUtcNow() < due)
        {
            ArmOrStop();
            return;
        }

        dueAtUtc = null;
        ticks.Stop();
        Wake?.Invoke(this, EventArgs.Empty);
    }

    private void ArmOrStop()
    {
        if (disposed || paused || !visible || dueAtUtc is not { } due)
        {
            ticks.Stop();
            return;
        }

        var remaining = due - timeProvider.GetUtcNow();
        ticks.Start(remaining <= TimeSpan.Zero
            ? MinimumInterval
            : remaining > MaximumInterval ? MaximumInterval : remaining);
    }
}
