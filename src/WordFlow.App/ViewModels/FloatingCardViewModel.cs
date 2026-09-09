using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WordFlow.Application;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;
using WordFlow.Domain.Learning;

namespace WordFlow.App.ViewModels;

public sealed record FloatingCardOperations(
    Func<DailyPlan, CancellationToken, Task<UseCaseResult<NextCard?>>> GetNextCard,
    Func<SubmitRatingRequest, CancellationToken, Task<UseCaseResult<LearningTransition>>> SubmitRating,
    Func<SlashWordRequest, CancellationToken, Task<UseCaseResult<LearningTransition>>> SlashWord,
    Func<UndoLastActionRequest, CancellationToken, Task<UseCaseResult<CardState>>> UndoLastAction,
    Func<CancellationToken, Task<UseCaseResult<DateTimeOffset?>>>? GetNextRelearningDue = null);

public sealed class IdleWakeScheduleChangedEventArgs(DateTimeOffset? dueAtUtc) : EventArgs
{
    public DateTimeOffset? DueAtUtc { get; } = dueAtUtc;
}

public sealed class FloatingCardViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly IReadOnlyList<string> StableReadingOrder = ["Word", "Phonetic", "Chinese"];
    private readonly FloatingCardOperations operations;
    private readonly ShortcutLabelMap shortcutLabels;
    private readonly Func<DailyPlan> planProvider;
    private readonly IFloatingCardActionHost actionHost;
    private readonly ICardPronunciationPlayback? pronunciation;
    private readonly LearningDataChangeNotifier? dataChanges;
    private readonly TimeProvider timeProvider;
    private NextCard? current;
    private string? errorMessage;
    private string accessibleStatus = "准备学习";
    private bool isBusy;
    private Guid? lastEventId;
    private bool disposed;
    private bool isPaused;
    private bool hasNextCardRefreshFailure;
    private bool isVisible = true;
    private DateTimeOffset? idleWakeAtUtc;
    private readonly CancellationTokenSource lifetime = new();
    private long generation;

    public FloatingCardViewModel(
        FloatingCardOperations operations,
        RelationDrawerViewModel synonyms,
        RelationDrawerViewModel confusables,
        ShortcutLabelMap shortcutLabels,
        IFloatingCardActionHost? actionHost = null,
        DailyPlan? plan = null,
        ICardPronunciationPlayback? pronunciation = null,
        Func<DailyPlan>? planProvider = null,
        TimeProvider? timeProvider = null,
        LearningDataChangeNotifier? dataChanges = null)
    {
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        Synonyms = synonyms ?? throw new ArgumentNullException(nameof(synonyms));
        Confusables = confusables ?? throw new ArgumentNullException(nameof(confusables));
        this.shortcutLabels = shortcutLabels ?? throw new ArgumentNullException(nameof(shortcutLabels));
        this.actionHost = actionHost ?? FloatingCardActionHost.Unavailable;
        this.pronunciation = pronunciation;
        this.dataChanges = dataChanges;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.planProvider = planProvider ?? (() => plan ?? DailyPlan.Default);
        shortcutLabels.PropertyChanged += OnShortcutLabelsChanged;
        Synonyms.ActionRequested += OnRelationActionRequested;
        Confusables.ActionRequested += OnRelationActionRequested;
        if (pronunciation is not null) pronunciation.PlaybackFeedback += OnPronunciationFeedback;

        AgainCommand = Command(() => RateAsync(RatingShortcut.F1), () => CanRate);
        HardCommand = Command(() => RateAsync(RatingShortcut.F2), () => CanRate);
        GoodCommand = Command(() => RateAsync(RatingShortcut.F3), () => CanRate);
        SlashCommand = Command(() => SlashAsync(), () => CanRate);
        UndoCommand = Command(() => UndoAsync(), () => !IsPaused && !IsBusy && lastEventId.HasValue);
        ToggleSynonymsCommand = Command(() => ToggleDrawerAsync(Synonyms), () => !IsPaused && HasCard && !IsBusy);
        ToggleConfusablesCommand = Command(() => ToggleDrawerAsync(Confusables), () => !IsPaused && HasCard && !IsBusy);
        SpeakCurrentWordCommand = new ActionCommand(() => PublishCurrent(RelationActionKind.Speak), () => !IsPaused && HasCard && !IsBusy && CanSpeakCurrentWord);
        OpenCurrentDetailsCommand = new ActionCommand(() => PublishCurrent(RelationActionKind.OpenDetails), () => !IsPaused && HasCard && !IsBusy && CanOpenCurrentDetails);
        AddToNotebookCommand = new ActionCommand(() => PublishCurrent(RelationActionKind.AddToLearning), () => !IsPaused && HasCard && !IsBusy && CanAddToNotebook);
    }

    public IReadOnlyList<string> ReadingOrder => StableReadingOrder;
    public RelationDrawerViewModel Synonyms { get; }
    public RelationDrawerViewModel Confusables { get; }
    public string Word => current?.Word.Lemma ?? (hasNextCardRefreshFailure ? "下一词暂时无法加载" : "今日学习已完成");
    public string Phonetic => current?.Word.Phonetic ?? "";
    public string Chinese => current?.Word.Chinese ?? (hasNextCardRefreshFailure ? "学习记录已保存，请稍后重新加载" : "没有待学习的单词");
    public bool HasCard => current is not null;
    public bool CanRate => HasCard && !IsBusy && !IsPaused;
    public bool IsPaused => isPaused;
    public bool CanSpeakCurrentWord => actionHost.Capability(RelationActionKind.Speak).IsAvailable;
    public bool CanOpenCurrentDetails => actionHost.Capability(RelationActionKind.OpenDetails).IsAvailable;
    public bool CanAddToNotebook => actionHost.Capability(RelationActionKind.AddToLearning).IsAvailable;
    public string SpeakAvailabilityHelp => $"{actionHost.Capability(RelationActionKind.Speak).HelpText} · 快捷键 {PronunciationGesture}";
    public string DetailsAvailabilityHelp => actionHost.Capability(RelationActionKind.OpenDetails).HelpText;
    public string AddToNotebookAvailabilityHelp => actionHost.Capability(RelationActionKind.AddToLearning).HelpText;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetField(ref isBusy, value)) return;
            OnPropertyChanged(nameof(CanRate));
            RaiseCommandStates();
        }
    }
    public string? ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (!SetField(ref errorMessage, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string AccessibleStatus
    {
        get => accessibleStatus;
        private set => SetField(ref accessibleStatus, value);
    }

    public string AgainGesture => Label(ShortcutAction.Again);
    public string HardGesture => Label(ShortcutAction.Hard);
    public string GoodGesture => Label(ShortcutAction.Good);
    public string SlashGesture => Label(ShortcutAction.Slash);
    public string SynonymsGesture => Label(ShortcutAction.ToggleSynonyms);
    public string ConfusablesGesture => Label(ShortcutAction.ToggleConfusables);
    public string UndoGesture => Label(ShortcutAction.Undo);
    public string PronunciationGesture => Label(ShortcutAction.Pronounce);

    public ICommand AgainCommand { get; }
    public ICommand HardCommand { get; }
    public ICommand GoodCommand { get; }
    public ICommand SlashCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand ToggleSynonymsCommand { get; }
    public ICommand ToggleConfusablesCommand { get; }
    public ICommand SpeakCurrentWordCommand { get; }
    public ICommand OpenCurrentDetailsCommand { get; }
    public ICommand AddToNotebookCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<RelationActionRequestedEventArgs>? ActionRequested;
    public event EventHandler<IdleWakeScheduleChangedEventArgs>? IdleWakeScheduleChanged;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            var result = await operations.GetNextCard(planProvider(), linked.Token);
            if (disposed) return;
            switch (result)
            {
                case Success<NextCard?> success:
                    SetCurrent(success.Value);
                    AccessibleStatus = success.Value is null ? "今日学习已完成" : $"当前单词 {success.Value.Word.Lemma}";
                    await RefreshIdleWakeScheduleAsync(linked.Token);
                    break;
                case StorageFailure<NextCard?> failure:
                    SetFailure("无法加载学习卡", failure.Message);
                    break;
                case NotFound<NextCard?> failure:
                    SetFailure("无法加载学习卡", failure.Message);
                    break;
                case Conflict<NextCard?> failure:
                    SetFailure("无法加载学习卡", failure.Message);
                    break;
            }
        }
        catch (OperationCanceledException) { if (!disposed) AccessibleStatus = "操作已取消"; }
        catch (Exception exception) { SetFailure("无法加载学习卡", exception.Message); }
        finally { if (!disposed) IsBusy = false; }
    }

    public Task WakeIdleAsync(CancellationToken ct = default)
    {
        if (disposed || IsPaused || !isVisible || IsBusy || HasCard) return Task.CompletedTask;
        CancelIdleWake();
        return InitializeAsync(ct);
    }

    public async Task RefreshIdleWakeScheduleAsync(CancellationToken ct = default)
    {
        if (disposed || IsPaused || !isVisible || HasCard)
        {
            CancelIdleWake();
            return;
        }

        var now = timeProvider.GetUtcNow();
        DateTimeOffset? relearningDue = null;
        if (operations.GetNextRelearningDue is not null)
        {
            try
            {
                var result = await operations.GetNextRelearningDue(ct);
                relearningDue = result is Success<DateTimeOffset?> success
                    ? success.Value
                    : now.AddMinutes(1);
            }
            catch (OperationCanceledException) { throw; }
            catch { relearningDue = now.AddMinutes(1); }
        }

        var midnight = NextLocalMidnightUtc();
        SetIdleWake(relearningDue is { } due && due < midnight ? due : midnight);
    }

    public void SetVisible(bool visible)
    {
        if (disposed || isVisible == visible) return;
        isVisible = visible;
        if (!visible)
        {
            CancelIdleWake();
            pronunciation?.OnCardHidden();
        }
        else _ = RefreshIdleWakeAfterLifecycleAsync();
    }

    public Task RateAsync(RatingShortcut rating, CancellationToken ct = default) =>
        MutateAsync((card, commandId, eventId, token) => operations.SubmitRating(
            new(commandId, eventId, card.Card, card.Revision, card.QueueItemId, rating, card.QueueDay, planProvider()), token), eventIdOnSuccess: true, ct);

    public Task SlashAsync(CancellationToken ct = default) =>
        MutateAsync((card, commandId, eventId, token) => operations.SlashWord(
            new(commandId, eventId, card.Card, card.Revision, card.QueueItemId, card.QueueDay, planProvider()), token), eventIdOnSuccess: true, ct);

    public async Task UndoAsync(CancellationToken ct = default)
    {
        if (IsPaused || IsBusy || !lastEventId.HasValue) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            var operationGeneration = generation;
            var result = await operations.UndoLastAction(
                new(Guid.NewGuid(), Guid.NewGuid(), lastEventId.Value), linked.Token);
            if (result is Success<CardState>) dataChanges?.PublishCommitted();
            if (disposed || generation != operationGeneration) return;
            switch (result)
            {
                case Success<CardState>:
                    lastEventId = null;
                    Synonyms.Reset();
                    Confusables.Reset();
                    AccessibleStatus = "已撤销上一学习操作";
                    await ReloadAfterUndoAsync(operationGeneration, linked.Token);
                    break;
                case StorageFailure<CardState> failure: SetFailure("撤销失败", failure.Message); break;
                case NotFound<CardState> failure: SetFailure("撤销失败", failure.Message); break;
                case Conflict<CardState> failure: SetFailure("撤销失败", failure.Message); break;
            }
        }
        catch (OperationCanceledException) { if (!disposed) AccessibleStatus = "撤销已取消"; }
        catch (Exception exception) { if (!disposed) SetFailure("撤销失败", exception.Message); }
        finally { if (!disposed) IsBusy = false; }
    }

    public async Task HandleShortcutAsync(ShortcutAction action, CancellationToken ct = default)
    {
        if (IsPaused) { AccessibleStatus = "学习已暂停"; return; }
        switch (action)
        {
            case ShortcutAction.Again: await RateAsync(RatingShortcut.F1, ct); break;
            case ShortcutAction.Hard: await RateAsync(RatingShortcut.F2, ct); break;
            case ShortcutAction.Good: await RateAsync(RatingShortcut.F3, ct); break;
            case ShortcutAction.Slash: await SlashAsync(ct); break;
            case ShortcutAction.ToggleSynonyms: await ToggleDrawerAsync(Synonyms, ct); break;
            case ShortcutAction.ToggleConfusables: await ToggleDrawerAsync(Confusables, ct); break;
            case ShortcutAction.Undo: await UndoAsync(ct); break;
            case ShortcutAction.Pronounce: PublishCurrent(RelationActionKind.Speak); break;
            default: throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown card action.");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        generation++;
        CancelIdleWake();
        lifetime.Cancel();
        shortcutLabels.PropertyChanged -= OnShortcutLabelsChanged;
        Synonyms.ActionRequested -= OnRelationActionRequested;
        Confusables.ActionRequested -= OnRelationActionRequested;
        if (pronunciation is not null)
        {
            pronunciation.PlaybackFeedback -= OnPronunciationFeedback;
            pronunciation.Stop();
        }
        transientStatusCancellation?.Cancel();
        transientStatusCancellation?.Dispose();
        Synonyms.Dispose();
        Confusables.Dispose();
        shortcutLabels.Dispose();
        lifetime.Dispose();
    }

    private async Task MutateAsync(
        Func<NextCard, Guid, Guid, CancellationToken, Task<UseCaseResult<LearningTransition>>> submit,
        bool eventIdOnSuccess,
        CancellationToken ct)
    {
        if (!CanRate || current is not { } captured) return;
        IsBusy = true;
        ErrorMessage = null;
        var eventId = Guid.NewGuid();
        var operationGeneration = generation;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            var result = await submit(captured, Guid.NewGuid(), eventId, linked.Token);
            if (result is Success<LearningTransition> { Value.Status: LearningTransitionStatus.Committed })
                dataChanges?.PublishCommitted();
            if (disposed || generation != operationGeneration) return;
            switch (result)
            {
                case Success<LearningTransition> success:
                    if (success.Value.Status == LearningTransitionStatus.QueueDayRolledOver)
                    {
                        SetCurrent(success.Value.NextCard);
                        AccessibleStatus = success.Value.NextCard is null
                            ? "日期已切换；上一日卡片未提交，今日暂无待学习单词"
                            : $"日期已切换；上一日卡片未提交，请重新评分今日单词 {success.Value.NextCard.Word.Lemma}";
                        await RefreshIdleWakeScheduleAsync(linked.Token);
                        break;
                    }
                    if (eventIdOnSuccess) lastEventId = success.Value.CommittedEventId ?? eventId;
                    Synonyms.Reset();
                    Confusables.Reset();
                    if (success.Value.RefreshStatus == NextCardRefreshStatus.Failed)
                    {
                        SetCurrent(null, refreshFailed: true);
                        var detail = string.IsNullOrWhiteSpace(success.Value.RefreshFailureMessage)
                            ? ""
                            : $"：{success.Value.RefreshFailureMessage}";
                        AccessibleStatus = $"学习记录已保存，但下一词暂时无法加载{detail}";
                    }
                    else
                    {
                        SetCurrent(success.Value.NextCard);
                        AccessibleStatus = success.Value.NextCard is null
                            ? "学习记录已保存，今日队列已完成"
                            : $"学习记录已保存，下一词 {success.Value.NextCard.Word.Lemma}";
                    }
                    await RefreshIdleWakeScheduleAsync(linked.Token);
                    break;
                case StorageFailure<LearningTransition> failure: SetFailure("学习记录未保存", failure.Message); break;
                case NotFound<LearningTransition> failure: SetFailure("学习记录未保存", failure.Message); break;
                case Conflict<LearningTransition> failure: SetFailure("学习记录未保存", failure.Message); break;
            }
        }
        catch (OperationCanceledException) { if (!disposed) AccessibleStatus = "学习操作已取消，当前卡片未改变"; }
        catch (Exception exception) { if (!disposed) SetFailure("学习记录未保存", exception.Message); }
        finally { if (!disposed) IsBusy = false; }
    }

    private async Task ToggleDrawerAsync(RelationDrawerViewModel drawer, CancellationToken ct = default)
    {
        if (IsPaused || !HasCard || IsBusy || current is null) return;
        if (drawer.IsOpen)
        {
            drawer.Close();
            return;
        }
        var other = ReferenceEquals(drawer, Synonyms) ? Confusables : Synonyms;
        other.Close();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await drawer.OpenAsync(current.Word.WordId, current.PrimarySense, linked.Token);
    }

    private async Task ReloadAfterUndoAsync(long operationGeneration, CancellationToken ct)
    {
        var result = await operations.GetNextCard(planProvider(), ct);
        if (disposed || generation != operationGeneration) return;
        if (result is Success<NextCard?> success) SetCurrent(success.Value);
        else if (result is StorageFailure<NextCard?> failure) SetFailure("已撤销，但无法刷新学习卡", failure.Message);
        await RefreshIdleWakeScheduleAsync(ct);
    }

    private void SetCurrent(NextCard? next, bool refreshFailed = false)
    {
        CancelIdleWake();
        generation++;
        current = next;
        hasNextCardRefreshFailure = refreshFailed;
        pronunciation?.OnCardChanged(next?.Word.WordId, next?.Word.Lemma, IsPaused || !isVisible);
        OnPropertyChanged(nameof(Word));
        OnPropertyChanged(nameof(Phonetic));
        OnPropertyChanged(nameof(Chinese));
        OnPropertyChanged(nameof(HasCard));
        OnPropertyChanged(nameof(CanRate));
        RaiseCommandStates();
    }

    private void SetFailure(string prefix, string message)
    {
        ErrorMessage = $"{prefix}：{message}";
        AccessibleStatus = ErrorMessage;
    }

    private string Label(ShortcutAction action) => shortcutLabels[action];

    private void OnShortcutLabelsChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(AgainGesture));
        OnPropertyChanged(nameof(HardGesture));
        OnPropertyChanged(nameof(GoodGesture));
        OnPropertyChanged(nameof(SlashGesture));
        OnPropertyChanged(nameof(SynonymsGesture));
        OnPropertyChanged(nameof(ConfusablesGesture));
        OnPropertyChanged(nameof(UndoGesture));
        OnPropertyChanged(nameof(PronunciationGesture));
        OnPropertyChanged(nameof(SpeakAvailabilityHelp));
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[] { AgainCommand, HardCommand, GoodCommand, SlashCommand, UndoCommand, ToggleSynonymsCommand, ToggleConfusablesCommand })
            if (command is AsyncActionCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
        foreach (var command in new[] { SpeakCurrentWordCommand, OpenCurrentDetailsCommand, AddToNotebookCommand })
            if (command is ActionCommand actionCommand) actionCommand.RaiseCanExecuteChanged();
    }

    public void ReportActionFeedback(string message, bool isError = false)
    {
        if (disposed) return;
        AccessibleStatus = message;
        if (isError) ErrorMessage = message;
        else ShowTransientStatus(message);
    }

    private CancellationTokenSource? transientStatusCancellation;

    public string? StatusMessage { get; private set; }
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public string HasStatusMessageVisibility => HasStatusMessage ? "Visible" : "Collapsed";

    private void ShowTransientStatus(string message)
    {
        transientStatusCancellation?.Cancel();
        transientStatusCancellation?.Dispose();
        StatusMessage = message;
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HasStatusMessage));
        OnPropertyChanged(nameof(HasStatusMessageVisibility));
        var cancellation = new CancellationTokenSource();
        transientStatusCancellation = cancellation;
        _ = ClearTransientStatusAsync(cancellation.Token);
    }

    private async Task ClearTransientStatusAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(true);
            if (token.IsCancellationRequested || disposed) return;
            StatusMessage = null;
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(HasStatusMessage));
            OnPropertyChanged(nameof(HasStatusMessageVisibility));
        }
        catch (OperationCanceledException) { }
    }

    public void RequestCurrentDetailsFromSurface()
    {
        if (current is not { } card || IsPaused || IsBusy || disposed) return;
        Dispatch(new(RelationActionKind.OpenDetails, card.Word.WordId, card.Word.Lemma));
    }

    public void SetPaused(bool paused)
    {
        if (disposed || isPaused == paused) return;
        isPaused = paused;
        pronunciation?.SetPaused(paused);
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(CanRate));
        if (paused)
        {
            CancelIdleWake();
            Synonyms.Close();
            Confusables.Close();
            AccessibleStatus = "学习已暂停";
        }
        else
        {
            AccessibleStatus = current is not null
                ? $"当前单词 {current.Word.Lemma}"
                : hasNextCardRefreshFailure ? "学习记录已保存，但下一词暂时无法加载" : "今日学习已完成";
            _ = RefreshIdleWakeAfterLifecycleAsync();
        }
        RaiseCommandStates();
    }

    private async Task RefreshIdleWakeAfterLifecycleAsync()
    {
        try { await RefreshIdleWakeScheduleAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    private DateTimeOffset NextLocalMidnightUtc()
    {
        var tomorrow = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime)
            .AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(tomorrow, timeProvider.LocalTimeZone), TimeSpan.Zero);
    }

    private void SetIdleWake(DateTimeOffset dueAtUtc)
    {
        dueAtUtc = dueAtUtc.ToUniversalTime();
        if (idleWakeAtUtc == dueAtUtc) return;
        idleWakeAtUtc = dueAtUtc;
        IdleWakeScheduleChanged?.Invoke(this, new(dueAtUtc));
    }

    private void CancelIdleWake()
    {
        if (idleWakeAtUtc is null) return;
        idleWakeAtUtc = null;
        IdleWakeScheduleChanged?.Invoke(this, new(null));
    }

    private AsyncActionCommand Command(Func<Task> execute, Func<bool> canExecute) =>
        new(execute, canExecute, exception => { if (!disposed) SetFailure("操作失败", exception.Message); });

    private void PublishCurrent(RelationActionKind action)
    {
        if (current is not { } card || IsPaused || IsBusy || disposed) return;
        Dispatch(new(action, card.Word.WordId, card.Word.Lemma));
    }

    private void OnRelationActionRequested(object? sender, RelationActionRequestedEventArgs args) => Dispatch(args);
    private void OnPronunciationFeedback(object? sender, PronunciationPlaybackResult result) =>
        ReportActionFeedback(result.Message, result.Status == PronunciationPlaybackStatus.Failed);
    private void Dispatch(RelationActionRequestedEventArgs args)
    {
        ActionRequested?.Invoke(this, args);
        var result = actionHost.Dispatch(args);
        ReportActionFeedback(result.AccessibleMessage, result.Status == FloatingCardActionStatus.Failed);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}

internal sealed class AsyncActionCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> report) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public async void Execute(object? parameter) => await ExecuteAsync();
    internal async Task ExecuteAsync()
    {
        try { await execute(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { report(exception); }
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class ActionCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
