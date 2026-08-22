using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;

namespace WordFlow.Application.Learning;

public sealed class DailyQueueCoordinator
{
    private readonly ILearningStore store;
    private readonly IVocabularyRepository vocabulary;
    private readonly IDailyQueueStore dailyQueue;
    private readonly QueuePolicy queuePolicy;
    private readonly TimeProvider timeProvider;

    public DailyQueueCoordinator(
        ILearningStore store,
        IVocabularyRepository vocabulary,
        IDailyQueueStore dailyQueue,
        QueuePolicy queuePolicy,
        TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
        this.dailyQueue = dailyQueue ?? throw new ArgumentNullException(nameof(dailyQueue));
        this.queuePolicy = queuePolicy ?? throw new ArgumentNullException(nameof(queuePolicy));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public DateOnly CurrentLocalDay => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);

    public IReadOnlyList<Guid> LastBuiltQueue { get; private set; } = [];

    public async Task<NextCard?> GetNextAsync(DailyPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var localNow = timeProvider.GetLocalNow();
        var localDay = DateOnly.FromDateTime(localNow.DateTime);
        var now = localNow.ToUniversalTime();
        var cards = await PagedReads.AllAsync(store.GetCardsAsync, store.ProbeCardsEndAsync, ct).ConfigureAwait(false);
        var words = await PagedReads.AllAsync(vocabulary.GetWordsAsync, vocabulary.ProbeWordsEndAsync, ct).ConfigureAwait(false);
        var headwords = words.Where(word => word.IsLearningHeadword).ToDictionary(word => word.WordId);
        var knownCards = cards.Select(card => card.Id).ToHashSet();
        var queuePlan = queuePolicy.BuildPlan(new QueueInput(
            cards,
            headwords.Keys.Where(id => !knownCards.Contains(id)),
            plan,
            now));
        LastBuiltQueue = queuePlan.ReviewCardIds.Concat(queuePlan.NewCardIds).ToArray();

        await dailyQueue.GetOrCreateAsync(new DailyQueueSeed(
            localDay,
            plan,
            queuePlan.ReviewCardIds,
            queuePlan.NewCardIds,
            now), ct).ConfigureAwait(false);
        await dailyQueue.EnsureDueRelearningAsync(localDay, now, ct).ConfigureAwait(false);
        var item = await dailyQueue.GetNextPendingAsync(localDay, now, ct).ConfigureAwait(false);
        if (item is null) return null;

        if (!headwords.TryGetValue(item.CardId, out var word))
            throw new InvalidDataException($"Learning card {item.CardId:D} has no vocabulary headword.");
        var projection = await ResolveProjectionAsync(item.CardId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Queue card {item.CardId:D} has no projection.");
        var primarySense = await ResolvePrimarySenseAsync(word, ct).ConfigureAwait(false);
        return new NextCard(
            projection.Card,
            projection.Revision,
            word,
            primarySense,
            item.ItemId,
            item.Kind);
    }

    internal async Task<CardProjection?> ResolveProjectionAsync(Guid cardId, CancellationToken ct)
    {
        var persisted = await store.GetCardProjectionAsync(cardId, ct).ConfigureAwait(false);
        if (persisted is not null) return persisted;
        var word = await vocabulary.GetWordAsync(cardId, ct).ConfigureAwait(false);
        return word is { IsLearningHeadword: true }
            ? new CardProjection(new CardState(cardId, null, timeProvider.GetUtcNow()), CardProjection.InitialRevision)
            : null;
    }

    private async Task<CurrentPrimarySense?> ResolvePrimarySenseAsync(VocabularyWord word, CancellationToken ct)
    {
        var senses = await PagedReads.AllAsync(
            (page, token) => vocabulary.GetSensesAsync(word.WordId, page, token),
            (offset, snapshot, token) => vocabulary.ProbeSensesEndAsync(word.WordId, offset, snapshot, token), ct).ConfigureAwait(false);
        if (senses.Count == 0)
            return string.IsNullOrWhiteSpace(word.PrimaryDefinition)
                ? null
                : new CurrentPrimarySense("", word.PrimaryDefinition, word.PrimaryPartOfSpeech, true);

        static string Normalize(string? value) => (value ?? "").Trim().Normalize(System.Text.NormalizationForm.FormC);
        var definition = Normalize(word.PrimaryDefinition);
        var partOfSpeech = Normalize(word.PrimaryPartOfSpeech);
        var matching = senses.FirstOrDefault(sense =>
            definition.Length > 0 && string.Equals(Normalize(sense.Definition), definition, StringComparison.OrdinalIgnoreCase) &&
            (partOfSpeech.Length == 0 || string.Equals(Normalize(sense.PartOfSpeech), partOfSpeech, StringComparison.OrdinalIgnoreCase)));
        if (matching is not null)
            return new CurrentPrimarySense(matching.SenseId, matching.Definition, matching.PartOfSpeech ?? "", false);

        var fallback = senses.OrderBy(sense => sense.SenseId, StringComparer.Ordinal).First();
        return new CurrentPrimarySense(
            fallback.SenseId,
            string.IsNullOrWhiteSpace(word.PrimaryDefinition) ? fallback.Definition : word.PrimaryDefinition,
            string.IsNullOrWhiteSpace(word.PrimaryPartOfSpeech) ? fallback.PartOfSpeech ?? "" : word.PrimaryPartOfSpeech,
            true);
    }
}
