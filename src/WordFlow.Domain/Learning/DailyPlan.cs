namespace WordFlow.Domain.Learning;

public sealed record DailyPlan
{
    public DailyPlan(int newLimit, int reviewLimit, bool isReviewLimitSoft = true)
    {
        if (newLimit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newLimit), "The daily new-card limit cannot be negative.");
        }

        if (reviewLimit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reviewLimit), "The daily review limit cannot be negative.");
        }

        NewLimit = newLimit;
        ReviewLimit = reviewLimit;
        IsReviewLimitSoft = isReviewLimitSoft;
    }

    public static DailyPlan Default { get; } = new(40, 120);

    public int NewLimit { get; }

    public int ReviewLimit { get; }

    public bool IsReviewLimitSoft { get; }
}
