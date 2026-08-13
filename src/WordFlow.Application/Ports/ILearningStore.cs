using WordFlow.Domain.Learning;

namespace WordFlow.Application.Ports;

public sealed record LearningCommand
{
    public LearningCommand(Guid commandId, ReviewEvent @event)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("A command ID cannot be empty.", nameof(commandId));
        }

        CommandId = commandId;
        Event = @event ?? throw new ArgumentNullException(nameof(@event));
    }

    public Guid CommandId { get; }

    public ReviewEvent Event { get; }
}

public sealed record CommitResult(bool Applied, Guid EventId, CardState Card);

public interface ILearningStore
{
    Task<CommitResult> ApplyAsync(LearningCommand command, CancellationToken ct);

    Task<CommitResult?> GetCommitAsync(Guid commandId, CancellationToken ct);

    Task<CardState?> GetCardAsync(Guid cardId, CancellationToken ct);

    Task<Page<CardState>> GetCardsAsync(PageRequest page, CancellationToken ct);

    Task<ReviewEvent?> GetLatestUndoableEventAsync(CancellationToken ct);
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
