using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Data;

namespace WordFlow.App.ViewModels;

public interface ICardPronunciationPlayback
{
    event EventHandler<PronunciationPlaybackResult>? PlaybackFeedback;
    void OnCardChanged(Guid? wordId, string? word, bool isPaused);
    void SetPaused(bool paused);
    void Stop();
}

public sealed class PronunciationSettingsViewModel : INotifyPropertyChanged, ICapabilityAwareFloatingCardActionPort, ICardPronunciationPlayback, IDisposable
{
    private const string PreservedConflictIssue = "检测到本机语音清单冲突，已保留原语音选择";
    public const string VoiceKey = "pronunciation.voice_id";
    public const string AccentKey = "pronunciation.accent";
    public const string RateKey = "pronunciation.rate";
    public const string VolumeKey = "pronunciation.volume";
    public const string AutoplayKey = "pronunciation.autoplay";

    private static readonly string[] SettingKeys = [VoiceKey, AccentKey, RateKey, VolumeKey, AutoplayKey];
    private readonly IPronunciationService service;
    private readonly SqliteAppSettingStore store;
    private readonly SynchronizationContext? feedbackContext;
    private string? selectedVoiceId;
    private PronunciationAccent accentPreference = PronunciationAccent.Automatic;
    private int rate;
    private int volume = 100;
    private bool autoplay;
    private string? settingsIssue;
    private readonly object operationGate = new();
    private readonly HashSet<Task> operations = [];
    private readonly object playbackActorGate = new();
    private Task playbackActorTail = Task.CompletedTask;
    private PlaybackOperation? currentPlayback;
    private long playbackGeneration;
    private bool paused;
    private bool disposed;

    public PronunciationSettingsViewModel(
        IPronunciationService service,
        SqliteAppSettingStore store,
        SynchronizationContext? feedbackContext = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.feedbackContext = feedbackContext;
    }

    public IReadOnlyList<PronunciationVoice> Voices => service.Voices;
    public PronunciationAvailability Availability => service.Availability;
    public FloatingCardActionCapability Capability =>
        service.Availability.IsAvailable && ResolveVoice() is not null
            ? new(true, "播放 Windows 本机离线发音")
            : new(false, service.Availability.Message);
    public string? SelectedVoiceId { get => selectedVoiceId; set => SetField(ref selectedVoiceId, value); }
    public PronunciationAccent AccentPreference { get => accentPreference; set => SetField(ref accentPreference, value); }
    public int Rate { get => rate; set => SetField(ref rate, value); }
    public int Volume { get => volume; set => SetField(ref volume, value); }
    public bool Autoplay
    {
        get => autoplay;
        set
        {
            if (autoplay == value) return;
            autoplay = value;
            EnqueuePlayback(value ? NoPlaybackWorkAsync : CancelPlaybackAsync);
            RaisePropertyChanged(nameof(Autoplay));
        }
    }
    public string? SettingsIssue { get => settingsIssue; private set => SetField(ref settingsIssue, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<PronunciationPlaybackResult>? PlaybackFeedback;

    public async Task RestoreAsync(CancellationToken ct = default)
    {
        var snapshot = await Task.Run(async () =>
        {
            var values = await store.GetManyAsync(SettingKeys, ct).ConfigureAwait(false);
            return BuildRestoredSnapshot(values);
        }, ct).ConfigureAwait(false);

        await ApplyOnOwnerAsync(() => ApplyRestoredSnapshot(snapshot), ct).ConfigureAwait(false);
        if (snapshot.NeedsRepair)
            await PersistAsync(snapshot.ToValues(), ct).ConfigureAwait(false);
    }

    public Task SaveAsync(CancellationToken ct = default)
    {
        ValidateCurrentSettings();
        return PersistAsync(CurrentValues(), ct);
    }

    private RestoredSettingsSnapshot BuildRestoredSnapshot(IReadOnlyDictionary<string, string?> values)
    {
        var issues = new List<string>();

        string? rawVoice = values[VoiceKey];
        string? restoredVoice;
        if (string.IsNullOrWhiteSpace(rawVoice)) restoredVoice = null;
        else if (service.Availability.InventoryState == PronunciationInventoryState.UnavailableFault ||
                 service.Voices.Any(voice => string.Equals(voice.Id, rawVoice, StringComparison.Ordinal)))
            restoredVoice = rawVoice;
        else if (service.Availability.QuarantinedVoiceIds.Contains(rawVoice, StringComparer.Ordinal))
        {
            restoredVoice = rawVoice;
            issues.Add(PreservedConflictIssue);
        }
        else restoredVoice = AddIssue<string?>(issues, "已移除不可用的语音", null);

        string? rawAccent = values[AccentKey];
        var restoredAccent = rawAccent is null ? PronunciationAccent.Automatic
            : Enum.TryParse<PronunciationAccent>(rawAccent, true, out var parsedAccent) &&
              parsedAccent is PronunciationAccent.Automatic or PronunciationAccent.British or PronunciationAccent.American
                ? parsedAccent
                : AddIssue(issues, "已修复口音偏好", PronunciationAccent.Automatic);

        int restoredRate = ParseBounded(values[RateKey], -10, 10, 0, "已修复语速", issues);
        int restoredVolume = ParseBounded(values[VolumeKey], 0, 100, 100, "已修复音量", issues);
        bool restoredAutoplay = values[AutoplayKey] is null ? false
            : bool.TryParse(values[AutoplayKey], out var parsedAutoplay) ? parsedAutoplay
            : AddIssue(issues, "已修复自动播放设置", false);

        return new(
            restoredVoice,
            restoredAccent,
            restoredRate,
            restoredVolume,
            restoredAutoplay,
            issues.Count == 0 ? null : string.Join("；", issues.Distinct(StringComparer.Ordinal)),
            issues.Any(issue => !string.Equals(issue, PreservedConflictIssue, StringComparison.Ordinal)));
    }

    private void ApplyRestoredSnapshot(RestoredSettingsSnapshot snapshot)
    {
        bool autoplayChanged = autoplay != snapshot.Autoplay;
        selectedVoiceId = snapshot.VoiceId;
        accentPreference = snapshot.Accent;
        rate = snapshot.Rate;
        volume = snapshot.Volume;
        autoplay = snapshot.Autoplay;
        settingsIssue = snapshot.Issue;
        if (autoplayChanged)
        {
            EnqueuePlayback(autoplay ? NoPlaybackWorkAsync : CancelPlaybackAsync);
        }
        NotifyAll();
        RaisePropertyChanged(nameof(SettingsIssue));
    }

    private void ValidateCurrentSettings()
    {
        if (Rate is < -10 or > 10) throw new ArgumentOutOfRangeException(nameof(Rate));
        if (Volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(Volume));
        if (AccentPreference is not (PronunciationAccent.Automatic or PronunciationAccent.British or PronunciationAccent.American))
            throw new ArgumentOutOfRangeException(nameof(AccentPreference));
        if (service.Availability.InventoryState != PronunciationInventoryState.UnavailableFault &&
            !string.IsNullOrWhiteSpace(SelectedVoiceId) &&
            service.Voices.All(voice => !string.Equals(voice.Id, SelectedVoiceId, StringComparison.Ordinal)) &&
            !service.Availability.QuarantinedVoiceIds.Contains(SelectedVoiceId, StringComparer.Ordinal))
            throw new ArgumentException("The selected voice is not an installed English voice.", nameof(SelectedVoiceId));
    }

    private IReadOnlyDictionary<string, string> CurrentValues() => new Dictionary<string, string>
    {
        [VoiceKey] = SelectedVoiceId ?? "",
        [AccentKey] = AccentPreference.ToString(),
        [RateKey] = Rate.ToString(CultureInfo.InvariantCulture),
        [VolumeKey] = Volume.ToString(CultureInfo.InvariantCulture),
        [AutoplayKey] = Autoplay.ToString(),
    };

    private Task PersistAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct) =>
        Task.Run(() => store.SetManyAsync(values, ct), ct);

    private Task ApplyOnOwnerAsync(Action apply, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (feedbackContext is null || ReferenceEquals(SynchronizationContext.Current, feedbackContext))
        {
            apply();
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int outcome = 0;
        var cancellation = ct.Register(() =>
        {
            if (Interlocked.CompareExchange(ref outcome, 2, 0) == 0)
                completion.TrySetCanceled(ct);
        });
        try
        {
            ct.ThrowIfCancellationRequested();
            feedbackContext.Post(_ =>
            {
                if (Interlocked.CompareExchange(ref outcome, 1, 0) != 0) return;
                try { apply(); completion.TrySetResult(true); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }, null);
        }
        catch
        {
            cancellation.Dispose();
            throw;
        }
        return AwaitOwnerApplyAsync(completion.Task, cancellation);
    }

    private static async Task AwaitOwnerApplyAsync(Task completion, CancellationTokenRegistration cancellation)
    {
        try { await completion.ConfigureAwait(false); }
        finally { cancellation.Dispose(); }
    }

    public FloatingCardActionResult Execute(Guid wordId, string word)
    {
        if (disposed || !service.Availability.IsAvailable || ResolveVoice() is null)
            return FloatingCardActionResult.Unavailable(service.Availability.Message);
        EnqueuePlayback(operation => PublishSpeechAsync(operation, word));
        return FloatingCardActionResult.Completed($"正在播放 {word} 的离线发音");
    }

    public void OnCardChanged(Guid? wordId, string? word, bool isPaused)
    {
        if (disposed) return;
        bool shouldAutoplay = Autoplay;
        EnqueuePlayback(
            operation => ChangeCardAsync(operation, word, shouldAutoplay),
            () => paused = isPaused);
    }

    public void SetPaused(bool value)
    {
        if (disposed) return;
        EnqueuePlayback(
            value ? CancelPlaybackAsync : NoPlaybackWorkAsync,
            () => paused = value);
    }

    public void Stop()
    {
        if (disposed) return;
        EnqueuePlayback(CancelPlaybackAsync);
    }

    public void Dispose()
    {
        Task queued;
        lock (playbackActorGate)
        {
            if (disposed) return;
            disposed = true;
            paused = true;
            queued = EnqueuePlaybackLocked(CancelPlaybackAsync);
        }
        Track(queued);
    }

    private async Task ChangeCardAsync(PlaybackOperation operation, string? word, bool shouldAutoplay)
    {
        try { await service.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            PublishIfCurrent(operation.Generation, PronunciationPlaybackResult.Failed(exception.Message));
            return;
        }
        if (operation.Cancellation.IsCancellationRequested ||
            !IsCurrent(operation.Generation) || paused || !shouldAutoplay || string.IsNullOrWhiteSpace(word)) return;
        await PublishSpeechAsync(operation, word).ConfigureAwait(false);
    }

    private Task PublishSpeechAsync(PlaybackOperation operation, string word)
    {
        if (operation.Cancellation.IsCancellationRequested || !IsCurrent(operation.Generation))
            return Task.CompletedTask;
        var voice = ResolveVoice();
        if (voice is null)
        {
            PublishIfCurrent(operation.Generation, PronunciationPlaybackResult.Unavailable(service.Availability.Message));
            return Task.CompletedTask;
        }

        try
        {
            operation.PublicationOwned = true;
            var completion = service.SpeakAsync(word, voice.Id, Rate, Volume, operation.Cancellation.Token);
            Track(ObservePlaybackAsync(operation, completion));
        }
        catch (Exception exception)
        {
            operation.PublicationOwned = false;
            PublishIfCurrent(operation.Generation, PronunciationPlaybackResult.Failed(exception.Message));
        }
        return Task.CompletedTask;
    }

    private async Task ObservePlaybackAsync(
        PlaybackOperation operation,
        Task<PronunciationPlaybackResult> completion)
    {
        try
        {
            var result = await completion.ConfigureAwait(false);
            if (result.Status != PronunciationPlaybackStatus.Cancelled)
                PublishIfCurrent(operation.Generation, result);
        }
        catch (Exception exception)
        {
            PublishIfCurrent(operation.Generation, PronunciationPlaybackResult.Failed(exception.Message));
        }
        finally { CompletePlaybackOperation(operation); }
    }

    private async Task CancelPlaybackAsync(PlaybackOperation operation)
    {
        try { await service.CancelAsync().ConfigureAwait(false); }
        catch { }
    }

    private static Task NoPlaybackWorkAsync(PlaybackOperation operation) => Task.CompletedTask;

    private void EnqueuePlayback(
        Func<PlaybackOperation, Task> command,
        Action? transition = null)
    {
        Task queued;
        lock (playbackActorGate)
        {
            if (disposed) return;
            transition?.Invoke();
            queued = EnqueuePlaybackLocked(command);
        }
        Track(queued);
    }

    private Task EnqueuePlaybackLocked(Func<PlaybackOperation, Task> command)
    {
        try { currentPlayback?.Cancellation.Cancel(); }
        catch { }
        var operation = new PlaybackOperation(++playbackGeneration);
        currentPlayback = operation;
        var predecessor = playbackActorTail;
        var queued = RunPlaybackCommandAsync(predecessor, operation, command);
        playbackActorTail = queued;
        return queued;
    }

    private async Task RunPlaybackCommandAsync(
        Task predecessor,
        PlaybackOperation operation,
        Func<PlaybackOperation, Task> command)
    {
        await Task.Yield();
        try { await predecessor.ConfigureAwait(false); }
        catch { }
        try
        {
            await command(operation).ConfigureAwait(false);
        }
        finally
        {
            if (!operation.PublicationOwned) CompletePlaybackOperation(operation);
        }
    }

    private void CompletePlaybackOperation(PlaybackOperation operation)
    {
        lock (playbackActorGate)
        {
            if (ReferenceEquals(currentPlayback, operation)) currentPlayback = null;
        }
        operation.Cancellation.Dispose();
    }

    private PronunciationVoice? ResolveVoice() => service.SelectVoice(SelectedVoiceId, AccentPreference);

    private bool IsCurrent(long generation) => !disposed && generation == Volatile.Read(ref playbackGeneration);

    private void PublishIfCurrent(long generation, PronunciationPlaybackResult result)
    {
        if (!IsCurrent(generation)) return;
        if (feedbackContext is not null && !ReferenceEquals(SynchronizationContext.Current, feedbackContext))
        {
            feedbackContext.Post(_ => PublishOnOwningContext(generation, result), null);
            return;
        }
        PublishOnOwningContext(generation, result);
    }

    private void PublishOnOwningContext(long generation, PronunciationPlaybackResult result)
    {
        if (!IsCurrent(generation) || PlaybackFeedback is not { } handlers) return;
        foreach (EventHandler<PronunciationPlaybackResult> handler in handlers.GetInvocationList())
        {
            try { handler(this, result); }
            catch { }
        }
    }

    private void Track(Task task)
    {
        lock (operationGate) operations.Add(task);
        _ = task.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (operationGate) operations.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static int ParseBounded(string? raw, int minimum, int maximum, int fallback, string issue, List<string> issues) =>
        raw is null ? fallback
            : int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= minimum && parsed <= maximum
                ? parsed
                : AddIssue(issues, issue, fallback);

    private static T AddIssue<T>(List<string> issues, string issue, T fallback)
    {
        issues.Add(issue);
        return fallback;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    private void NotifyAll()
    {
        foreach (string property in new[] { nameof(SelectedVoiceId), nameof(AccentPreference), nameof(Rate), nameof(Volume), nameof(Autoplay) })
            RaisePropertyChanged(property);
    }

    private void RaisePropertyChanged(string? propertyName)
    {
        if (PropertyChanged is not { } handlers) return;
        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, new(propertyName)); }
            catch { }
        }
    }

    private sealed record RestoredSettingsSnapshot(
        string? VoiceId,
        PronunciationAccent Accent,
        int Rate,
        int Volume,
        bool Autoplay,
        string? Issue,
        bool NeedsRepair)
    {
        public IReadOnlyDictionary<string, string> ToValues() => new Dictionary<string, string>
        {
            [VoiceKey] = VoiceId ?? "",
            [AccentKey] = Accent.ToString(),
            [RateKey] = Rate.ToString(CultureInfo.InvariantCulture),
            [VolumeKey] = Volume.ToString(CultureInfo.InvariantCulture),
            [AutoplayKey] = Autoplay.ToString(),
        };
    }

    private sealed class PlaybackOperation(long generation)
    {
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool PublicationOwned { get; set; }
    }
}
