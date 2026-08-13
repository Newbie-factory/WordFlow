using WordFlow.Domain.Scheduling;

namespace WordFlow.Domain.Learning;

public sealed class LearningActions
{
    private const int MaximumSameDayFailures = 3;
    private static readonly TimeSpan RelearningDelay = TimeSpan.FromMinutes(10);

    private readonly IFsrsScheduler scheduler;
    private readonly TimeProvider timeProvider;

    public LearningActions(IFsrsScheduler scheduler, TimeProvider timeProvider)
    {
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ReviewEvent Review(Guid eventId, CardState card, Rating rating)
    {
        EnsureEventId(eventId);
        ArgumentNullException.ThrowIfNull(card);
        if (card.Slash.IsSlashed)
        {
            throw new InvalidOperationException("A slashed card cannot be reviewed.");
        }

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var schedule = scheduler.Review(card.MemoryState, rating, now, 0.90);
        var after = card with
        {
            MemoryState = schedule.State,
            DueAt = rating == Rating.Again ? now.Add(RelearningDelay) : schedule.DueAt,
        };

        if (rating == Rating.Again)
        {
            after = RecordFailure(after, now);
        }

        return new ReviewEvent(eventId, card.Id, now, ToAction(rating), card, after);
    }

    public ReviewEvent Slash(Guid eventId, CardState card)
    {
        EnsureEventId(eventId);
        ArgumentNullException.ThrowIfNull(card);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        return new ReviewEvent(eventId, card.Id, now, LearningAction.Slash, card, Slash(card, now));
    }

    public ReviewEvent Restore(Guid eventId, CardState card, RestoreMode mode)
    {
        EnsureEventId(eventId);
        ArgumentNullException.ThrowIfNull(card);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var action = mode switch
        {
            RestoreMode.Scheduled => LearningAction.RestoreScheduled,
            RestoreMode.Immediate => LearningAction.RestoreImmediate,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        return new ReviewEvent(eventId, card.Id, now, action, card, Restore(card, mode, now));
    }

    public ReviewEvent Undo(Guid eventId, ReviewEvent original)
    {
        EnsureEventId(eventId);
        ArgumentNullException.ThrowIfNull(original);
        if (eventId == original.EventId)
        {
            throw new ArgumentException("A compensating event requires a distinct event ID.", nameof(eventId));
        }

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        return new ReviewEvent(
            eventId,
            original.CardId,
            now,
            LearningAction.Undo,
            original.After,
            original.Before,
            original.EventId);
    }

    public static CardState Slash(CardState card, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.Slash.IsSlashed)
        {
            throw new InvalidOperationException("The card is already slashed.");
        }

        return card with { Slash = SlashState.At(instant) };
    }

    public static CardState Restore(CardState card, RestoreMode mode, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!card.Slash.IsSlashed)
        {
            throw new InvalidOperationException("Only a slashed card can be restored.");
        }

        var restoredAt = instant.ToUniversalTime();
        if (restoredAt < card.Slash.SlashedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(instant), "A restore cannot precede the slash instant.");
        }

        var dueAt = mode switch
        {
            RestoreMode.Scheduled => card.DueAt,
            RestoreMode.Immediate => restoredAt,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        return card with
        {
            DueAt = dueAt,
            Slash = card.Slash.Restore(restoredAt),
        };
    }

    private static CardState RecordFailure(CardState card, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var count = card.FailureDayUtc == today
            ? Math.Min(card.SameDayFailureCount + 1, MaximumSameDayFailures)
            : 1;

        return card with
        {
            SameDayFailureCount = count,
            FailureDayUtc = today,
            HardWordProtectedUntil = count >= MaximumSameDayFailures
                ? new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero)
                : null,
        };
    }

    private static LearningAction ToAction(Rating rating) => rating switch
    {
        Rating.Again => LearningAction.Again,
        Rating.Hard => LearningAction.Hard,
        Rating.Good => LearningAction.Good,
        _ => throw new ArgumentOutOfRangeException(nameof(rating)),
    };

    private static void EnsureEventId(Guid eventId)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("An event ID cannot be empty.", nameof(eventId));
        }
    }
}
