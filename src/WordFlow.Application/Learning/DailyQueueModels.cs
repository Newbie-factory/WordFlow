using WordFlow.Domain.Learning;

namespace WordFlow.Application.Learning;

public enum DailyQueueItemKind
{
    New,
    Review,
    Relearning,
}

public enum DailyQueueItemStatus
{
    Pending,
    Completed,
    Slashed,
    CarriedForward,
}

public sealed record DailyQueueSeed(
    DateOnly LocalDay,
    DailyPlan ConfiguredPlan,
    IReadOnlyList<Guid> OrderedReviewCandidates,
    IReadOnlyList<Guid> OrderedNewCandidates,
    DateTimeOffset CreatedAt);

public sealed record DailyQueueItem(
    Guid ItemId,
    DateOnly LocalDay,
    int Ordinal,
    Guid CardId,
    DailyQueueItemKind Kind,
    DateOnly OriginDay,
    Guid? SourceItemId,
    DailyQueueItemStatus Status,
    Guid? CompletedEventId,
    DateTimeOffset? CompletedAt);

public sealed record DailySessionSnapshot(
    DateOnly LocalDay,
    DailyPlan ConfiguredPlan,
    DailyPlan EffectivePlan,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<DailyQueueItem> Items);

public sealed record DailyHistoryEntry(
    DateOnly LocalDay,
    int NewCompleted,
    int NewTarget,
    int ReviewCompleted,
    int ReviewTarget,
    int PendingRelearning,
    bool WasStarted,
    bool WasCompleted);
