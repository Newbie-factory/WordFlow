using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WordFlow.Application;
using WordFlow.Application.Learning;
using WordFlow.Application.Shortcuts;
using WordFlow.Domain.Learning;

namespace WordFlow.App.ViewModels;

public sealed record FloatingCardOperations(
    Func<DailyPlan, CancellationToken, Task<UseCaseResult<NextCard?>>> GetNextCard,
    Func<SubmitRatingRequest, CancellationToken, Task<UseCaseResult<LearningTransition>>> SubmitRating,
    Func<SlashWordRequest, CancellationToken, Task<UseCaseResult<LearningTransition>>> SlashWord,
    Func<UndoLastActionRequest, CancellationToken, Task<UseCaseResult<CardState>>> UndoLastAction);

public sealed class FloatingCardViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly IReadOnlyList<string> StableReadingOrder = ["Word", "Phonetic", "Chinese"];
    private readonly FloatingCardOperations operations;
    private readonly ShortcutLabelMap shortcutLabels;
    private readonly DailyPlan plan;
    private NextCard? current;
    private string? errorMessage;
    private string accessibleStatus = "准备学习";
    private bool isBusy;
    private Guid? lastEventId;
    private bool disposed;

    public FloatingCardViewModel(
        FloatingCardOperations operations,
        RelationDrawerViewModel synonyms,
        RelationDrawerViewModel confusables,
        ShortcutLabelMap shortcutLabels,
        DailyPlan? plan = null)
    {
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        Synonyms = synonyms ?? throw new ArgumentNullException(nameof(synonyms));
        Confusables = confusables ?? throw new ArgumentNullException(nameof(confusables));
        this.shortcutLabels = shortcutLabels ?? throw new ArgumentNullException(nameof(shortcutLabels));
        this.plan = plan ?? DailyPlan.Default;
        shortcutLabels.PropertyChanged += OnShortcutLabelsChanged;

        AgainCommand = new AsyncActionCommand(() => RateAsync(RatingShortcut.F1), () => CanRate);
        HardCommand = new AsyncActionCommand(() => RateAsync(RatingShortcut.F2), () => CanRate);
        GoodCommand = new AsyncActionCommand(() => RateAsync(RatingShortcut.F3), () => CanRate);
        SlashCommand = new AsyncActionCommand(() => SlashAsync(), () => CanRate);
        UndoCommand = new AsyncActionCommand(() => UndoAsync(), () => !IsBusy && lastEventId.HasValue);
        ToggleSynonymsCommand = new AsyncActionCommand(() => ToggleDrawerAsync(Synonyms), () => HasCard && !IsBusy);
        ToggleConfusablesCommand = new AsyncActionCommand(() => ToggleDrawerAsync(Confusables), () => HasCard && !IsBusy);
    }

    public IReadOnlyList<string> ReadingOrder => StableReadingOrder;
    public RelationDrawerViewModel Synonyms { get; }
    public RelationDrawerViewModel Confusables { get; }
    public string Word => current?.Word.Lemma ?? "今日学习已完成";
    public string Phonetic => current?.Word.Phonetic ?? "";
    public string Chinese => current?.Word.Chinese ?? "没有待学习的单词";
    public string ProgressText => current is null ? "今日队列已完成" : "专注当前词 · 提交后进入下一词";
    public bool HasCard => current is not null;
    public bool CanRate => HasCard && !IsBusy;
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

    public ICommand AgainCommand { get; }
    public ICommand HardCommand { get; }
    public ICommand GoodCommand { get; }
    public ICommand SlashCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand ToggleSynonymsCommand { get; }
    public ICommand ToggleConfusablesCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await operations.GetNextCard(plan, ct);
            switch (result)
            {
                case Success<NextCard?> success:
                    SetCurrent(success.Value);
                    AccessibleStatus = success.Value is null ? "今日学习已完成" : $"当前单词 {success.Value.Word.Lemma}";
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { SetFailure("无法加载学习卡", exception.Message); }
        finally { IsBusy = false; }
    }

    public Task RateAsync(RatingShortcut rating, CancellationToken ct = default) =>
        MutateAsync((card, commandId, eventId) => operations.SubmitRating(
            new(commandId, eventId, card.Card, card.Revision, rating, plan), ct), eventIdOnSuccess: true);

    public Task SlashAsync(CancellationToken ct = default) =>
        MutateAsync((card, commandId, eventId) => operations.SlashWord(
            new(commandId, eventId, card.Card, card.Revision, plan), ct), eventIdOnSuccess: true);

    public async Task UndoAsync(CancellationToken ct = default)
    {
        if (IsBusy || lastEventId is not { } eventId) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await operations.UndoLastAction(new(Guid.NewGuid(), eventId), ct);
            switch (result)
            {
                case Success<CardState>:
                    lastEventId = null;
                    Synonyms.Reset();
                    Confusables.Reset();
                    AccessibleStatus = "已撤销上一学习操作";
                    await ReloadAfterUndoAsync(ct);
                    break;
                case StorageFailure<CardState> failure: SetFailure("撤销失败", failure.Message); break;
                case NotFound<CardState> failure: SetFailure("撤销失败", failure.Message); break;
                case Conflict<CardState> failure: SetFailure("撤销失败", failure.Message); break;
            }
        }
        finally { IsBusy = false; }
    }

    public async Task HandleShortcutAsync(ShortcutAction action, CancellationToken ct = default)
    {
        switch (action)
        {
            case ShortcutAction.Again: await RateAsync(RatingShortcut.F1, ct); break;
            case ShortcutAction.Hard: await RateAsync(RatingShortcut.F2, ct); break;
            case ShortcutAction.Good: await RateAsync(RatingShortcut.F3, ct); break;
            case ShortcutAction.Slash: await SlashAsync(ct); break;
            case ShortcutAction.ToggleSynonyms: await ToggleDrawerAsync(Synonyms, ct); break;
            case ShortcutAction.ToggleConfusables: await ToggleDrawerAsync(Confusables, ct); break;
            case ShortcutAction.Undo: await UndoAsync(ct); break;
            default: throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown card action.");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        shortcutLabels.PropertyChanged -= OnShortcutLabelsChanged;
        shortcutLabels.Dispose();
        disposed = true;
    }

    private async Task MutateAsync(
        Func<NextCard, Guid, Guid, Task<UseCaseResult<LearningTransition>>> submit,
        bool eventIdOnSuccess)
    {
        if (!CanRate || current is not { } captured) return;
        IsBusy = true;
        ErrorMessage = null;
        var eventId = Guid.NewGuid();
        try
        {
            var result = await submit(captured, Guid.NewGuid(), eventId);
            switch (result)
            {
                case Success<LearningTransition> success:
                    if (eventIdOnSuccess) lastEventId = eventId;
                    SetCurrent(success.Value.NextCard);
                    Synonyms.Reset();
                    Confusables.Reset();
                    AccessibleStatus = success.Value.NextCard is null
                        ? "学习记录已保存，今日队列已完成"
                        : $"学习记录已保存，下一词 {success.Value.NextCard.Word.Lemma}";
                    break;
                case StorageFailure<LearningTransition> failure: SetFailure("学习记录未保存", failure.Message); break;
                case NotFound<LearningTransition> failure: SetFailure("学习记录未保存", failure.Message); break;
                case Conflict<LearningTransition> failure: SetFailure("学习记录未保存", failure.Message); break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { SetFailure("学习记录未保存", exception.Message); }
        finally { IsBusy = false; }
    }

    private async Task ToggleDrawerAsync(RelationDrawerViewModel drawer, CancellationToken ct = default)
    {
        if (!HasCard || IsBusy || current is null) return;
        if (drawer.IsOpen)
        {
            drawer.Close();
            return;
        }
        var other = ReferenceEquals(drawer, Synonyms) ? Confusables : Synonyms;
        other.Close();
        await drawer.OpenAsync(current.Word.WordId, ct);
    }

    private async Task ReloadAfterUndoAsync(CancellationToken ct)
    {
        var result = await operations.GetNextCard(plan, ct);
        if (result is Success<NextCard?> success) SetCurrent(success.Value);
        else if (result is StorageFailure<NextCard?> failure) SetFailure("已撤销，但无法刷新学习卡", failure.Message);
    }

    private void SetCurrent(NextCard? next)
    {
        current = next;
        OnPropertyChanged(nameof(Word));
        OnPropertyChanged(nameof(Phonetic));
        OnPropertyChanged(nameof(Chinese));
        OnPropertyChanged(nameof(ProgressText));
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
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[] { AgainCommand, HardCommand, GoodCommand, SlashCommand, UndoCommand, ToggleSynonymsCommand, ToggleConfusablesCommand })
            if (command is AsyncActionCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
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

internal sealed class AsyncActionCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public async void Execute(object? parameter) => await execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
