using WordFlow.Application.Ports;

namespace WordFlow.Application.Relations;

public sealed record GetConfusablesRequest(Guid WordId);

public sealed record ConfusableItem(
    Guid? WordId,
    string Spelling,
    string RelationType,
    string Phonetic = "",
    string Chinese = "",
    string Contrast = "",
    string Collocation = "");

public sealed class GetConfusables
{
    private readonly IRelationRepository relations;
    private readonly IVocabularyRepository vocabulary;
    private readonly TimeProvider timeProvider;

    public GetConfusables(IRelationRepository relations, IVocabularyRepository vocabulary, TimeProvider timeProvider)
    {
        this.relations = relations ?? throw new ArgumentNullException(nameof(relations));
        this.vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<UseCaseResult<IReadOnlyList<ConfusableItem>>> HandleAsync(GetConfusablesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = timeProvider.GetUtcNow();
        try
        {
            var source = await vocabulary.GetWordAsync(request.WordId, ct).ConfigureAwait(false);
            if (source is null)
                return new NotFound<IReadOnlyList<ConfusableItem>>($"Word {request.WordId:D} was not found.");
            var all = await PagedReads.AllAsync(
                (page, token) => relations.GetRelationsAsync(request.WordId, page, token),
                (offset, snapshot, token) => relations.ProbeRelationsEndAsync(request.WordId, offset, snapshot, token), ct).ConfigureAwait(false);
            var items = new List<ConfusableItem>();
            foreach (var relation in all.Where(x => RelationKinds.Confusable.Contains(x.RelationType)))
            {
                var target = await vocabulary.GetWordAsync(relation.TargetWordId, ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Relation target {relation.TargetWordId:D} is missing from vocabulary.");
                if (!target.IsLearningHeadword) throw new InvalidDataException("A confusable relation targets a non-headword.");
                items.Add(new ConfusableItem(target.WordId, target.Lemma, relation.RelationType,
                    target.Phonetic, target.Chinese, relation.Contrast, relation.Collocation));
            }
            var misspellings = await PagedReads.AllAsync(
                (page, token) => relations.GetMisspellingsAsync(request.WordId, page, token),
                (offset, snapshot, token) => relations.ProbeMisspellingsEndAsync(request.WordId, offset, snapshot, token), ct).ConfigureAwait(false);
            items.AddRange(misspellings.Select(x => new ConfusableItem(null, $"{x.Spelling} → {source.Lemma}", RelationKinds.Misspelling,
                Chinese: "拼写纠正", Contrast: x.Contrast)));
            var ordered = items.OrderBy(x => x.RelationType, StringComparer.Ordinal)
                .ThenBy(x => x.Spelling, StringComparer.Ordinal)
                .ThenBy(x => x.WordId).ToArray();
            return new Success<IReadOnlyList<ConfusableItem>>(ordered);
        }
        catch (TransientStorageException exception)
        {
            return new StorageFailure<IReadOnlyList<ConfusableItem>>(exception.Message);
        }
    }
}
