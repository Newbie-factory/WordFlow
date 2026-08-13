namespace WordFlow.Domain.Scheduling;

public interface IFsrsScheduler
{
    ScheduleResult Review(
        MemoryState? previous,
        Rating rating,
        DateTimeOffset reviewedAt,
        double desiredRetention);

    double Retrievability(MemoryState state, DateTimeOffset at);
}
