using System.ComponentModel;
using System.IO;
using WordFlow.App.Bootstrap;

namespace WordFlow.App.ViewModels;

public sealed class ThemeSettingsViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(250);
    private readonly ImageThemeService service;
    private readonly Func<ImageTheme, CancellationToken, Task> saveAsync;
    private readonly Func<CancellationToken, Task<ImageTheme>> restoreAsync;
    private readonly Func<string, double, CancellationToken, Task<ImageTheme>> importAsync;
    private readonly Func<CancellationToken, Task> resetAsync;
    private readonly TimeSpan debounceInterval;
    private readonly SynchronizationContext? ownerContext;
    private readonly object persistenceSync = new();
    private readonly SemaphoreSlim persistenceGate = new(1, 1);
    private ImageTheme current = ImageTheme.Default;
    private CancellationTokenSource? debounceCancellation;
    private Task durableTail = Task.CompletedTask;
    private long revision;
    private long persistedRevision;

    public ThemeSettingsViewModel(ImageThemeService service)
        : this(service, (theme, ct) => service.SaveAsync(theme, ct), DefaultDebounceInterval)
    {
    }

    internal ThemeSettingsViewModel(
        ImageThemeService service,
        Func<ImageTheme, CancellationToken, Task> saveAsync,
        TimeSpan debounceInterval,
        Func<CancellationToken, Task<ImageTheme>>? restoreAsync = null,
        Func<string, double, CancellationToken, Task<ImageTheme>>? importAsync = null,
        Func<CancellationToken, Task>? resetAsync = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        if (debounceInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounceInterval));
        this.debounceInterval = debounceInterval;
        this.restoreAsync = restoreAsync ?? this.service.RestoreAsync;
        this.importAsync = importAsync ?? this.service.ImportAsync;
        this.resetAsync = resetAsync ?? this.service.ResetAsync;
        ownerContext = SynchronizationContext.Current;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ThemeChanged;
    public event Action<string>? Feedback;

    public ImageTheme Current => current;

    public double Opacity
    {
        get => Current.Opacity;
        set => RunOnOwner(() =>
        {
            var next = new ImageTheme(Current.ImagePath, ImageThemeService.ClampOpacity(value));
            if (next == Current) return;
            SetCurrent(next);
            SchedulePersistence(next);
        });
    }

    public bool IsCustom => !Current.IsDefault;
    public string DisplayName => IsCustom ? Path.GetFileName(Current.ImagePath) : "默认浅色背景";

    public Task RestoreAsync(CancellationToken ct = default) => StartDurableOperation(async () =>
    {
        await persistenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var restored = await restoreAsync(ct).ConfigureAwait(false);
            RunOnOwner(() => SetDurableCurrent(restored));
        }
        finally { persistenceGate.Release(); }
    });

    public Task ImportAsync(string sourcePath, double opacity, CancellationToken ct = default) => StartDurableOperation(async () =>
    {
        await persistenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var imported = await importAsync(sourcePath, opacity, ct).ConfigureAwait(false);
            RunOnOwner(() => SetDurableCurrent(imported));
        }
        finally { persistenceGate.Release(); }
    });

    public Task ResetAsync(CancellationToken ct = default) => StartDurableOperation(async () =>
    {
        await persistenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await resetAsync(ct).ConfigureAwait(false);
            RunOnOwner(() => SetDurableCurrent(ImageTheme.Default));
        }
        finally { persistenceGate.Release(); }
    });

    public async Task FlushAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task snapshot;
            lock (persistenceSync) snapshot = durableTail;
            await snapshot.WaitAsync(ct).ConfigureAwait(false);
            lock (persistenceSync)
            {
                if (ReferenceEquals(snapshot, durableTail)) return;
            }
        }
    }

    private void SetCurrent(ImageTheme value)
    {
        current = value;
        PropertyChanged?.Invoke(this, new(nameof(Current)));
        PropertyChanged?.Invoke(this, new(nameof(Opacity)));
        PropertyChanged?.Invoke(this, new(nameof(IsCustom)));
        PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetDurableCurrent(ImageTheme value)
    {
        CancelPendingPersistence();
        lock (persistenceSync)
        {
            revision++;
            persistedRevision = revision;
        }
        SetCurrent(value);
    }

    private void SchedulePersistence(ImageTheme snapshot)
    {
        CancellationTokenSource? previous;
        CancellationToken token;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (persistenceSync)
        {
            previous = debounceCancellation;
            debounceCancellation = new CancellationTokenSource();
            token = debounceCancellation.Token;
            long scheduledRevision = ++revision;
            Task predecessor = durableTail;
            Task operation = RunRegisteredAsync(start.Task,
                () => RunAfterPredecessorAsync(predecessor,
                    () => DebounceAndPersistAsync(scheduledRevision, snapshot, token)));
            durableTail = ObserveCompletionAsync(operation);
        }
        CancelAndDispose(previous);
        start.SetResult();
    }

    private Task StartDurableOperation(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CancellationTokenSource? cancellation;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task registered;
        lock (persistenceSync)
        {
            cancellation = debounceCancellation;
            debounceCancellation = null;
            Task predecessor = durableTail;
            registered = RunRegisteredAsync(start.Task,
                () => RunAfterPredecessorAsync(predecessor, operation));
            durableTail = ObserveCompletionAsync(registered);
        }
        CancelAndDispose(cancellation);
        start.SetResult();
        return registered;
    }

    private static async Task RunRegisteredAsync(Task start, Func<Task> operation)
    {
        await start.ConfigureAwait(false);
        await operation().ConfigureAwait(false);
    }

    internal static async Task RunAfterPredecessorAsync(Task predecessor, Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(operation);
        try { await predecessor.ConfigureAwait(false); }
        catch { }
        await operation().ConfigureAwait(false);
    }

    private static async Task ObserveCompletionAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch { }
    }

    private async Task DebounceAndPersistAsync(long scheduledRevision, ImageTheme snapshot, CancellationToken token)
    {
        try
        {
            await Task.Delay(debounceInterval, token).ConfigureAwait(false);
            await persistenceGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (persistenceSync)
                {
                    if (token.IsCancellationRequested || scheduledRevision != revision || scheduledRevision <= persistedRevision) return;
                }

                await saveAsync(snapshot, token).ConfigureAwait(false);
                lock (persistenceSync) persistedRevision = Math.Max(persistedRevision, scheduledRevision);
            }
            finally { persistenceGate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { RunOnOwner(() => Feedback?.Invoke($"主题设置未保存：{exception.Message}")); }
    }

    private void CancelPendingPersistence()
    {
        CancellationTokenSource? cancellation;
        lock (persistenceSync)
        {
            cancellation = debounceCancellation;
            debounceCancellation = null;
        }
        CancelAndDispose(cancellation);
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void RunOnOwner(Action action)
    {
        if (ownerContext is null || SynchronizationContext.Current == ownerContext)
        {
            action();
            return;
        }

        Exception? failure = null;
        ownerContext.Send(_ =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }, null);
        if (failure is not null) throw failure;
    }
}
