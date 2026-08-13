namespace WordFlow.Application.Ports;

public sealed record PageRequest
{
    public PageRequest(int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        Offset = offset;
        Limit = limit;
    }

    public int Offset { get; }

    public int Limit { get; }
}

public sealed record Page<T>(IReadOnlyList<T> Items, int TotalCount);

public sealed record VocabularyWord(Guid WordId, string Lemma, int? FrequencyRank);

public sealed record VocabularySense(string SenseId, Guid WordId, string Definition);

public interface IVocabularyRepository
{
    Task<Page<VocabularyWord>> GetWordsAsync(PageRequest page, CancellationToken ct);

    Task<Page<VocabularySense>> GetSensesAsync(Guid wordId, PageRequest page, CancellationToken ct);
}
