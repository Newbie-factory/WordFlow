using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;

namespace WordFlow.App.ViewModels;

public sealed record LearningDayCellViewModel(
    DateOnly Day,
    string StatusText,
    string AutomationName,
    string Tooltip,
    string FillKey,
    bool IsToday);

public sealed class LearningHistoryViewModel : INotifyPropertyChanged
{
    internal const int HistoryDayCount = 183;
    private readonly IDailyQueueStore store;
    private readonly TimeProvider clock;
    private readonly SynchronizationContext? context;
    private int slashedWords;
    private int totalWords;
    private double goalProgressRatio;
    private long loadGeneration;

    public LearningHistoryViewModel(IDailyQueueStore store, TimeProvider? clock = null, SynchronizationContext? context = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? TimeProvider.System;
        this.context = context;
    }

    public ObservableCollection<LearningDayCellViewModel> Days { get; } = [];
    public int SlashedWords { get => slashedWords; private set => Set(ref slashedWords, value); }
    public int TotalWords { get => totalWords; private set => Set(ref totalWords, value); }
    public double GoalProgressRatio { get => goalProgressRatio; private set => Set(ref goalProgressRatio, value); }
    public string GoalProgressText => $"{SlashedWords} / {TotalWords}";
    public string GoalProgressPercentText => GoalProgressRatio.ToString("P2", CultureInfo.CurrentCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadAsync(CancellationToken ct)
    {
        long generation = Interlocked.Increment(ref loadGeneration);
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var goalTask = store.GetGoalProgressAsync(ct);
        var historyTask = store.GetHistoryAsync(today, HistoryDayCount, ct);
        await Task.WhenAll(goalTask, historyTask).ConfigureAwait(false);

        var goal = await goalTask.ConfigureAwait(false);
        var historyByDay = (await historyTask.ConfigureAwait(false)).ToDictionary(entry => entry.LocalDay);
        var cells = new List<LearningDayCellViewModel>(HistoryDayCount);
        for (var offset = HistoryDayCount - 1; offset >= 0; offset--)
        {
            var day = today.AddDays(-offset);
            cells.Add(CreateCell(day, historyByDay.GetValueOrDefault(day), today));
        }

        await PublishIfCurrentAsync(generation, () =>
        {
            SlashedWords = goal.SlashedWords;
            TotalWords = goal.TotalWords;
            GoalProgressRatio = goal.Ratio;
            Raise(nameof(GoalProgressText));
            Raise(nameof(GoalProgressPercentText));
            Days.Clear();
            foreach (var cell in cells) Days.Add(cell);
        }).ConfigureAwait(false);
    }

    private static LearningDayCellViewModel CreateCell(DateOnly day, DailyHistoryEntry? entry, DateOnly today)
    {
        var (status, fillKey) = entry switch
        {
            { WasCompleted: true } => ("已完成", "Completed"),
            { WasStarted: true } => ("未完成", "Incomplete"),
            _ => ("未开始", "Absent"),
        };
        string dayText = day.ToString("yyyy年M月d日", CultureInfo.CurrentCulture);
        string detail = entry is null or { WasStarted: false }
            ? "当天未创建学习计划"
            : $"新词 {entry.NewCompleted}/{entry.NewTarget}，复习 {entry.ReviewCompleted}/{entry.ReviewTarget}，待重学 {entry.PendingRelearning}";
        string tooltip = $"{dayText}：{status}（{detail}）";
        return new LearningDayCellViewModel(day, status, $"{dayText}，{status}，{detail}", tooltip, fillKey, day == today);
    }

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private Task PublishIfCurrentAsync(long generation, Action action)
    {
        if (generation != Volatile.Read(ref loadGeneration)) return Task.CompletedTask;
        if (context is null || context == SynchronizationContext.Current)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try
            {
                if (generation == Volatile.Read(ref loadGeneration)) action();
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        }, null);
        return completion.Task;
    }
}
