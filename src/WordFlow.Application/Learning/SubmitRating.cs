using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Application.Learning;

public enum RatingShortcut { F1, F2, F3 }

public sealed record SubmitRatingRequest(
    Guid CommandId,
    Guid EventId,
    CardState ExpectedCard,
    Guid ExpectedRevision,
    Guid QueueItemId,
    RatingShortcut Shortcut,
    DailyPlan? Plan = null);

public sealed record LearningTransition(CardState Card, NextCard? NextCard);

public sealed class SubmitRating
{
    private readonly ILearningStore store;
    private readonly GetNextCard nextCard;
    private readonly LearningActions actions;

    public SubmitRating(ILearningStore store, GetNextCard nextCard, IFsrsScheduler scheduler, TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.nextCard = nextCard ?? throw new ArgumentNullException(nameof(nextCard));
        actions = new LearningActions(scheduler, timeProvider);
    }

    public async Task<UseCaseResult<LearningTransition>> HandleAsync(SubmitRatingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rating = request.Shortcut switch
        {
            RatingShortcut.F1 => Rating.Again,
            RatingShortcut.F2 => Rating.Hard,
            RatingShortcut.F3 => Rating.Good,
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Shortcut, "Only F1, F2, and F3 are rating actions."),
        };
        return await LearningMutation.CommitAndAdvanceAsync(
            store, nextCard, request.CommandId, request.EventId, request.ExpectedCard, request.ExpectedRevision,
            request.QueueItemId, DailyQueueItemStatus.Completed,
            card => actions.Review(request.EventId, card, rating), request.Plan ?? DailyPlan.Default, ct).ConfigureAwait(false);
    }
}
