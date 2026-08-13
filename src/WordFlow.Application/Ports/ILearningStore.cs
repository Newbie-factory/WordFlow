using WordFlow.Domain.Learning;

namespace WordFlow.Application.Ports;

public sealed record LearningCommand
{
    public LearningCommand(Guid commandId, ReviewEvent @event, Guid? expectedRevision = null)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("A command ID cannot be empty.", nameof(commandId));
        }

        CommandId = commandId;
        Event = @event ?? throw new ArgumentNullException(nameof(@event));
        ExpectedRevision = expectedRevision;
    }

    public Guid CommandId { get; }

    public ReviewEvent Event { get; }
    public Guid? ExpectedRevision { get; }
}

public sealed record CommitResult(bool Applied, Guid EventId, CardState Card);

public sealed record CardProjection(CardState Card, Guid Revision)
{
    public static Guid InitialRevision { get; } = Guid.Empty;
}

public sealed record UndoLearningCommand(Guid CommandId, Guid EventId, DateTimeOffset OccurredAt);

public interface ILearningStore
{
    Task<CommitResult> ApplyAsync(LearningCommand command, CancellationToken ct);

    Task<CommitResult?> GetCommitAsync(Guid commandId, CancellationToken ct);

    Task<CardState?> GetCardAsync(Guid cardId, CancellationToken ct);

    Task<CardProjection?> GetCardProjectionAsync(Guid cardId, CancellationToken ct);

    Task<Page<CardState>> GetCardsAsync(PageRequest page, CancellationToken ct);

    Task<ExhaustionProbe> ProbeCardsEndAsync(int offset, string snapshotId, CancellationToken ct);

    Task<ReviewEvent?> GetLatestUndoableEventAsync(CancellationToken ct);

    Task<CommitResult> UndoLatestAsync(UndoLearningCommand command, CancellationToken ct);
}

public sealed class LearningConcurrencyException : InvalidOperationException
{
    public LearningConcurrencyException(Guid cardId)
        : base($"Card {cardId:D} changed after the command was created.")
    {
        CardId = cardId;
    }

    public Guid CardId { get; }
}

public sealed class LearningNotFoundException(string message) : InvalidOperationException(message);

public sealed class TransientStorageException(string message, Exception innerException)
    : IOException(message, innerException);
