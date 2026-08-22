using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;

namespace WordFlow.Application.Learning;

public sealed record GetNextCardRequest(DailyPlan Plan);

public sealed record CurrentPrimarySense(string SenseId, string Definition, string PartOfSpeech, bool IsDeterministicFallback);

public sealed record NextCard(
    CardState Card,
    Guid Revision,
    VocabularyWord Word,
    CurrentPrimarySense? PrimarySense,
    Guid QueueItemId,
    DailyQueueItemKind QueueKind);

public sealed class GetNextCard
{
    private readonly DailyQueueCoordinator coordinator;

    public GetNextCard(DailyQueueCoordinator coordinator) =>
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public IReadOnlyList<Guid> LastBuiltQueue => coordinator.LastBuiltQueue;

    internal Task<CardProjection?> ResolveProjectionAsync(Guid cardId, CancellationToken ct) =>
        coordinator.ResolveProjectionAsync(cardId, ct);

    public async Task<UseCaseResult<NextCard?>> HandleAsync(GetNextCardRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return new Success<NextCard?>(await coordinator.GetNextAsync(request.Plan, ct).ConfigureAwait(false));
        }
        catch (TransientStorageException exception)
        {
            return new StorageFailure<NextCard?>(exception.Message);
        }
    }

}
