using WordFlow.App.ViewModels;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;

namespace WordFlow.App.Tests.ViewModels;

public sealed class LearningHistoryViewModelTests
{
    [Fact]
    public async Task Load_exposes_slash_ratio_and_exactly_183_accessible_day_cells()
    {
        var today = new DateOnly(2026, 8, 22);
        var completedDay = today.AddDays(-2);
        var store = new StubDailyQueueStore(
            new GoalProgress(3, 12046),
            [
                new DailyHistoryEntry(completedDay, 10, 10, 20, 20, 0, true, true),
                new DailyHistoryEntry(today.AddDays(-1), 2, 10, 3, 20, 0, true, false),
            ]);
        var viewModel = new LearningHistoryViewModel(store, new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 21, 16, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.CreateCustomTimeZone("UTC+08", TimeSpan.FromHours(8), "UTC+08", "UTC+08")));

        await viewModel.LoadAsync(default);

        Assert.Equal("3 / 12046", viewModel.GoalProgressText);
        Assert.Equal(viewModel.GoalProgressRatio.ToString("P2", System.Globalization.CultureInfo.CurrentCulture), viewModel.GoalProgressPercentText);
        Assert.Equal(3d / 12046d, viewModel.GoalProgressRatio, 12);
        Assert.Equal(183, viewModel.Days.Count);
        Assert.Equal(183, store.RequestedDayCount);
        Assert.Equal(today, store.RequestedThroughDay);

        var completed = Assert.Single(viewModel.Days, x => x.Day == completedDay);
        Assert.Equal("已完成", completed.StatusText);
        Assert.Equal("Completed", completed.FillKey);
        Assert.Contains("已完成", completed.Tooltip);
        Assert.Contains("已完成", completed.AutomationName);
        Assert.Contains("新词 10/10", completed.Tooltip);

        var incomplete = Assert.Single(viewModel.Days, x => x.Day == today.AddDays(-1));
        Assert.Equal("未完成", incomplete.StatusText);
        Assert.Equal("Incomplete", incomplete.FillKey);
        Assert.Contains("未完成", incomplete.Tooltip);
        Assert.Contains("新词 2/10", incomplete.AutomationName);

        var absent = Assert.Single(viewModel.Days, x => x.Day == today.AddDays(-3));
        Assert.Equal("未开始", absent.StatusText);
        Assert.Equal("Absent", absent.FillKey);
        Assert.True(absent.IsToday is false);
        Assert.Contains("未开始", absent.Tooltip);
        Assert.Contains("当天未创建学习计划", absent.AutomationName);
    }

    [Fact]
    public async Task Load_keeps_newer_result_when_an_older_request_finishes_last()
    {
        var firstGoal = new TaskCompletionSource<GoalProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHistory = new TaskCompletionSource<IReadOnlyList<DailyHistoryEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SequencedDailyQueueStore(firstGoal, firstHistory);
        var viewModel = new LearningHistoryViewModel(store, new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 21, 16, 30, 0, TimeSpan.Zero), TimeZoneInfo.Utc));

        Task first = viewModel.LoadAsync(default);
        Task second = viewModel.LoadAsync(default);
        await second;
        firstGoal.SetResult(new GoalProgress(1, 100));
        firstHistory.SetResult([]);
        await first;

        Assert.Equal("9 / 100", viewModel.GoalProgressText);
    }

    [Fact]
    public void Dashboard_day_boundary_uses_explicit_local_time_provider()
    {
        var provider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 21, 16, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.CreateCustomTimeZone("UTC+08-dashboard", TimeSpan.FromHours(8), "UTC+08-dashboard", "UTC+08-dashboard"));
        Assert.Equal(new DateOnly(2026, 8, 22), ControlCenterViewModel.GetDashboardDay(provider));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => localTimeZone;
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StubDailyQueueStore(GoalProgress goal, IReadOnlyList<DailyHistoryEntry> entries) : IDailyQueueStore
    {
        public DateOnly RequestedThroughDay { get; private set; }
        public int RequestedDayCount { get; private set; }

        public Task<DailySessionSnapshot> GetOrCreateAsync(DailyQueueSeed seed, CancellationToken ct) => throw new NotSupportedException();
        public Task<DailyQueueItem?> GetNextPendingAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task EnsureDueRelearningAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<GoalProgress> GetGoalProgressAsync(CancellationToken ct) => Task.FromResult(goal);
        public Task<IReadOnlyList<DailyHistoryEntry>> GetHistoryAsync(DateOnly throughDay, int dayCount, CancellationToken ct)
        {
            RequestedThroughDay = throughDay;
            RequestedDayCount = dayCount;
            return Task.FromResult(entries);
        }
    }

    private sealed class SequencedDailyQueueStore(
        TaskCompletionSource<GoalProgress> firstGoal,
        TaskCompletionSource<IReadOnlyList<DailyHistoryEntry>> firstHistory) : IDailyQueueStore
    {
        private int calls;

        public Task<DailySessionSnapshot> GetOrCreateAsync(DailyQueueSeed seed, CancellationToken ct) => throw new NotSupportedException();
        public Task<DailyQueueItem?> GetNextPendingAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task EnsureDueRelearningAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<GoalProgress> GetGoalProgressAsync(CancellationToken ct) => Interlocked.Increment(ref calls) == 1
            ? firstGoal.Task : Task.FromResult(new GoalProgress(9, 100));
        public Task<IReadOnlyList<DailyHistoryEntry>> GetHistoryAsync(DateOnly throughDay, int dayCount, CancellationToken ct) => Volatile.Read(ref calls) == 1
            ? firstHistory.Task : Task.FromResult<IReadOnlyList<DailyHistoryEntry>>([]);
    }
}
