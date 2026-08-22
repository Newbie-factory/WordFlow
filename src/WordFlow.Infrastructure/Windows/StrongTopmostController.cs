namespace WordFlow.Infrastructure.Windows;

public interface ITopmostTickSource : IDisposable
{
    event EventHandler? Tick;
    void Start(TimeSpan interval);
    void Stop();
}

public sealed class StrongTopmostController : IDisposable
{
    public static readonly TimeSpan ReassertInterval = TimeSpan.FromMilliseconds(500);

    private readonly object sync = new();
    private readonly Func<IReadOnlyList<nint>> getHandles;
    private readonly IWindowZOrder zOrder;
    private readonly ITopmostTickSource ticks;
    private bool enabled;
    private bool visible;
    private bool disposed;

    public StrongTopmostController(nint hwnd, IWindowZOrder zOrder)
        : this(hwnd, zOrder, new ThreadingTopmostTickSource()) { }

    public StrongTopmostController(nint hwnd, IWindowZOrder zOrder, ITopmostTickSource ticks)
        : this(() => [hwnd], zOrder, ticks) { }

    public StrongTopmostController(Func<IReadOnlyList<nint>> getHandles, IWindowZOrder zOrder)
        : this(getHandles, zOrder, new ThreadingTopmostTickSource()) { }

    public StrongTopmostController(
        Func<IReadOnlyList<nint>> getHandles,
        IWindowZOrder zOrder,
        ITopmostTickSource ticks)
    {
        this.getHandles = getHandles ?? throw new ArgumentNullException(nameof(getHandles));
        this.zOrder = zOrder ?? throw new ArgumentNullException(nameof(zOrder));
        this.ticks = ticks ?? throw new ArgumentNullException(nameof(ticks));
        this.ticks.Tick += OnTick;
    }

    public bool Enabled
    {
        get { lock (sync) return enabled; }
        set
        {
            lock (sync)
            {
                if (disposed || enabled == value) return;
                enabled = value;
                UpdateTimerUnsafe();
                if (visible || !enabled) ApplyUnsafe();
            }
        }
    }

    public bool Visible
    {
        get { lock (sync) return visible; }
        set
        {
            lock (sync)
            {
                if (disposed || visible == value) return;
                visible = value;
                UpdateTimerUnsafe();
                if (visible) ApplyUnsafe();
            }
        }
    }

    public void Reassert()
    {
        lock (sync)
        {
            if (disposed || !visible) return;
            ApplyUnsafe();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            visible = false;
            ticks.Tick -= OnTick;
            ticks.Stop();
            ticks.Dispose();
        }
    }

    private void OnTick(object? sender, EventArgs args)
    {
        lock (sync)
        {
            if (disposed || !enabled || !visible) return;
            ApplyUnsafe();
        }
    }

    private void UpdateTimerUnsafe()
    {
        if (enabled && visible) ticks.Start(ReassertInterval);
        else ticks.Stop();
    }

    private void ApplyUnsafe()
    {
        IReadOnlyList<nint> handles;
        try { handles = getHandles() ?? []; }
        catch { return; }

        var seen = new HashSet<nint>();
        foreach (nint hwnd in handles)
        {
            if (hwnd == 0 || !seen.Add(hwnd)) continue;
            try
            {
                if (enabled) zOrder.SetTopmost(hwnd, noActivate: true);
                else zOrder.SetNotTopmost(hwnd, noActivate: true);
            }
            catch { }
        }
    }
}

internal sealed class ThreadingTopmostTickSource : ITopmostTickSource
{
    private readonly System.Threading.Timer timer;
    private bool disposed;

    public ThreadingTopmostTickSource() => timer = new(_ => Tick?.Invoke(this, EventArgs.Empty));

    public event EventHandler? Tick;

    public void Start(TimeSpan interval)
    {
        if (disposed) return;
        timer.Change(interval, interval);
    }

    public void Stop()
    {
        if (disposed) return;
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Dispose();
        Tick = null;
    }
}
