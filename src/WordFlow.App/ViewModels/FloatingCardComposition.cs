using WordFlow.Application;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Application.Relations;
using WordFlow.Application.Shortcuts;

namespace WordFlow.App.ViewModels;

public static class FloatingCardComposition
{
    public static FloatingCardViewModel Create(
        GetNextCard getNextCard,
        SubmitRating submitRating,
        SlashWord slashWord,
        UndoLastAction undoLastAction,
        GetSynonyms getSynonyms,
        GetConfusables getConfusables,
        ShortcutLabelMap shortcutLabels,
        IFloatingCardActionHost? actionHost = null,
        ICardPronunciationPlayback? pronunciation = null)
    {
        ArgumentNullException.ThrowIfNull(getNextCard);
        ArgumentNullException.ThrowIfNull(submitRating);
        ArgumentNullException.ThrowIfNull(slashWord);
        ArgumentNullException.ThrowIfNull(undoLastAction);
        ArgumentNullException.ThrowIfNull(getSynonyms);
        ArgumentNullException.ThrowIfNull(getConfusables);
        ArgumentNullException.ThrowIfNull(shortcutLabels);
        actionHost ??= FloatingCardActionHost.Unavailable;

        var operations = new FloatingCardOperations(
            (plan, ct) => getNextCard.HandleAsync(new(plan), ct),
            submitRating.HandleAsync,
            slashWord.HandleAsync,
            undoLastAction.HandleAsync);
        var synonyms = new RelationDrawerViewModel(
            "近义辨析", "暂无可靠近义词",
            async (wordId, ct) => MapSynonyms(await getSynonyms.HandleAsync(new(wordId), ct)), actionHost, usesSenseGroups: true);
        var confusables = new RelationDrawerViewModel(
            "形近易混", "暂无可靠易混词",
            async (wordId, ct) => MapConfusables(await getConfusables.HandleAsync(new(wordId), ct)), actionHost);
        return new(operations, synonyms, confusables, shortcutLabels, actionHost, pronunciation: pronunciation);
    }

    private static UseCaseResult<IReadOnlyList<RelationItemData>> MapSynonyms(
        UseCaseResult<IReadOnlyList<SynonymGroup>> result) => result switch
    {
        Success<IReadOnlyList<SynonymGroup>> success => new Success<IReadOnlyList<RelationItemData>>(
            success.Value.SelectMany(group => group.Words.Select(word => new RelationItemData(
                word.WordId,
                word.Lemma,
                word.Phonetic,
                word.Chinese,
                word.Contrast,
                word.Collocation,
                string.IsNullOrWhiteSpace(group.PartOfSpeech) ? "近义" : $"近义 · {group.PartOfSpeech}",
                false,
                group.SourceSenseId,
                group.PartOfSpeech,
                group.Definition,
                word.TargetSenseId))).ToArray()),
        StorageFailure<IReadOnlyList<SynonymGroup>> failure => new StorageFailure<IReadOnlyList<RelationItemData>>(failure.Message),
        NotFound<IReadOnlyList<SynonymGroup>> failure => new NotFound<IReadOnlyList<RelationItemData>>(failure.Message),
        Conflict<IReadOnlyList<SynonymGroup>> failure => new Conflict<IReadOnlyList<RelationItemData>>(failure.Message),
        _ => throw new InvalidOperationException("Unexpected synonym result."),
    };

    private static UseCaseResult<IReadOnlyList<RelationItemData>> MapConfusables(
        UseCaseResult<IReadOnlyList<ConfusableItem>> result) => result switch
    {
        Success<IReadOnlyList<ConfusableItem>> success => new Success<IReadOnlyList<RelationItemData>>(
            success.Value.Select(item => new RelationItemData(
                item.WordId,
                item.Spelling,
                item.Phonetic,
                item.Chinese,
                item.Contrast,
                item.Collocation,
                BadgeFor(item.RelationType),
                item.RelationType == RelationKinds.Misspelling)).ToArray()),
        StorageFailure<IReadOnlyList<ConfusableItem>> failure => new StorageFailure<IReadOnlyList<RelationItemData>>(failure.Message),
        NotFound<IReadOnlyList<ConfusableItem>> failure => new NotFound<IReadOnlyList<RelationItemData>>(failure.Message),
        Conflict<IReadOnlyList<ConfusableItem>> failure => new Conflict<IReadOnlyList<RelationItemData>>(failure.Message),
        _ => throw new InvalidOperationException("Unexpected confusable result."),
    };

    private static string BadgeFor(string relationType) => relationType switch
    {
        RelationKinds.SpellingSimilar => "形近",
        RelationKinds.PronunciationSimilar => "音近",
        RelationKinds.RootConfusable => "同词根",
        RelationKinds.TopicConfusable => "同主题",
        RelationKinds.AntonymConfusable => "反义易混",
        RelationKinds.PersonalConfusable => "个人易混",
        RelationKinds.Misspelling => "误拼纠正",
        _ => relationType,
    };
}
