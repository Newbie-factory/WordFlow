using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;

namespace WordFlow.Application.Learning;

public sealed record UndoLastActionRequest(Guid CommandId, Guid EventId);

public sealed class UndoLastAction
{
    private readonly ILearningStore store;
    private readonly TimeProvider timeProvider;

    public UndoLastAction(ILearningStore store, TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<UseCaseResult<CardState>> HandleAsync(UndoLastActionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var commit = await store.UndoLatestAsync(new UndoLearningCommand(
                request.CommandId, request.EventId, timeProvider.GetUtcNow()), ct).ConfigureAwait(false);
            return new Success<CardState>(commit.Card);
        }
        catch (LearningNotFoundException exception) { return new NotFound<CardState>(exception.Message); }
        catch (LearningConcurrencyException exception) { return new Conflict<CardState>(exception.Message); }
        catch (TransientStorageException exception) { return new StorageFailure<CardState>(exception.Message); }
    }
}
