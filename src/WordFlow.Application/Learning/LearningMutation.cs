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
        DateOnly expectedQueueDay,
        CancellationToken ct)
    {
        CommitResult commit;
        var committedQueueDay = expectedQueueDay == default ? nextCard.CurrentLocalDay : expectedQueueDay;
        try
        {
            var duplicate = await store.GetCommitAsync(commandId, ct).ConfigureAwait(false);
            if (duplicate is not null)
            {
                commit = duplicate;
            }
            else
            {
                if (expectedQueueDay != default && expectedQueueDay != nextCard.CurrentLocalDay)
                {
                    var rollover = await nextCard.HandleAsync(new GetNextCardRequest(plan), ct).ConfigureAwait(false);
                    if (rollover is not Success<NextCard?> today)
                        return RolloverFailure(rollover);
                    if (today.Value is not { } mapped
                        || mapped.Card.Id != expectedCard.Id
                        || mapped.Revision != expectedRevision
                        || (expectedRevision != CardProjection.InitialRevision && mapped.Card != expectedCard))
                    {
                        return new Success<LearningTransition>(new(
                            expectedCard,
                            today.Value,
                            Status: LearningTransitionStatus.QueueDayRolledOver));
                    }

                    queueItemId = mapped.QueueItemId;
                    committedQueueDay = mapped.QueueDay;
                }

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
            committedQueueDay = commit.QueueDay ?? committedQueueDay;
            var next = await nextCard.HandleAsync(new GetNextCardRequest(plan), ct).ConfigureAwait(false);
            return next switch
            {
                Success<NextCard?> success => new Success<LearningTransition>(new LearningTransition(
                    commit.Card, success.Value, CommittedEventId: commit.EventId, CommittedQueueDay: committedQueueDay)),
                StorageFailure<NextCard?> failure => CommittedRefreshFailure(commit, committedQueueDay, failure.Message),
                NotFound<NextCard?> failure => CommittedRefreshFailure(commit, committedQueueDay, failure.Message),
                Conflict<NextCard?> failure => CommittedRefreshFailure(commit, committedQueueDay, failure.Message),
                _ => CommittedRefreshFailure(commit, committedQueueDay, "下一词刷新返回了未知状态。"),
            };
        }
        catch (OperationCanceledException exception)
        {
            return CommittedRefreshFailure(commit, committedQueueDay,
                string.IsNullOrWhiteSpace(exception.Message) ? "下一词刷新已取消。" : exception.Message);
        }
        catch (Exception exception)
        {
            return CommittedRefreshFailure(commit, committedQueueDay,
                string.IsNullOrWhiteSpace(exception.Message) ? "下一词暂时无法加载。" : exception.Message);
        }
    }

    private static UseCaseResult<LearningTransition> RolloverFailure(UseCaseResult<NextCard?> result) => result switch
    {
        StorageFailure<NextCard?> failure => new StorageFailure<LearningTransition>(failure.Message),
        NotFound<NextCard?> failure => new NotFound<LearningTransition>(failure.Message),
        Conflict<NextCard?> failure => new Conflict<LearningTransition>(failure.Message),
        _ => new StorageFailure<LearningTransition>("日期切换后无法核对今日学习队列。"),
    };

    private static Success<LearningTransition> CommittedRefreshFailure(
        CommitResult commit, DateOnly committedQueueDay, string message) =>
        new(new LearningTransition(commit.Card, null, NextCardRefreshStatus.Failed, message,
            CommittedEventId: commit.EventId, CommittedQueueDay: committedQueueDay));
}

internal sealed class UnusedScheduler : IFsrsScheduler
{
    public ScheduleResult Review(MemoryState? previous, Rating rating, DateTimeOffset reviewedAt, double desiredRetention) =>
        throw new InvalidOperationException("This action does not schedule reviews.");
    public double Retrievability(MemoryState state, DateTimeOffset at) =>
        throw new InvalidOperationException("This action does not build queues.");
}
