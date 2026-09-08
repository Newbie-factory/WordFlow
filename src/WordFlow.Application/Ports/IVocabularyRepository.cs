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

public sealed record Page<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    bool? HasMore = null,
    string? SnapshotId = null);

public sealed record ExhaustionProbe(bool IsExhausted, string SnapshotId);

public sealed record VocabularyWord(
    Guid WordId,
    string Lemma,
    int? FrequencyRank,
    bool IsLearningHeadword = true,
    string Phonetic = "",
    string Chinese = "",
    string PrimaryDefinition = "",
    string PrimaryPartOfSpeech = "");

public sealed record VocabularySense(string SenseId, Guid WordId, string Definition, string? PartOfSpeech = null);

public sealed record WordEntry(
    Guid WordId,
    string Lemma,
    string Phonetic,
    string Chinese,
    string? PrimaryDefinition,
    string? PartOfSpeech,
    string? Exchange,
    string? Tags,
    string? Tier,
    int? FrequencyRank,
    int? Collins,
    int? Oxford,
    int? BncRank);

public interface IVocabularyRepository
{
    Task<Page<VocabularyWord>> GetWordsAsync(PageRequest page, CancellationToken ct);

    Task<ExhaustionProbe> ProbeWordsEndAsync(int offset, string snapshotId, CancellationToken ct);

    Task<VocabularyWord?> GetWordAsync(Guid wordId, CancellationToken ct);

    Task<WordEntry?> GetWordEntryAsync(Guid wordId, CancellationToken ct);

    Task<Page<VocabularySense>> GetSensesAsync(Guid wordId, PageRequest page, CancellationToken ct);

    Task<ExhaustionProbe> ProbeSensesEndAsync(Guid wordId, int offset, string snapshotId, CancellationToken ct);
}
