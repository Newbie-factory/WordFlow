using WordFlow.Domain.Scheduling;

namespace WordFlow.Domain.Learning;

public sealed record CardState
{
    public CardState(Guid id, MemoryState? memoryState, DateTimeOffset dueAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A card ID cannot be empty.", nameof(id));
        }

        Id = id;
        MemoryState = memoryState;
        DueAt = dueAt.ToUniversalTime();
        Slash = SlashState.Active;
    }

    public Guid Id { get; init; }

    public MemoryState? MemoryState { get; init; }

    public DateTimeOffset DueAt { get; init; }

    public SlashState Slash { get; init; }

    public int SameDayFailureCount { get; init; }

    public DateOnly? FailureDayUtc { get; init; }

    public DateTimeOffset? HardWordProtectedUntil { get; init; }
}
