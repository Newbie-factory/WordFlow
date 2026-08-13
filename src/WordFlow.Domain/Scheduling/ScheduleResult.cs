namespace WordFlow.Domain.Scheduling;

public sealed record ScheduleResult(
    MemoryState State,
    double RetrievabilityBeforeReview,
    TimeSpan Interval,
    DateTimeOffset DueAt);
