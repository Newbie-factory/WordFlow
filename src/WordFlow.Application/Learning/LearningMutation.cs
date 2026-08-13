using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Application.Learning;

internal static class LearningMutation
{
    public static async Task<UseCaseResult<LearningTransition>> CommitAndAdvanceAsync(
        ILearningStore store,
        GetNextCard nextCard,
        Guid commandId,
        Guid eventId,
        CardState expectedCard,
        Func<CardState, ReviewEvent> createEvent,
        DailyPlan plan,
        CancellationToken ct)
    {
        try
        {
            var duplicate = await store.GetCommitAsync(commandId, ct).ConfigureAwait(false);
            CommitResult commit;
            if (duplicate is not null)
            {
                commit = duplicate;
            }
            else
            {
                var known = await nextCard.ResolveCardAsync(expectedCard.Id, ct).ConfigureAwait(false);
                if (known is null) return new NotFound<LearningTransition>($"Card {expectedCard.Id:D} was not found.");
                commit = await store.ApplyAsync(new LearningCommand(commandId, createEvent(expectedCard)), ct).ConfigureAwait(false);
            }

            var next = await nextCard.HandleAsync(new GetNextCardRequest(plan), ct).ConfigureAwait(false);
            return next switch
            {
                Success<NextCard?> success => new Success<LearningTransition>(new LearningTransition(commit.Card, success.Value)),
                StorageFailure<NextCard?> failure => new StorageFailure<LearningTransition>(failure.Message),
                _ => throw new InvalidOperationException("Unexpected next-card result."),
            };
        }
        catch (LearningConcurrencyException exception)
        {
            return new Conflict<LearningTransition>(exception.Message);
        }
        catch (TransientStorageException exception)
        {
            return new StorageFailure<LearningTransition>(exception.Message);
        }
    }
}

internal sealed class UnusedScheduler : IFsrsScheduler
{
    public ScheduleResult Review(MemoryState? previous, Rating rating, DateTimeOffset reviewedAt, double desiredRetention) =>
        throw new InvalidOperationException("This action does not schedule reviews.");
    public double Retrievability(MemoryState state, DateTimeOffset at) =>
        throw new InvalidOperationException("This action does not build queues.");
}
