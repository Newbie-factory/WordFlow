using WordFlow.Application.Learning;
using WordFlow.Domain.Learning;

namespace WordFlow.Application.Ports;

public interface IDailyQueueStore
{
    Task<DailySessionSnapshot> GetOrCreateAsync(DailyQueueSeed seed, CancellationToken ct);

    Task<DailyQueueItem?> GetNextPendingAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct);

    Task EnsureDueRelearningAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyList<DailyHistoryEntry>> GetHistoryAsync(DateOnly throughDay, int dayCount, CancellationToken ct);

    Task<GoalProgress> GetGoalProgressAsync(CancellationToken ct);
}

public sealed record GoalProgress(int SlashedWords, int TotalWords)
{
    public double Ratio => TotalWords == 0 ? 0 : Math.Clamp((double)SlashedWords / TotalWords, 0, 1);
}
