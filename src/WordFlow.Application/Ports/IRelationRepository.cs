namespace WordFlow.Application.Ports;

public sealed record WordRelation(Guid SourceWordId, Guid TargetWordId, string RelationType);

public sealed record UserRelationOverride
{
    public UserRelationOverride(Guid sourceWordId, Guid targetWordId, string relationType, bool isEnabled)
    {
        if (sourceWordId == Guid.Empty) throw new ArgumentException("A source word ID is required.", nameof(sourceWordId));
        if (targetWordId == Guid.Empty) throw new ArgumentException("A target word ID is required.", nameof(targetWordId));
        if (string.IsNullOrWhiteSpace(relationType)) throw new ArgumentException("A relation type is required.", nameof(relationType));
        SourceWordId = sourceWordId;
        TargetWordId = targetWordId;
        RelationType = relationType.Trim();
        IsEnabled = isEnabled;
    }

    public Guid SourceWordId { get; }
    public Guid TargetWordId { get; }
    public string RelationType { get; }
    public bool IsEnabled { get; }
}

public interface IRelationRepository
{
    Task<Page<WordRelation>> GetRelationsAsync(Guid sourceWordId, PageRequest page, CancellationToken ct);

    Task SetOverrideAsync(UserRelationOverride relationOverride, CancellationToken ct);
}
