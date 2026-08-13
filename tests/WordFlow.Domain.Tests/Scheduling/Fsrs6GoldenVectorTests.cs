using WordFlow.Domain.Scheduling;

namespace WordFlow.Domain.Tests.Scheduling;

public sealed class Fsrs6GoldenVectorTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<Rating, double, double, double, int> InitialReviewVectors => new()
    {
        { Rating.Again, 6.4133, 0.212, 0.0, 1 },
        { Rating.Hard, 5.112170705601056, 1.2931, 0.0, 1 },
        { Rating.Good, 2.118103970459016, 2.3065, 0.0, 2 },
    };

    [Theory]
    [MemberData(nameof(InitialReviewVectors))]
    public void Initial_review_matches_pinned_fsrs6_reference(
        Rating rating,
        double expectedDifficulty,
        double expectedStability,
        double expectedRetrievability,
        int expectedIntervalDays)
    {
        var result = new Fsrs6Scheduler().Review(null, rating, BaseTime, 0.90);

        AssertClose(expectedDifficulty, result.State.Difficulty);
        AssertClose(expectedStability, result.State.StabilityDays);
        AssertClose(expectedRetrievability, result.RetrievabilityBeforeReview);
        Assert.Equal(TimeSpan.FromDays(expectedIntervalDays), result.Interval);
        Assert.Equal(BaseTime.AddDays(expectedIntervalDays), result.DueAt);
        Assert.Equal(BaseTime, result.State.LastReviewAt);
    }

    [Fact]
    public void Same_day_hard_review_matches_pinned_fsrs6_reference()
    {
        var previous = GoodInitialState();
        var reviewedAt = BaseTime.AddMinutes(10);

        var result = new Fsrs6Scheduler().Review(previous, Rating.Hard, reviewedAt, 0.90);

        AssertClose(4.752858488532557, result.State.Difficulty);
        AssertClose(1.3333787168039835, result.State.StabilityDays);
        AssertClose(0.9995456304854935, result.RetrievabilityBeforeReview);
        Assert.Equal(TimeSpan.FromDays(1), result.Interval);
        Assert.Equal(reviewedAt.AddDays(1), result.DueAt);
    }

    [Fact]
    public void Delayed_good_review_matches_pinned_fsrs6_reference()
    {
        var reviewedAt = BaseTime.AddDays(10).AddHours(12);

        var result = new Fsrs6Scheduler().Review(GoodInitialState(), Rating.Good, reviewedAt, 0.90);

        AssertClose(2.111214235785395, result.State.Difficulty);
        AssertClose(25.63120446574721, result.State.StabilityDays);
        AssertClose(0.7696434024095495, result.RetrievabilityBeforeReview);
        Assert.Equal(TimeSpan.FromDays(26), result.Interval);
    }

    [Fact]
    public void Lapse_matches_pinned_fsrs6_reference()
    {
        var reviewedAt = BaseTime.AddDays(30);

        var result = new Fsrs6Scheduler().Review(GoodInitialState(), Rating.Again, reviewedAt, 0.90);

        AssertClose(7.394502741279718, result.State.Difficulty);
        AssertClose(0.9053468286574414, result.State.StabilityDays);
        AssertClose(0.6675263338730357, result.RetrievabilityBeforeReview);
        Assert.Equal(TimeSpan.FromDays(1), result.Interval);
    }

    [Theory]
    [InlineData(0.85, 4)]
    [InlineData(0.90, 2)]
    [InlineData(0.95, 1)]
    public void Requested_retention_matches_pinned_fsrs6_reference(double retention, int expectedDays)
    {
        var result = new Fsrs6Scheduler().Review(null, Rating.Good, BaseTime, retention);

        Assert.Equal(TimeSpan.FromDays(expectedDays), result.Interval);
        Assert.Equal(BaseTime.AddDays(expectedDays), result.DueAt);
    }

    [Fact]
    public void Elapsed_time_uses_absolute_utc_instants_across_offset_date_boundary()
    {
        var previous = new MemoryState(
            2.118103970459016,
            2.3065,
            new DateTimeOffset(2026, 1, 16, 0, 15, 0, TimeSpan.FromHours(8)));
        var reviewedAt = new DateTimeOffset(2026, 1, 15, 17, 0, 0, TimeSpan.Zero);

        var result = new Fsrs6Scheduler().Review(previous, Rating.Hard, reviewedAt, 0.90);

        AssertClose(0.9979674071534425, result.RetrievabilityBeforeReview);
        AssertClose(1.3333787168039835, result.State.StabilityDays);
        Assert.Equal(TimeSpan.Zero, result.State.LastReviewAt.Offset);
        Assert.Equal(reviewedAt.AddDays(1), result.DueAt);
    }

    [Fact]
    public void Exactly_twenty_four_hours_uses_long_term_formula()
    {
        var result = new Fsrs6Scheduler().Review(GoodInitialState(), Rating.Good, BaseTime.AddDays(1), 0.90);

        AssertClose(7.31530074407728, result.State.StabilityDays);
        AssertClose(0.9468474993825461, result.RetrievabilityBeforeReview);
        Assert.Equal(TimeSpan.FromDays(7), result.Interval);
    }

    [Fact]
    public void Retrievability_at_stability_is_ninety_percent()
    {
        var state = new MemoryState(5.0, 12.75, BaseTime);

        var retrievability = new Fsrs6Scheduler().Retrievability(state, BaseTime.AddDays(12.75));

        AssertClose(0.9, retrievability);
    }

    private static MemoryState GoodInitialState() =>
        new(2.118103970459016, 2.3065, BaseTime);

    private static void AssertClose(double expected, double actual) =>
        Assert.Equal(expected, actual, precision: 12);
}
