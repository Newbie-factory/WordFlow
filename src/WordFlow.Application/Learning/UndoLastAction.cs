using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;

namespace WordFlow.Application.Learning;

public sealed record UndoLastActionRequest(Guid CommandId, Guid EventId);

public sealed class UndoLastAction
{
    private readonly ILearningStore store;
    private readonly LearningActions actions;

    public UndoLastAction(ILearningStore store, TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        actions = new LearningActions(new UnusedScheduler(), timeProvider);
    }

    public async Task<UseCaseResult<CardState>> HandleAsync(UndoLastActionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var duplicate = await store.GetCommitAsync(request.CommandId, ct).ConfigureAwait(false);
            if (duplicate is not null) return new Success<CardState>(duplicate.Card);
            var original = await store.GetLatestUndoableEventAsync(ct).ConfigureAwait(false);
            if (original is null) return new NotFound<CardState>("There is no action to undo.");
            var commit = await store.ApplyAsync(new LearningCommand(
                request.CommandId, actions.Undo(request.EventId, original)), ct).ConfigureAwait(false);
            return new Success<CardState>(commit.Card);
        }
        catch (LearningConcurrencyException exception) { return new Conflict<CardState>(exception.Message); }
        catch (Exception exception) when (ExpectedStorageFailure.Is(exception)) { return new StorageFailure<CardState>(exception.Message); }
    }
}
