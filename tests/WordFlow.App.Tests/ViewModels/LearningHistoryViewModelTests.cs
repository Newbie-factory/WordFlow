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
        var viewModel = new LearningHistoryViewModel(store, new FixedTimeProvider(today));

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

        var incomplete = Assert.Single(viewModel.Days, x => x.Day == today.AddDays(-1));
        Assert.Equal("未完成", incomplete.StatusText);
        Assert.Equal("Incomplete", incomplete.FillKey);

        var absent = Assert.Single(viewModel.Days, x => x.Day == today.AddDays(-3));
        Assert.Equal("未开始", absent.StatusText);
        Assert.Equal("Absent", absent.FillKey);
        Assert.True(absent.IsToday is false);
    }

    private sealed class FixedTimeProvider(DateOnly day) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
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
}
