using WordFlow.App.ViewModels;
using WordFlow.Application;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;
using WordFlow.Domain.Learning;

namespace WordFlow.App.Bootstrap;

internal static class UiSmokeCardFactory
{
    public static FloatingCardViewModel CreateViewModel(IFloatingCardActionHost? actionHost = null)
    {
        actionHost ??= new FloatingCardActionHost(
            CompletedPort((_, word) => $"已播放 {word} 的离线发音"),
            CompletedPort((_, word) => $"已打开 {word} 的完整词条"),
            CompletedPort((_, word) => $"已将 {word} 加入学习"));
        var id = Guid.Parse("6fbb935a-1e9d-4e2f-91db-7f9322c5150a");
        var card = new CardState(id, null, DateTimeOffset.UtcNow);
        var next = new NextCard(card, Guid.Parse("1803b0c0-34a4-4eca-b8c1-84f1499aa101"),
            new VocabularyWord(id, "meticulous", 4242, true, "/məˈtɪkjələs/", "一丝不苟的；极其仔细的", "careful and precise", "a"),
            new CurrentPrimarySense("smoke-primary", "careful and precise", "a", false));
        var operations = new FloatingCardOperations(
            (_, _) => Task.FromResult<UseCaseResult<NextCard?>>(new Success<NextCard?>(next)),
            (_, _) => Task.FromResult<UseCaseResult<LearningTransition>>(new Success<LearningTransition>(new(card, next))),
            (_, _) => Task.FromResult<UseCaseResult<LearningTransition>>(new Success<LearningTransition>(new(card, next))),
            (_, _) => Task.FromResult<UseCaseResult<CardState>>(new Success<CardState>(card)));
        var synonyms = new RelationDrawerViewModel("近义辨析", "暂无可靠近义词", (_, _) =>
            Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>(
                Rows("近义", ["precise", "thorough", "scrupulous", "painstaking", "careful", "exact", "rigorous"]))), actionHost, usesSenseGroups: true);
        var confusables = new RelationDrawerViewModel("形近易混", "暂无可靠易混词", (_, _) =>
            Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>(
                Rows("形近", ["methodical", "metallic", "metaphorical", "miraculous", "methodological", "meticulousness", "metallurgical"]))), actionHost);
        return new(operations, synonyms, confusables, new ShortcutLabelMap(new PreviewShortcutService()), actionHost);
    }

    private static IFloatingCardActionPort CompletedPort(Func<Guid, string, string> action) =>
        new DelegateFloatingCardActionPort((wordId, word) => FloatingCardActionResult.Completed(action(wordId, word)));

    private static IReadOnlyList<RelationItemData> Rows(string badge, IReadOnlyList<string> words) => words
        .Select((word, index) => new RelationItemData(
            Guid.Parse($"0000000{index + 1}-0000-0000-0000-000000000000"),
            word,
            $"/sample-{index + 1}/",
            $"经过核验的中文释义 {index + 1}",
            $"与 meticulous 的用法差异 {index + 1}",
            $"{word} analysis",
            badge,
            false,
            badge == "近义" ? "smoke-primary" : "",
            badge == "近义" ? "a" : "",
            badge == "近义" ? "careful and precise" : ""))
        .ToArray();

    private sealed class PreviewShortcutService : IShortcutService
    {
        public IReadOnlyDictionary<ShortcutAction, ShortcutBinding> Bindings => ShortcutDefaults.All;
        public IReadOnlyList<ShortcutRestoreIssue> RestoreIssues => [];
        public ShortcutLifecycleSnapshot Lifecycle => new(ShortcutLifecycleState.Ready);
        public event EventHandler<ShortcutAction>? ActionInvoked { add { } remove { } }
        public event EventHandler<ShortcutCallbackFaultedEventArgs>? CallbackFaulted { add { } remove { } }
        public event EventHandler<ShortcutBindingChangedEventArgs>? BindingChanged { add { } remove { } }
        public ShortcutRegistrationResult AttachWindowHandle(nint handle) => ShortcutRegistrationResult.Success();
        public bool ProcessWindowMessage(int message, nint id, nint chordData = default) => false;
        public ShortcutRegistrationResult TryReplace(ShortcutAction action, ShortcutBinding candidate) => ShortcutRegistrationResult.Success(candidate);
        public ShortcutRegistrationResult ResetAll() => ShortcutRegistrationResult.Success();
        public ShortcutRestoreResult RestorePersisted() => new([]);
        public void Dispose() { }
    }
}
