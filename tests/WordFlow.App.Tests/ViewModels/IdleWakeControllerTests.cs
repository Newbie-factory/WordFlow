using WordFlow.App.ViewModels;
using WordFlow.App.Views;
using System.Reflection;

namespace WordFlow.App.Tests.ViewModels;

public sealed class IdleWakeControllerTests
{
    [Fact]
    public void Floating_window_owns_the_dispatcher_backed_idle_wake_controller()
    {
        var field = typeof(FloatingCardWindow).GetField("idleWakeController", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal(typeof(IdleWakeController), field!.FieldType);
    }

    [Fact]
    public void Absolute_due_wake_is_rescheduled_and_suppressed_while_hidden_paused_or_disposed()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero));
        var ticks = new ManualIdleWakeTickSource();
        var wakes = 0;
        var controller = new IdleWakeController(ticks, clock);
        controller.Wake += (_, _) => wakes++;
        var dueAt = clock.GetUtcNow().AddMinutes(10);

        controller.Schedule(dueAt);
        Assert.Equal(TimeSpan.FromMinutes(10), ticks.Interval);
        controller.Visible = false;
        Assert.False(ticks.IsRunning);
        controller.Visible = true;
        Assert.True(ticks.IsRunning);
        controller.Paused = true;
        Assert.False(ticks.IsRunning);
        controller.Paused = false;
        clock.UtcNow = dueAt.AddSeconds(1);
        ticks.Fire();

        Assert.Equal(1, wakes);
        Assert.False(ticks.IsRunning);
        controller.Dispose();
        controller.Schedule(clock.GetUtcNow());
        ticks.Fire();
        Assert.Equal(1, wakes);
        Assert.True(ticks.IsDisposed);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class ManualIdleWakeTickSource : IIdleWakeTickSource
    {
        public event EventHandler? Tick;
        public TimeSpan Interval { get; private set; }
        public bool IsRunning { get; private set; }
        public bool IsDisposed { get; private set; }
        public void Start(TimeSpan interval) { Interval = interval; IsRunning = true; }
        public void Stop() => IsRunning = false;
        public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
        public void Dispose() { IsDisposed = true; IsRunning = false; Tick = null; }
    }
}
