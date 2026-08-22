using WordFlow.Domain.Scheduling;

namespace WordFlow.Domain.Learning;

public sealed class QueueInput
{
    public QueueInput(
        IEnumerable<CardState> dueCards,
        IEnumerable<Guid> newCandidates,
        DailyPlan dailyPlan,
        DateTimeOffset currentInstant)
    {
        ArgumentNullException.ThrowIfNull(dueCards);
        ArgumentNullException.ThrowIfNull(newCandidates);

        DueCards = dueCards.ToArray();
        if (DueCards.Any(card => card is null))
        {
            throw new ArgumentException("Due cards cannot contain null snapshots.", nameof(dueCards));
        }

        foreach (var group in DueCards.GroupBy(card => card.Id))
        {
            var snapshot = group.First();
            if (group.Skip(1).Any(candidate => candidate != snapshot))
            {
                throw new ArgumentException(
                    $"Card {group.Key} has conflicting due-card snapshots.",
                    nameof(dueCards));
            }
        }

        NewCandidates = newCandidates.ToArray();
        DailyPlan = dailyPlan ?? throw new ArgumentNullException(nameof(dailyPlan));
        CurrentInstant = currentInstant.ToUniversalTime();
    }

    public IReadOnlyList<CardState> DueCards { get; }

    public IReadOnlyList<Guid> NewCandidates { get; }

    public DailyPlan DailyPlan { get; }

    public DateTimeOffset CurrentInstant { get; }
}

public sealed class QueuePolicy
{
    private readonly IFsrsScheduler scheduler;

    public QueuePolicy(IFsrsScheduler scheduler)
    {
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    public IReadOnlyList<Guid> Build(QueueInput input)
    {
        var plan = BuildPlan(input);
        return plan.ReviewCardIds.Concat(plan.NewCardIds).ToArray();
    }

    public DailyQueuePlan BuildPlan(QueueInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var reviews = input.DueCards
            .Where(card => IsAvailable(card, input.CurrentInstant))
            .GroupBy(card => card.Id)
            .Select(group => group
                .OrderBy(card => card.DueAt)
                .ThenBy(card => card.MemoryState?.LastReviewAt)
                .First())
            .Select(card => new
            {
                Card = card,
                Risk = card.MemoryState is null
                    ? 1.0
                    : 1.0 - scheduler.Retrievability(card.MemoryState, input.CurrentInstant),
            })
            .OrderByDescending(entry => entry.Risk)
            .ThenBy(entry => entry.Card.DueAt)
            .ThenBy(entry => entry.Card.Id)
            .Take(input.DailyPlan.SoftReviewLimit)
            .Select(entry => entry.Card.Id)
            .ToArray();

        var included = input.DueCards.Select(card => card.Id).ToHashSet();
        var newCards = input.NewCandidates
            .Where(id => id != Guid.Empty && included.Add(id))
            .Distinct()
            .OrderBy(id => id)
            .Take(input.DailyPlan.NewLimit)
            .ToArray();

        return new DailyQueuePlan(reviews, newCards);
    }

    private static bool IsAvailable(CardState card, DateTimeOffset now) =>
        !card.Slash.IsSlashed
        && card.DueAt <= now
        && (card.HardWordProtectedUntil is null || card.HardWordProtectedUntil <= now);
}
