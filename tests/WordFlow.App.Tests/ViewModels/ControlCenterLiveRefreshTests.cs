using WordFlow.App.Bootstrap;
using WordFlow.App.ViewModels;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;
using WordFlow.Domain.Learning;
using WordFlow.Infrastructure.Audio;
using WordFlow.Infrastructure.Data;

namespace WordFlow.App.Tests.ViewModels;

public sealed class ControlCenterLiveRefreshTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wordflow-live-{Guid.NewGuid():N}");

    [Fact]
    public async Task Committed_notifications_refresh_open_total_goal_and_history_after_slash_and_undo()
    {
        Directory.CreateDirectory(directory);
        var paths = new AppPaths(directory, Path.Combine(directory, "bundled"));
        paths.Initialize();
        var factory = new SqliteConnectionFactory(paths.UserDatabasePath);
        await new MigrationRunner(factory).MigrateAsync(default);
        var settingsStore = new SqliteAppSettingStore(factory);
        var progress = new MutableProgressReader();
        var history = new MutableDailyQueueStore();
        var notifier = new LearningDataChangeNotifier();
        using var pronunciationService = new FakePronunciationService();
        using var viewModel = new ControlCenterViewModel(
            new DailyPlanSettingsViewModel(settingsStore),
            new ShortcutSettingsViewModel(new FakeShortcutService()),
            new PronunciationSettingsViewModel(pronunciationService, settingsStore),
            new ThemeSettingsViewModel(new ImageThemeService(paths, settingsStore)),
            progress,
            dailyQueueStore: history,
            clock: new FixedTimeProvider(new DateTimeOffset(2026, 8, 22, 8, 0, 0, TimeSpan.Zero)),
            dataChanges: notifier);
        await viewModel.LoadAsync();

        progress.Slashed = history.Slashed = 1;
        progress.Reviewed = 1;
        history.Completed = 1;
        notifier.PublishCommitted();
        await WaitUntilAsync(() => viewModel.SlashedTotal == 1 &&
            viewModel.ReviewedToday == 1 && viewModel.LearningHistory?.SlashedWords == 1);

        progress.Slashed = history.Slashed = 0;
        progress.Reviewed = 0;
        history.Completed = 0;
        notifier.PublishCommitted();
        await WaitUntilAsync(() => viewModel.SlashedTotal == 0 &&
            viewModel.ReviewedToday == 0 && viewModel.LearningHistory?.SlashedWords == 0);

        viewModel.Dispose();
        progress.Slashed = history.Slashed = 2;
        notifier.PublishCommitted();
        await Task.Delay(150);
        Assert.Equal(0, viewModel.SlashedTotal);
        Assert.Equal(0, viewModel.LearningHistory?.SlashedWords);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException("The live dashboard did not refresh.");
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class MutableProgressReader : ILearningProgressReader
    {
        public int Reviewed { get; set; }
        public int Slashed { get; set; }
        public Task<DailyLearningStatistics> GetDailyStatisticsAsync(DateOnly day, CancellationToken ct) =>
            Task.FromResult(new DailyLearningStatistics(Reviewed, Slashed));
    }

    private sealed class MutableDailyQueueStore : IDailyQueueStore
    {
        public int Slashed { get; set; }
        public int Completed { get; set; }
        public Task<DailySessionSnapshot?> GetAsync(DateOnly localDay, CancellationToken ct) => Task.FromResult<DailySessionSnapshot?>(null);
        public Task<DailySessionSnapshot> GetOrCreateAsync(DailyQueueSeed seed, CancellationToken ct) => throw new NotSupportedException();
        public Task<DailyQueueItem?> GetNextPendingAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task EnsureDueRelearningAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<DateTimeOffset?> GetNextRelearningDueAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DailyHistoryEntry>> GetHistoryAsync(DateOnly throughDay, int dayCount, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DailyHistoryEntry>>(
                [new(throughDay, Completed, 1, 0, 0, 0, true, Completed == 1)]);
        public Task<GoalProgress> GetGoalProgressAsync(CancellationToken ct) => Task.FromResult(new GoalProgress(Slashed, 100));
    }

    private sealed class FakePronunciationService : IPronunciationService
    {
        public IReadOnlyList<PronunciationVoice> Voices { get; } = [new("test", "Test", "en-US", PronunciationAccent.American)];
        public PronunciationAvailability Availability { get; } = new(true, "可用", PronunciationInventoryState.AuthoritativeAvailable);
        public PronunciationVoice? SelectVoice(string? voiceId, PronunciationAccent preference) => Voices[0];
        public Task<PronunciationPlaybackResult> SpeakAsync(string text, string voiceId, int rate, int volume, CancellationToken ct) =>
            Task.FromResult(PronunciationPlaybackResult.Completed(text));
        public Task CancelAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeShortcutService : IShortcutService
    {
        public IReadOnlyDictionary<ShortcutAction, ShortcutBinding> Bindings { get; } = ShortcutDefaults.All;
        public IReadOnlyList<ShortcutRestoreIssue> RestoreIssues => [];
        public ShortcutLifecycleSnapshot Lifecycle => new(ShortcutLifecycleState.Ready);
        public event EventHandler<ShortcutAction>? ActionInvoked { add { } remove { } }
        public event EventHandler<ShortcutBindingChangedEventArgs>? BindingChanged { add { } remove { } }
        public event EventHandler<ShortcutCallbackFaultedEventArgs>? CallbackFaulted { add { } remove { } }
        public ShortcutRegistrationResult TryReplace(ShortcutAction action, ShortcutBinding candidate) => ShortcutRegistrationResult.Success(candidate);
        public ShortcutRegistrationResult ResetAll() => ShortcutRegistrationResult.Success();
        public ShortcutRestoreResult RestorePersisted() => new([]);
        public ShortcutRegistrationResult AttachWindowHandle(nint handle) => ShortcutRegistrationResult.Success();
        public bool ProcessWindowMessage(int message, nint id, nint chordData) => false;
        public void Dispose() { }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
