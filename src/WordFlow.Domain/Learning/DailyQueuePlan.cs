namespace WordFlow.Domain.Learning;

public sealed record DailyQueuePlan(
    IReadOnlyList<Guid> ReviewCardIds,
    IReadOnlyList<Guid> NewCardIds);
