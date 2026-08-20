using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;

namespace WordFlow.Application.Learning;

public sealed record GetNextCardRequest(DailyPlan Plan);

public sealed record CurrentPrimarySense(string SenseId, string Definition, string PartOfSpeech, bool IsDeterministicFallback);

public sealed record NextCard(CardState Card, Guid Revision, VocabularyWord Word, CurrentPrimarySense? PrimarySense = null);

public sealed class GetNextCard
{
    private readonly ILearningStore store;
    private readonly IVocabularyRepository vocabulary;
    private readonly QueuePolicy queuePolicy;
    private readonly TimeProvider timeProvider;

    public GetNextCard(
        ILearningStore store,
        IVocabularyRepository vocabulary,
        QueuePolicy queuePolicy,
        TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
        this.queuePolicy = queuePolicy ?? throw new ArgumentNullException(nameof(queuePolicy));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public IReadOnlyList<Guid> LastBuiltQueue { get; private set; } = [];

    internal async Task<CardState?> ResolveCardAsync(Guid cardId, CancellationToken ct)
    {
        var persisted = await store.GetCardAsync(cardId, ct).ConfigureAwait(false);
        if (persisted is not null) return persisted;
        var word = await vocabulary.GetWordAsync(cardId, ct).ConfigureAwait(false);
        return word is { IsLearningHeadword: true }
            ? new CardState(cardId, null, timeProvider.GetUtcNow())
            : null;
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

    public async Task<UseCaseResult<NextCard?>> HandleAsync(GetNextCardRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var cards = await PagedReads.AllAsync(store.GetCardsAsync, store.ProbeCardsEndAsync, ct).ConfigureAwait(false);
            var words = await PagedReads.AllAsync(vocabulary.GetWordsAsync, vocabulary.ProbeWordsEndAsync, ct).ConfigureAwait(false);
            var headwords = words.Where(word => word.IsLearningHeadword).ToDictionary(word => word.WordId);
            var knownCards = cards.Select(card => card.Id).ToHashSet();
            var newCandidates = headwords.Keys.Where(id => !knownCards.Contains(id));
            LastBuiltQueue = queuePolicy.Build(new QueueInput(cards, newCandidates, request.Plan, timeProvider.GetUtcNow()));
            if (LastBuiltQueue.Count == 0) return new Success<NextCard?>(null);

            var id = LastBuiltQueue[0];
            if (!headwords.TryGetValue(id, out var word))
            {
                throw new InvalidDataException($"Learning card {id:D} has no vocabulary headword.");
            }
            var card = cards.SingleOrDefault(candidate => candidate.Id == id)
                ?? new CardState(id, null, timeProvider.GetUtcNow());
            var projection = await ResolveProjectionAsync(id, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Queue card {id:D} has no projection.");
            var primarySense = await ResolvePrimarySenseAsync(word, ct).ConfigureAwait(false);
            return new Success<NextCard?>(new NextCard(projection.Card, projection.Revision, word, primarySense));
        }
        catch (TransientStorageException exception)
        {
            return new StorageFailure<NextCard?>(exception.Message);
        }
    }

    private async Task<CurrentPrimarySense?> ResolvePrimarySenseAsync(VocabularyWord word, CancellationToken ct)
    {
        var senses = await PagedReads.AllAsync(
            (page, token) => vocabulary.GetSensesAsync(word.WordId, page, token),
            (offset, snapshot, token) => vocabulary.ProbeSensesEndAsync(word.WordId, offset, snapshot, token), ct).ConfigureAwait(false);
        if (senses.Count == 0)
            return string.IsNullOrWhiteSpace(word.PrimaryDefinition) ? null : new("", word.PrimaryDefinition, word.PrimaryPartOfSpeech, true);

        static string Normalize(string? value) => (value ?? "").Trim().Normalize(System.Text.NormalizationForm.FormC);
        var definition = Normalize(word.PrimaryDefinition);
        var partOfSpeech = Normalize(word.PrimaryPartOfSpeech);
        var matching = senses.FirstOrDefault(sense =>
            definition.Length > 0 && string.Equals(Normalize(sense.Definition), definition, StringComparison.OrdinalIgnoreCase) &&
            (partOfSpeech.Length == 0 || string.Equals(Normalize(sense.PartOfSpeech), partOfSpeech, StringComparison.OrdinalIgnoreCase)));
        if (matching is not null) return new(matching.SenseId, matching.Definition, matching.PartOfSpeech ?? "", false);

        var fallback = senses.OrderBy(sense => sense.SenseId, StringComparer.Ordinal).First();
        return new(fallback.SenseId,
            string.IsNullOrWhiteSpace(word.PrimaryDefinition) ? fallback.Definition : word.PrimaryDefinition,
            string.IsNullOrWhiteSpace(word.PrimaryPartOfSpeech) ? fallback.PartOfSpeech ?? "" : word.PrimaryPartOfSpeech,
            true);
    }
}
