namespace WordFlow.Domain.Learning;

public sealed record SlashState(
    bool IsSlashed,
    DateTimeOffset? SlashedAt,
    DateTimeOffset? RestoredAt)
{
    public static SlashState Active { get; } = new(false, null, null);

    internal static SlashState At(DateTimeOffset instant) =>
        new(true, instant.ToUniversalTime(), null);

    internal SlashState Restore(DateTimeOffset instant) =>
        new(false, SlashedAt, instant.ToUniversalTime());
}
