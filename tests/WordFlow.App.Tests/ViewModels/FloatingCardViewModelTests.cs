using WordFlow.App.ViewModels;
using WordFlow.Application;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;

namespace WordFlow.App.Tests.ViewModels;

public sealed class FloatingCardViewModelTests
{
    [Fact]
    public async Task Exposes_the_nonnegotiable_word_phonetic_chinese_reading_order()
    {
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        using var viewModel = CreateViewModel(labels, Card(1, "abate", "/əˈbeɪt/", "减轻"));

        await viewModel.InitializeAsync();

        Assert.Equal(["Word", "Phonetic", "Chinese"], viewModel.ReadingOrder);
        Assert.Equal("abate", viewModel.Word);
        Assert.Equal("/əˈbeɪt/", viewModel.Phonetic);
        Assert.Equal("减轻", viewModel.Chinese);
    }

    [Fact]
    public async Task Rating_disables_reentry_and_changes_card_only_after_successful_commit()
    {
        var pending = new TaskCompletionSource<UseCaseResult<LearningTransition>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        using var viewModel = CreateViewModel(labels, Card(1, "abate", "/əˈbeɪt/", "减轻"),
            submit: (_, _) => { calls++; return pending.Task; });
        await viewModel.InitializeAsync();

        var first = viewModel.RateAsync(RatingShortcut.F3);
        var second = viewModel.RateAsync(RatingShortcut.F3);

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanRate);
        Assert.Equal("abate", viewModel.Word);
        Assert.Equal(1, calls);

        pending.SetResult(new Success<LearningTransition>(new(
            new CardState(Id(1), null, DateTimeOffset.UtcNow), Card(2, "bolster", "/ˈbəʊlstə/", "支持"))));
        await Task.WhenAll(first, second);

        Assert.Equal("bolster", viewModel.Word);
        Assert.True(viewModel.CanRate);
    }

    [Fact]
    public async Task Failed_rating_retains_card_and_exposes_accessible_error()
    {
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        using var viewModel = CreateViewModel(labels, Card(1, "abate", "/əˈbeɪt/", "减轻"),
            submit: (_, _) => Task.FromResult<UseCaseResult<LearningTransition>>(new StorageFailure<LearningTransition>("database busy")));
        await viewModel.InitializeAsync();

        await viewModel.RateAsync(RatingShortcut.F1);

        Assert.Equal("abate", viewModel.Word);
        Assert.Equal("学习记录未保存：database busy", viewModel.ErrorMessage);
        Assert.Equal(viewModel.ErrorMessage, viewModel.AccessibleStatus);
        Assert.True(viewModel.CanRate);
    }

    [Fact]
    public async Task Unexpected_initial_load_failure_is_announced_instead_of_escaping_the_window_event()
    {
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        var operations = new FloatingCardOperations(
            (_, _) => throw new InvalidDataException("corrupt local corpus"),
            (_, _) => throw new NotSupportedException(),
            (_, _) => throw new NotSupportedException(),
            (_, _) => throw new NotSupportedException());
        using var viewModel = new FloatingCardViewModel(
            operations,
            EmptyDrawer("近义辨析", "暂无可靠近义词"),
            EmptyDrawer("形近易混", "暂无可靠易混词"),
            labels);

        var exception = await Record.ExceptionAsync(() => viewModel.InitializeAsync());

        Assert.Null(exception);
        Assert.Equal("无法加载学习卡：corrupt local corpus", viewModel.AccessibleStatus);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Drawers_are_mutually_exclusive_toggle_without_switching_word_and_close_after_rating()
    {
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        using var viewModel = CreateViewModel(labels, Card(1, "abate", "/əˈbeɪt/", "减轻"));
        await viewModel.InitializeAsync();

        await viewModel.HandleShortcutAsync(ShortcutAction.ToggleSynonyms);
        Assert.True(viewModel.Synonyms.IsOpen);
        Assert.False(viewModel.Confusables.IsOpen);
        await viewModel.HandleShortcutAsync(ShortcutAction.ToggleConfusables);
        Assert.False(viewModel.Synonyms.IsOpen);
        Assert.True(viewModel.Confusables.IsOpen);
        Assert.Equal("abate", viewModel.Word);
        await viewModel.HandleShortcutAsync(ShortcutAction.ToggleConfusables);
        Assert.False(viewModel.Confusables.IsOpen);

        await viewModel.HandleShortcutAsync(ShortcutAction.ToggleSynonyms);
        await viewModel.RateAsync(RatingShortcut.F2);

        Assert.False(viewModel.Synonyms.IsOpen);
        Assert.False(viewModel.Confusables.IsOpen);
    }

    [Fact]
    public void Live_shortcut_labels_flow_to_every_card_action()
    {
        var service = new FakeShortcutService();
        using var labels = new ShortcutLabelMap(service);
        using var viewModel = CreateViewModel(labels, Card(1, "abate", "/əˈbeɪt/", "减轻"));

        service.Replace(ShortcutAction.ToggleSynonyms, "Ctrl+4");
        service.Replace(ShortcutAction.Undo, "Ctrl+Shift+Z");

        Assert.Equal("Ctrl+4", viewModel.SynonymsGesture);
        Assert.Equal("Ctrl+Shift+Z", viewModel.UndoGesture);
    }

    [Fact]
    public async Task Current_word_actions_are_owned_and_publish_word_specific_requests()
    {
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        using var viewModel = CreateViewModel(labels, Card(1, "abate", "/əˈbeɪt/", "减轻"));
        var requests = new List<RelationActionRequestedEventArgs>();
        viewModel.ActionRequested += (_, request) => requests.Add(request);
        await viewModel.InitializeAsync();

        viewModel.SpeakCurrentWordCommand.Execute(null);
        viewModel.OpenCurrentDetailsCommand.Execute(null);

        Assert.Equal([RelationActionKind.Speak, RelationActionKind.OpenDetails], requests.Select(x => x.Action));
        Assert.All(requests, request => Assert.Equal("abate", request.Word));
    }

    [Fact]
    public async Task Unexpected_and_cancelled_command_failures_are_observed_and_announced()
    {
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        using var viewModel = CreateViewModel(labels, Card(1, "abate", "/əˈbeɪt/", "减轻"),
            submit: (_, _) => throw new InvalidOperationException("boom"));
        await viewModel.InitializeAsync();

        var exception = await Record.ExceptionAsync(() => viewModel.RateAsync(RatingShortcut.F1));

        Assert.Null(exception);
        Assert.Contains("boom", viewModel.AccessibleStatus);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public void Production_action_host_dispatches_every_advertised_action_to_an_owner_with_feedback()
    {
        var calls = new List<string>();
        IFloatingCardActionHost host = new FloatingCardActionHost(
            (_, word) => calls.Add($"speak:{word}"),
            (_, word) => calls.Add($"details:{word}"),
            (_, word) => calls.Add($"add:{word}"));

        var results = Enum.GetValues<RelationActionKind>()
            .Select(action => host.Dispatch(new(action, Id(1), "abate"))).ToArray();

        Assert.Equal(["speak:abate", "details:abate", "add:abate"], calls);
        Assert.All(results, result => Assert.False(result.IsError));
        Assert.All(results, result => Assert.Contains("abate", result.AccessibleMessage));
    }

    [Fact]
    public async Task Disposal_invalidates_an_ignored_cancellation_mutation_completion()
    {
        var pending = new TaskCompletionSource<UseCaseResult<LearningTransition>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        var viewModel = CreateViewModel(labels, Card(1, "abate", "/əˈbeɪt/", "减轻"), submit: (_, _) => pending.Task);
        await viewModel.InitializeAsync();
        var operation = viewModel.RateAsync(RatingShortcut.F3);

        viewModel.Dispose();
        pending.SetResult(new Success<LearningTransition>(new(new CardState(Id(2), null, DateTimeOffset.UtcNow), Card(2, "late", "/late/", "迟"))));
        await operation;

        Assert.Equal("abate", viewModel.Word);
    }

    private static FloatingCardViewModel CreateViewModel(
        ShortcutLabelMap labels,
        NextCard initial,
        Func<SubmitRatingRequest, CancellationToken, Task<UseCaseResult<LearningTransition>>>? submit = null)
    {
        var operations = new FloatingCardOperations(
            (_, _) => Task.FromResult<UseCaseResult<NextCard?>>(new Success<NextCard?>(initial)),
            submit ?? ((_, _) => Task.FromResult<UseCaseResult<LearningTransition>>(new Success<LearningTransition>(new(initial.Card, initial)))),
            (_, _) => Task.FromResult<UseCaseResult<LearningTransition>>(new Success<LearningTransition>(new(initial.Card, initial))),
            (_, _) => Task.FromResult<UseCaseResult<CardState>>(new Success<CardState>(initial.Card)));
        var synonyms = EmptyDrawer("近义辨析", "暂无可靠近义词");
        var confusables = EmptyDrawer("形近易混", "暂无可靠易混词");
        return new FloatingCardViewModel(operations, synonyms, confusables, labels);
    }

    private static RelationDrawerViewModel EmptyDrawer(string title, string emptyMessage) =>
        new(title, emptyMessage, (_, _) => Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>([])));

    private static NextCard Card(int index, string word, string phonetic, string chinese)
    {
        var state = new CardState(Id(index), null, DateTimeOffset.UtcNow);
        return new(state, Guid.NewGuid(), new VocabularyWord(Id(index), word, index, true, phonetic, chinese));
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private sealed class FakeShortcutService : IShortcutService
    {
        private readonly Dictionary<ShortcutAction, ShortcutBinding> bindings = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
        public IReadOnlyDictionary<ShortcutAction, ShortcutBinding> Bindings => bindings;
        public IReadOnlyList<ShortcutRestoreIssue> RestoreIssues => [];
        public ShortcutLifecycleSnapshot Lifecycle => new(ShortcutLifecycleState.Ready);
        public event EventHandler<ShortcutAction>? ActionInvoked { add { } remove { } }
        public event EventHandler<ShortcutCallbackFaultedEventArgs>? CallbackFaulted { add { } remove { } }
        public event EventHandler<ShortcutBindingChangedEventArgs>? BindingChanged;
        public ShortcutRegistrationResult AttachWindowHandle(nint handle) => ShortcutRegistrationResult.Success();
        public bool ProcessWindowMessage(int message, nint id, nint chordData = default) => false;
        public ShortcutRegistrationResult TryReplace(ShortcutAction action, ShortcutBinding candidate) => ShortcutRegistrationResult.Success(candidate);
        public ShortcutRegistrationResult ResetAll() => ShortcutRegistrationResult.Success();
        public ShortcutRestoreResult RestorePersisted() => new([]);
        public void Dispose() { }
        public void Replace(ShortcutAction action, string chord)
        {
            var binding = new ShortcutBinding(ShortcutChord.Parse(chord), ShortcutScope.Focused, true);
            bindings[action] = binding;
            BindingChanged?.Invoke(this, new(action, binding));
        }
    }
}
