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
    public async Task Rating_forwards_the_exact_current_queue_item_identity()
    {
        var queueItemId = Guid.NewGuid();
        var initial = Card(1, "abate", "/əˈbeɪt/", "减轻") with { QueueItemId = queueItemId };
        SubmitRatingRequest? captured = null;
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        using var viewModel = CreateViewModel(labels, initial,
            submit: (request, _) =>
            {
                captured = request;
                return Task.FromResult<UseCaseResult<LearningTransition>>(
                    new Success<LearningTransition>(new(initial.Card, initial)));
            });
        await viewModel.InitializeAsync();

        await viewModel.RateAsync(RatingShortcut.F3);

        Assert.Equal(queueItemId, captured!.QueueItemId);
    }

    [Fact]
    public void View_model_contract_removes_progress_copy_but_retains_undo_command_and_gesture()
    {
        Assert.Null(typeof(FloatingCardViewModel).GetProperty("ProgressText"));
        Assert.NotNull(typeof(FloatingCardViewModel).GetProperty(nameof(FloatingCardViewModel.UndoCommand)));
        Assert.NotNull(typeof(FloatingCardViewModel).GetProperty(nameof(FloatingCardViewModel.UndoGesture)));
    }

    [Fact]
    public async Task Undo_uses_a_distinct_compensating_event_identity_after_a_successful_rating()
    {
        var initial = Card(1, "abate", "/əˈbeɪt/", "减轻");
        Guid? ratingEventId = null;
        UndoLastActionRequest? undoRequest = null;
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        var operations = new FloatingCardOperations(
            (_, _) => Task.FromResult<UseCaseResult<NextCard?>>(new Success<NextCard?>(initial)),
            (request, _) =>
            {
                ratingEventId = request.EventId;
                return Task.FromResult<UseCaseResult<LearningTransition>>(
                    new Success<LearningTransition>(new(initial.Card, initial)));
            },
            (_, _) => throw new NotSupportedException(),
            (request, _) =>
            {
                undoRequest = request;
                return Task.FromResult<UseCaseResult<CardState>>(new Success<CardState>(initial.Card));
            });
        using var viewModel = new FloatingCardViewModel(
            operations,
            EmptyDrawer("近义辨析", "暂无可靠近义词"),
            EmptyDrawer("形近易混", "暂无可靠易混词"),
            labels);
        await viewModel.InitializeAsync();

        await viewModel.RateAsync(RatingShortcut.F3);
        await viewModel.UndoAsync();

        Assert.NotNull(ratingEventId);
        Assert.NotNull(undoRequest);
        Assert.NotEqual(ratingEventId, undoRequest!.EventId);
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
        var host = AvailableHost();
        using var viewModel = CreateViewModel(labels, Card(1, "abate", "/əˈbeɪt/", "减轻"), actionHost: host);
        var requests = new List<RelationActionRequestedEventArgs>();
        viewModel.ActionRequested += (_, request) => requests.Add(request);
        await viewModel.InitializeAsync();

        viewModel.SpeakCurrentWordCommand.Execute(null);
        viewModel.OpenCurrentDetailsCommand.Execute(null);

        Assert.Equal([RelationActionKind.Speak, RelationActionKind.OpenDetails], requests.Select(x => x.Action));
        Assert.All(requests, request => Assert.Equal("abate", request.Word));
    }

    [Fact]
    public async Task Configured_pronunciation_shortcut_uses_the_same_current_word_action()
    {
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        var spoken = new List<string>();
        var host = new FloatingCardActionHost(offlineSpeech: new FakeActionPort((_, word) =>
        {
            spoken.Add(word);
            return FloatingCardActionResult.Completed("started");
        }));
        using var viewModel = CreateViewModel(labels, Card(1, "abate", "/əˈbeɪt/", "减轻"), actionHost: host);
        await viewModel.InitializeAsync();

        await viewModel.HandleShortcutAsync(ShortcutAction.Pronounce);

        Assert.Equal(["abate"], spoken);
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
    public void Production_action_host_never_claims_success_without_a_registered_capability()
    {
        IFloatingCardActionHost host = FloatingCardActionHost.Unavailable;

        var results = Enum.GetValues<RelationActionKind>()
            .Select(action => host.Dispatch(new(action, Id(1), "abate"))).ToArray();

        Assert.All(Enum.GetValues<RelationActionKind>(), action => Assert.False(host.Capability(action).IsAvailable));
        Assert.All(results, result => Assert.Equal(FloatingCardActionStatus.Unavailable, result.Status));
        Assert.All(results, result => Assert.Contains("功能将在对应离线模块就绪后可用", result.AccessibleMessage));
        Assert.DoesNotContain(results, result => result.AccessibleMessage.Contains("已提交") || result.AccessibleMessage.Contains("已打开"));
    }

    [Fact]
    public async Task Blank_card_details_action_reports_unavailable_truth_or_dispatches_the_registered_owner()
    {
        using var unavailableLabels = new ShortcutLabelMap(new FakeShortcutService());
        using var unavailable = CreateViewModel(unavailableLabels, Card(1, "abate", "/əˈbeɪt/", "减轻"));
        await unavailable.InitializeAsync();

        unavailable.RequestCurrentDetailsFromSurface();

        Assert.Equal("功能将在对应离线模块就绪后可用", unavailable.AccessibleStatus);

        var calls = new List<string>();
        var host = new FloatingCardActionHost(details: new FakeActionPort((_, word) =>
        {
            calls.Add(word);
            return FloatingCardActionResult.Completed($"已打开 {word} 的完整词条");
        }));
        using var availableLabels = new ShortcutLabelMap(new FakeShortcutService());
        using var available = CreateViewModel(availableLabels, Card(1, "abate", "/əˈbeɪt/", "减轻"), actionHost: host);
        await available.InitializeAsync();

        available.RequestCurrentDetailsFromSurface();

        Assert.Equal(["abate"], calls);
        Assert.Equal("已打开 abate 的完整词条", available.AccessibleStatus);
    }

    [Fact]
    public void Registered_action_ports_report_their_real_result_and_observable_side_effect()
    {
        var calls = new List<string>();
        IFloatingCardActionHost host = new FloatingCardActionHost(
            new FakeActionPort((_, word) => { calls.Add($"speak:{word}"); return FloatingCardActionResult.Completed($"已播放 {word}"); }),
            new FakeActionPort((_, word) => { calls.Add($"details:{word}"); return FloatingCardActionResult.Completed($"已打开 {word}"); }),
            new FakeActionPort((_, word) => { calls.Add($"add:{word}"); return FloatingCardActionResult.Completed($"已加入 {word}"); }));

        var results = Enum.GetValues<RelationActionKind>()
            .Select(action => host.Dispatch(new(action, Id(1), "abate"))).ToArray();

        Assert.Equal(["speak:abate", "details:abate", "add:abate"], calls);
        Assert.All(results, result => Assert.Equal(FloatingCardActionStatus.Completed, result.Status));
        Assert.All(Enum.GetValues<RelationActionKind>(), action => Assert.True(host.Capability(action).IsAvailable));
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

    [Fact]
    public async Task Disposal_invalidates_an_ignored_cancellation_reload_after_undo()
    {
        var lateReload = new TaskCompletionSource<UseCaseResult<NextCard?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        var initial = Card(1, "abate", "/əˈbeɪt/", "减轻");
        var operations = new FloatingCardOperations(
            (_, _) => ++loads == 1 ? Task.FromResult<UseCaseResult<NextCard?>>(new Success<NextCard?>(initial)) : lateReload.Task,
            (_, _) => Task.FromResult<UseCaseResult<LearningTransition>>(new Success<LearningTransition>(new(initial.Card, initial))),
            (_, _) => Task.FromResult<UseCaseResult<LearningTransition>>(new Success<LearningTransition>(new(initial.Card, initial))),
            (_, _) => Task.FromResult<UseCaseResult<CardState>>(new Success<CardState>(initial.Card)));
        var viewModel = new FloatingCardViewModel(operations, EmptyDrawer("近义辨析", "暂无可靠近义词"), EmptyDrawer("形近易混", "暂无可靠易混词"), labels);
        await viewModel.InitializeAsync();
        await viewModel.RateAsync(RatingShortcut.F3);
        var undo = viewModel.UndoAsync();

        viewModel.Dispose();
        lateReload.SetResult(new Success<NextCard?>(Card(2, "late", "/late/", "迟")));
        await undo;

        Assert.Equal("abate", viewModel.Word);
    }

    [Fact]
    public async Task Pause_closes_drawers_and_gates_commands_and_shortcuts_without_learning_writes()
    {
        var writes = 0;
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        using var viewModel = CreateViewModel(labels, Card(1, "abate", "/əˈbeɪt/", "减轻"),
            submit: (_, _) => { writes++; throw new InvalidOperationException("must remain gated"); });
        await viewModel.InitializeAsync();
        await viewModel.HandleShortcutAsync(ShortcutAction.ToggleSynonyms);

        viewModel.SetPaused(true);
        await viewModel.HandleShortcutAsync(ShortcutAction.Good);
        viewModel.GoodCommand.Execute(null);

        Assert.True(viewModel.IsPaused);
        Assert.False(viewModel.CanRate);
        Assert.False(viewModel.Synonyms.IsOpen);
        Assert.False(viewModel.ToggleSynonymsCommand.CanExecute(null));
        Assert.Equal(0, writes);
        Assert.Equal("学习已暂停", viewModel.AccessibleStatus);
    }

    [Fact]
    public async Task Autoplay_is_requested_once_for_initial_and_successful_new_cards_but_never_for_failure_or_pause()
    {
        var playback = new RecordingCardPronunciation();
        var first = Card(1, "abate", "/əˈbeɪt/", "减轻");
        var second = Card(2, "bolster", "/ˈbəʊlstə/", "支持");
        int calls = 0;
        using var labels = new ShortcutLabelMap(new FakeShortcutService());
        using var viewModel = CreateViewModel(labels, first,
            submit: (_, _) => ++calls == 1
                ? Task.FromResult<UseCaseResult<LearningTransition>>(new StorageFailure<LearningTransition>("busy"))
                : Task.FromResult<UseCaseResult<LearningTransition>>(new Success<LearningTransition>(new(second.Card, second))),
            pronunciation: playback);

        await viewModel.InitializeAsync();
        await viewModel.RateAsync(RatingShortcut.F3);
        viewModel.SetPaused(true);
        await viewModel.RateAsync(RatingShortcut.F3);
        viewModel.SetPaused(false);
        await viewModel.RateAsync(RatingShortcut.F3);

        Assert.Equal(["abate", "bolster"], playback.CardWords.Where(word => word is not null));
        Assert.Contains(true, playback.PauseStates);
    }

    private static FloatingCardViewModel CreateViewModel(
        ShortcutLabelMap labels,
        NextCard initial,
        Func<SubmitRatingRequest, CancellationToken, Task<UseCaseResult<LearningTransition>>>? submit = null,
        IFloatingCardActionHost? actionHost = null,
        ICardPronunciationPlayback? pronunciation = null)
    {
        var operations = new FloatingCardOperations(
            (_, _) => Task.FromResult<UseCaseResult<NextCard?>>(new Success<NextCard?>(initial)),
            submit ?? ((_, _) => Task.FromResult<UseCaseResult<LearningTransition>>(new Success<LearningTransition>(new(initial.Card, initial)))),
            (_, _) => Task.FromResult<UseCaseResult<LearningTransition>>(new Success<LearningTransition>(new(initial.Card, initial))),
            (_, _) => Task.FromResult<UseCaseResult<CardState>>(new Success<CardState>(initial.Card)));
        var synonyms = EmptyDrawer("近义辨析", "暂无可靠近义词");
        var confusables = EmptyDrawer("形近易混", "暂无可靠易混词");
        return new FloatingCardViewModel(operations, synonyms, confusables, labels, actionHost, pronunciation: pronunciation);
    }

    private static IFloatingCardActionHost AvailableHost() => new FloatingCardActionHost(
        new FakeActionPort((_, word) => FloatingCardActionResult.Completed($"played {word}")),
        new FakeActionPort((_, word) => FloatingCardActionResult.Completed($"opened {word}")),
        new FakeActionPort((_, word) => FloatingCardActionResult.Completed($"added {word}")));

    private static RelationDrawerViewModel EmptyDrawer(string title, string emptyMessage) =>
        new(title, emptyMessage, (_, _) => Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>([])));

    private static NextCard Card(int index, string word, string phonetic, string chinese)
    {
        var state = new CardState(Id(index), null, DateTimeOffset.UtcNow);
        return new(state, Guid.NewGuid(), new VocabularyWord(Id(index), word, index, true, phonetic, chinese),
            null, Id(1000 + index), DailyQueueItemKind.New);
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

    private sealed class FakeActionPort(Func<Guid, string, FloatingCardActionResult> execute) : IFloatingCardActionPort
    {
        public FloatingCardActionResult Execute(Guid wordId, string word) => execute(wordId, word);
    }

    private sealed class RecordingCardPronunciation : ICardPronunciationPlayback
    {
        public List<string?> CardWords { get; } = [];
        public List<bool> PauseStates { get; } = [];
        public event EventHandler<PronunciationPlaybackResult>? PlaybackFeedback { add { } remove { } }
        public void OnCardChanged(Guid? wordId, string? word, bool isPaused) => CardWords.Add(word);
        public void SetPaused(bool paused) => PauseStates.Add(paused);
        public void Stop() => CardWords.Add(null);
    }
}
