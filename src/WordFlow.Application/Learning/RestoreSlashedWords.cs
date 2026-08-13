using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;

namespace WordFlow.Application.Learning;

public sealed record RestoreSlashedWordRequest(Guid CommandId, Guid EventId, Guid CardId, RestoreMode Mode);

public sealed class RestoreSlashedWords
{
    private readonly ILearningStore store;
    private readonly LearningActions actions;

    public RestoreSlashedWords(ILearningStore store, TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        actions = new LearningActions(new UnusedScheduler(), timeProvider);
    }

    public async Task<UseCaseResult<CardState>> HandleAsync(RestoreSlashedWordRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var duplicate = await store.GetCommitAsync(request.CommandId, ct).ConfigureAwait(false);
            if (duplicate is not null) return new Success<CardState>(duplicate.Card);
            var card = await store.GetCardAsync(request.CardId, ct).ConfigureAwait(false);
            if (card is null) return new NotFound<CardState>($"Card {request.CardId:D} was not found.");
            var commit = await store.ApplyAsync(new LearningCommand(
                request.CommandId, actions.Restore(request.EventId, card, request.Mode)), ct).ConfigureAwait(false);
            return new Success<CardState>(commit.Card);
        }
        catch (LearningConcurrencyException exception) { return new Conflict<CardState>(exception.Message); }
        catch (Exception exception) when (ExpectedStorageFailure.Is(exception)) { return new StorageFailure<CardState>(exception.Message); }
    }
}
