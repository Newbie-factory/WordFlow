namespace WordFlow.Domain.Scheduling;

public sealed record MemoryState(
    double Difficulty,
    double StabilityDays,
    DateTimeOffset LastReviewAt);
