using WordFlow.Application.Ports;

namespace WordFlow.Application.Relations;

public sealed record GetSynonymsRequest(Guid WordId);

public sealed record RelationWord(Guid WordId, string Lemma);

public sealed record SynonymGroup(
    string SourceSenseId,
    string PartOfSpeech,
    string Definition,
    IReadOnlyList<RelationWord> Words);

public sealed class GetSynonyms
{
    private readonly IRelationRepository relations;
    private readonly IVocabularyRepository vocabulary;
    private readonly TimeProvider timeProvider;

    public GetSynonyms(IRelationRepository relations, IVocabularyRepository vocabulary, TimeProvider timeProvider)
    {
        this.relations = relations ?? throw new ArgumentNullException(nameof(relations));
        this.vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<UseCaseResult<IReadOnlyList<SynonymGroup>>> HandleAsync(GetSynonymsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = timeProvider.GetUtcNow();
        try
        {
            if (await vocabulary.GetWordAsync(request.WordId, ct).ConfigureAwait(false) is null)
                return new NotFound<IReadOnlyList<SynonymGroup>>($"Word {request.WordId:D} was not found.");
            var allRelations = await PagedReads.AllAsync(
                (page, token) => relations.GetRelationsAsync(request.WordId, page, token), ct).ConfigureAwait(false);
            var synonyms = allRelations.Where(relation => relation.RelationType == RelationKinds.Synonym).ToArray();
            if (synonyms.Length == 0) return new Success<IReadOnlyList<SynonymGroup>>([]);
            if (synonyms.Any(x => string.IsNullOrWhiteSpace(x.SourceSenseId) || string.IsNullOrWhiteSpace(x.TargetSenseId) || string.IsNullOrWhiteSpace(x.PartOfSpeech)))
                throw new InvalidDataException("A verified synonym is missing its sense/POS evidence.");

            var senses = await PagedReads.AllAsync(
                (page, token) => vocabulary.GetSensesAsync(request.WordId, page, token), ct).ConfigureAwait(false);
            var senseById = senses.ToDictionary(x => x.SenseId, StringComparer.Ordinal);
            var targetWords = new Dictionary<Guid, VocabularyWord>();
            foreach (var id in synonyms.Select(x => x.TargetWordId).Distinct())
            {
                targetWords[id] = await vocabulary.GetWordAsync(id, ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Relation target {id:D} is missing from vocabulary.");
            }

            var groups = synonyms
                .GroupBy(x => (x.SourceSenseId!, x.PartOfSpeech!))
                .OrderBy(x => x.Key.Item1, StringComparer.Ordinal)
                .ThenBy(x => x.Key.Item2, StringComparer.Ordinal)
                .Select(group =>
                {
                    if (!senseById.TryGetValue(group.Key.Item1, out var sense))
                        throw new InvalidDataException($"Source sense '{group.Key.Item1}' is missing.");
                    return new SynonymGroup(
                        group.Key.Item1,
                        group.Key.Item2,
                        sense.Definition,
                        group.Select(x => targetWords[x.TargetWordId])
                            .DistinctBy(x => x.WordId)
                            .OrderBy(x => x.Lemma, StringComparer.Ordinal)
                            .ThenBy(x => x.WordId)
                            .Select(x => new RelationWord(x.WordId, x.Lemma)).ToArray());
                }).ToArray();
            return new Success<IReadOnlyList<SynonymGroup>>(groups);
        }
        catch (Exception exception) when (ExpectedStorageFailure.Is(exception))
        {
            return new StorageFailure<IReadOnlyList<SynonymGroup>>(exception.Message);
        }
    }
}
