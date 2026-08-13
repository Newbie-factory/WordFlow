namespace WordFlow.Domain.Learning;

public sealed record DailyPlan
{
    public DailyPlan(int newLimit, int softReviewLimit)
    {
        if (newLimit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newLimit), "The daily new-card limit cannot be negative.");
        }

        if (softReviewLimit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(softReviewLimit), "The daily soft review target cannot be negative.");
        }

        NewLimit = newLimit;
        SoftReviewLimit = softReviewLimit;
    }

    public static DailyPlan Default { get; } = new(40, 120);

    public int NewLimit { get; }

    public int SoftReviewLimit { get; }
}
