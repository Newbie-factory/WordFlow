using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;

namespace WordFlow.Application.Learning;

public sealed record SlashWordRequest(Guid CommandId, Guid EventId, CardState ExpectedCard, Guid ExpectedRevision, DailyPlan? Plan = null);

public sealed class SlashWord
{
    private readonly ILearningStore store;
    private readonly GetNextCard nextCard;
    private readonly LearningActions actions;

    public SlashWord(ILearningStore store, GetNextCard nextCard, TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.nextCard = nextCard ?? throw new ArgumentNullException(nameof(nextCard));
        actions = new LearningActions(new UnusedScheduler(), timeProvider);
    }

    public Task<UseCaseResult<LearningTransition>> HandleAsync(SlashWordRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return LearningMutation.CommitAndAdvanceAsync(
            store, nextCard, request.CommandId, request.EventId, request.ExpectedCard, request.ExpectedRevision,
            card => actions.Slash(request.EventId, card), request.Plan ?? DailyPlan.Default, ct);
    }
}
