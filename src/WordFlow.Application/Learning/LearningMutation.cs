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
        Guid expectedRevision,
        Guid queueItemId,
        DailyQueueItemStatus terminalStatus,
        Func<CardState, ReviewEvent> createEvent,
        DailyPlan plan,
        CancellationToken ct)
    {
        CommitResult commit;
        try
        {
            var duplicate = await store.GetCommitAsync(commandId, ct).ConfigureAwait(false);
            if (duplicate is not null)
            {
                commit = duplicate;
            }
            else
            {
                var known = await nextCard.ResolveProjectionAsync(expectedCard.Id, ct).ConfigureAwait(false);
                if (known is null) return new NotFound<LearningTransition>($"Card {expectedCard.Id:D} was not found.");
                commit = await store.ApplyQueuedAsync(
                    new LearningCommand(commandId, createEvent(expectedCard), expectedRevision),
                    queueItemId,
                    terminalStatus,
                    ct).ConfigureAwait(false);
            }
        }
        catch (LearningConcurrencyException exception)
        {
            return new Conflict<LearningTransition>(exception.Message);
        }
        catch (TransientStorageException exception)
        {
            return new StorageFailure<LearningTransition>(exception.Message);
        }

        try
        {
            var next = await nextCard.HandleAsync(new GetNextCardRequest(plan), ct).ConfigureAwait(false);
            return next switch
            {
                Success<NextCard?> success => new Success<LearningTransition>(new LearningTransition(commit.Card, success.Value)),
                StorageFailure<NextCard?> failure => CommittedRefreshFailure(commit, failure.Message),
                NotFound<NextCard?> failure => CommittedRefreshFailure(commit, failure.Message),
                Conflict<NextCard?> failure => CommittedRefreshFailure(commit, failure.Message),
                _ => CommittedRefreshFailure(commit, "下一词刷新返回了未知状态。"),
            };
        }
        catch (OperationCanceledException exception)
        {
            return CommittedRefreshFailure(commit,
                string.IsNullOrWhiteSpace(exception.Message) ? "下一词刷新已取消。" : exception.Message);
        }
        catch (Exception exception)
        {
            return CommittedRefreshFailure(commit,
                string.IsNullOrWhiteSpace(exception.Message) ? "下一词暂时无法加载。" : exception.Message);
        }
    }

    private static Success<LearningTransition> CommittedRefreshFailure(CommitResult commit, string message) =>
        new(new LearningTransition(commit.Card, null, NextCardRefreshStatus.Failed, message));
}

internal sealed class UnusedScheduler : IFsrsScheduler
{
    public ScheduleResult Review(MemoryState? previous, Rating rating, DateTimeOffset reviewedAt, double desiredRetention) =>
        throw new InvalidOperationException("This action does not schedule reviews.");
    public double Retrievability(MemoryState state, DateTimeOffset at) =>
        throw new InvalidOperationException("This action does not build queues.");
}
