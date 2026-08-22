using WordFlow.Infrastructure.Windows;

namespace WordFlow.Infrastructure.Tests.Windows;

public sealed class StrongTopmostControllerTests
{
    [Fact]
    public void Native_policy_uses_only_no_move_no_size_no_activate_for_topmost_and_not_topmost()
    {
        var native = new RecordingSetWindowPosNative();
        var policy = new TopmostPolicy(native);

        policy.SetTopmost((nint)31, noActivate: true);
        policy.SetNotTopmost((nint)32, noActivate: true);

        Assert.Equal(
            [new NativeCall((nint)31, (nint)(-1), 0x0013), new NativeCall((nint)32, (nint)(-2), 0x0013)],
            native.Calls);
    }

    [Fact]
    public void Enabled_visible_controller_sets_every_current_window_topmost_without_activation()
    {
        var zOrder = new RecordingWindowZOrder();
        var ticks = new ManualTopmostTickSource();
        nint[] handles = [(nint)41, (nint)42, (nint)43];
        using var controller = new StrongTopmostController(() => handles, zOrder, ticks);

        controller.Enabled = true;
        controller.Visible = true;

        Assert.Equal([new ZOrderCall(true, (nint)41, true), new(true, (nint)42, true), new(true, (nint)43, true)], zOrder.Calls);
        Assert.True(ticks.IsRunning);
        Assert.Equal(TimeSpan.FromMilliseconds(500), ticks.Interval);
    }

    [Fact]
    public void Disabling_downgrades_every_current_window_and_stops_reassertion_ticks()
    {
        var zOrder = new RecordingWindowZOrder();
        var ticks = new ManualTopmostTickSource();
        using var controller = new StrongTopmostController(() => [(nint)51, (nint)52, (nint)53], zOrder, ticks)
        {
            Enabled = true,
            Visible = true,
        };
        zOrder.Calls.Clear();

        controller.Enabled = false;

        Assert.Equal([new ZOrderCall(false, (nint)51, true), new(false, (nint)52, true), new(false, (nint)53, true)], zOrder.Calls);
        Assert.False(ticks.IsRunning);
        ticks.Fire();
        Assert.Equal(3, zOrder.Calls.Count);
    }

    [Fact]
    public void Hidden_ticks_do_nothing_and_showing_again_reuses_one_tick_subscription()
    {
        var zOrder = new RecordingWindowZOrder();
        var ticks = new ManualTopmostTickSource();
        using var controller = new StrongTopmostController((nint)61, zOrder, ticks) { Enabled = true };
        controller.Visible = true;
        controller.Visible = false;
        zOrder.Calls.Clear();

        ticks.Fire();
        Assert.Empty(zOrder.Calls);

        controller.Visible = true;
        zOrder.Calls.Clear();
        ticks.Fire();

        Assert.Single(zOrder.Calls);
        Assert.Equal(new ZOrderCall(true, (nint)61, true), zOrder.Calls[0]);
        Assert.Equal(1, ticks.SubscriberCount);
    }

    [Fact]
    public void Repeated_visible_ticks_reassert_topmost_and_resolve_new_popup_handles()
    {
        var zOrder = new RecordingWindowZOrder();
        var ticks = new ManualTopmostTickSource();
        nint[] handles = [(nint)71];
        using var controller = new StrongTopmostController(() => handles, zOrder, ticks)
        {
            Enabled = true,
            Visible = true,
        };
        zOrder.Calls.Clear();

        ticks.Fire();
        handles = [(nint)71, (nint)72];
        ticks.Fire();

        Assert.Equal(
            [new ZOrderCall(true, (nint)71, true), new(true, (nint)71, true), new(true, (nint)72, true)],
            zOrder.Calls);
    }

    [Fact]
    public void Zero_duplicate_and_provider_failure_handles_are_ignored_without_stopping_future_ticks()
    {
        var zOrder = new RecordingWindowZOrder();
        var ticks = new ManualTopmostTickSource();
        int readCount = 0;
        using var controller = new StrongTopmostController(() => ++readCount == 2
            ? throw new InvalidOperationException("window was recreated")
            : [(nint)0, (nint)81, (nint)81], zOrder, ticks)
        {
            Enabled = true,
            Visible = true,
        };

        ticks.Fire();
        ticks.Fire();

        Assert.Equal(2, zOrder.Calls.Count);
        Assert.All(zOrder.Calls, call => Assert.Equal((nint)81, call.Handle));
    }

    [Fact]
    public void Dispose_unsubscribes_disposes_ticks_and_prevents_transition_or_manual_calls()
    {
        var zOrder = new RecordingWindowZOrder();
        var ticks = new ManualTopmostTickSource();
        var controller = new StrongTopmostController((nint)91, zOrder, ticks)
        {
            Enabled = true,
            Visible = true,
        };
        zOrder.Calls.Clear();

        controller.Dispose();
        ticks.Fire();
        controller.Reassert();
        controller.Enabled = false;
        controller.Visible = false;

        Assert.Empty(zOrder.Calls);
        Assert.True(ticks.IsDisposed);
        Assert.Equal(0, ticks.SubscriberCount);
    }

    private sealed class RecordingWindowZOrder : IWindowZOrder
    {
        public List<ZOrderCall> Calls { get; } = [];
        public void SetTopmost(nint hwnd, bool noActivate) => Calls.Add(new(true, hwnd, noActivate));
        public void SetNotTopmost(nint hwnd, bool noActivate) => Calls.Add(new(false, hwnd, noActivate));
    }

    private sealed class ManualTopmostTickSource : ITopmostTickSource
    {
        private EventHandler? tick;
        public event EventHandler? Tick { add => tick += value; remove => tick -= value; }
        public TimeSpan Interval { get; private set; }
        public bool IsRunning { get; private set; }
        public bool IsDisposed { get; private set; }
        public int SubscriberCount => tick?.GetInvocationList().Length ?? 0;
        public void Start(TimeSpan interval) { Interval = interval; IsRunning = true; }
        public void Stop() => IsRunning = false;
        public void Fire() => tick?.Invoke(this, EventArgs.Empty);
        public void Dispose() { IsDisposed = true; IsRunning = false; tick = null; }
    }

    private sealed record ZOrderCall(bool Topmost, nint Handle, bool NoActivate);

    private sealed class RecordingSetWindowPosNative : ISetWindowPosNative
    {
        public List<NativeCall> Calls { get; } = [];

        public bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags)
        {
            Assert.Equal(0, x);
            Assert.Equal(0, y);
            Assert.Equal(0, width);
            Assert.Equal(0, height);
            Calls.Add(new(hwnd, insertAfter, flags));
            return true;
        }
    }

    private sealed record NativeCall(nint Handle, nint InsertAfter, uint Flags);
}
