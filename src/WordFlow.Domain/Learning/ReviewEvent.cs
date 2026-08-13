namespace WordFlow.Domain.Learning;

public sealed record ReviewEvent(
    Guid EventId,
    Guid CardId,
    DateTimeOffset OccurredAt,
    LearningAction Action,
    CardState Before,
    CardState After,
    Guid? CompensatesEventId = null);
