namespace WordFlow.Application.Ports;

public enum RelationDirection
{
    Forward,
    Bidirectional,
}

public sealed record WordRelation(
    Guid SourceWordId,
    Guid TargetWordId,
    string RelationType,
    RelationDirection Direction,
    string? SourceSenseId = null,
    string? TargetSenseId = null,
    string? PartOfSpeech = null,
    string Contrast = "",
    string Collocation = "");

public sealed record MisspellingRelation(string Spelling, Guid TargetWordId, string Contrast = "");

public static class RelationKinds
{
    public const string Synonym = "synonym";
    public const string Antonym = "antonym";
    public const string Derivational = "derivational";
    public const string SpellingSimilar = "spelling_similar";
    public const string PronunciationSimilar = "pronunciation_similar";
    public const string RootConfusable = "root_confusable";
    public const string TopicConfusable = "topic_confusable";
    public const string AntonymConfusable = "antonym_confusable";
    public const string PersonalConfusable = "personal_confusable";
    public const string Misspelling = "misspelling";

    public static IReadOnlySet<string> Confusable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SpellingSimilar, PronunciationSimilar, RootConfusable, TopicConfusable,
        AntonymConfusable, PersonalConfusable,
    };
}

public sealed record UserRelationOverride
{
    public UserRelationOverride(Guid sourceWordId, Guid targetWordId, string relationType, bool isEnabled, DateTimeOffset? updatedAt = null)
    {
        if (sourceWordId == Guid.Empty) throw new ArgumentException("A source word ID is required.", nameof(sourceWordId));
        if (targetWordId == Guid.Empty) throw new ArgumentException("A target word ID is required.", nameof(targetWordId));
        if (string.IsNullOrWhiteSpace(relationType)) throw new ArgumentException("A relation type is required.", nameof(relationType));
        SourceWordId = sourceWordId;
        TargetWordId = targetWordId;
        RelationType = relationType.Trim();
        IsEnabled = isEnabled;
        UpdatedAt = (updatedAt ?? DateTimeOffset.UnixEpoch).ToUniversalTime();
    }

    public Guid SourceWordId { get; }
    public Guid TargetWordId { get; }
    public string RelationType { get; }
    public bool IsEnabled { get; }
    public DateTimeOffset UpdatedAt { get; }
}

public interface IRelationRepository
{
    Task<Page<WordRelation>> GetRelationsAsync(Guid sourceWordId, PageRequest page, CancellationToken ct);

    Task<ExhaustionProbe> ProbeRelationsEndAsync(Guid sourceWordId, int offset, string snapshotId, CancellationToken ct);

    Task<Page<MisspellingRelation>> GetMisspellingsAsync(Guid targetWordId, PageRequest page, CancellationToken ct);

    Task<ExhaustionProbe> ProbeMisspellingsEndAsync(Guid targetWordId, int offset, string snapshotId, CancellationToken ct);

    Task SetOverrideAsync(UserRelationOverride relationOverride, CancellationToken ct);
}
