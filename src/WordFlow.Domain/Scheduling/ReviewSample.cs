namespace WordFlow.Domain.Scheduling;

public enum ReviewSampleKind
{
    Rating,
    Slash,
    Other,
}

public sealed record ReviewSample(
    string CommandId,
    string CardId,
    DateTimeOffset ReviewedAt,
    int? RatingValue,
    ReviewSampleKind Kind = ReviewSampleKind.Rating,
    bool IsUndone = false,
    bool IsInverse = false);
