using WordFlow.Domain.Scheduling;

namespace WordFlow.Domain.Tests.Scheduling;

public sealed class Fsrs6InvariantTests
{
    private static readonly (double Lower, double Upper)[] OfficialParameterBounds =
    [
        (0.001, 100.0), (0.001, 100.0), (0.001, 100.0), (0.001, 100.0),
        (1.0, 10.0), (0.001, 4.0), (0.001, 4.0), (0.001, 0.75),
        (0.0, 4.5), (0.0, 0.8), (0.001, 3.5), (0.001, 5.0),
        (0.001, 0.25), (0.001, 0.9), (0.0, 4.0), (0.0, 1.0),
        (1.0, 6.0), (0.0, 2.0), (0.0, 2.0), (0.0, 0.8), (0.1, 0.8),
    ];

    public static TheoryData<int, double> OfficialAcceptedParameterValues
    {
        get
        {
            var data = new TheoryData<int, double>();
            for (var index = 0; index < OfficialParameterBounds.Length; index++)
            {
                data.Add(index, OfficialParameterBounds[index].Lower);
                data.Add(index, OfficialParameterBounds[index].Upper);
            }

            return data;
        }
    }

    public static TheoryData<int, double, double, double> OfficialRejectedParameterValues
    {
        get
        {
            var data = new TheoryData<int, double, double, double>();
            for (var index = 0; index < OfficialParameterBounds.Length; index++)
            {
                var (lower, upper) = OfficialParameterBounds[index];
                data.Add(index, Math.BitDecrement(lower), lower, upper);
                data.Add(index, Math.BitIncrement(upper), lower, upper);
            }

            return data;
        }
    }

    [Fact]
    public void Ten_thousand_deterministic_histories_preserve_scheduler_invariants()
    {
        var scheduler = new Fsrs6Scheduler();
        var random = new Random(0x5F525336);
        var epoch = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var history = 0; history < 10_000; history++)
        {
            MemoryState? state = null;
            var reviewedAt = epoch.AddMinutes(history);
            var reviewCount = 1 + random.Next(20);

            for (var review = 0; review < reviewCount; review++)
            {
                reviewedAt = reviewedAt.AddMinutes(random.Next(0, 60 * 24 * 180));
                var rating = (Rating)random.Next(1, 4);
                var retention = 0.85 + (random.Next(11) * 0.01);

                var result = scheduler.Review(state, rating, reviewedAt, retention);
                var lowerRetention = scheduler.Review(state, rating, reviewedAt, 0.85);
                var higherRetention = scheduler.Review(state, rating, reviewedAt, 0.95);

                Assert.True(double.IsFinite(result.State.Difficulty));
                Assert.True(double.IsFinite(result.State.StabilityDays));
                Assert.True(double.IsFinite(result.RetrievabilityBeforeReview));
                Assert.InRange(result.State.Difficulty, 1.0, 10.0);
                Assert.True(result.State.StabilityDays > 0.0);
                Assert.InRange(result.RetrievabilityBeforeReview, 0.0, 1.0);
                Assert.True(result.Interval >= TimeSpan.Zero);
                Assert.True(result.DueAt >= result.State.LastReviewAt);
                Assert.Equal(TimeSpan.Zero, result.State.LastReviewAt.Offset);
                Assert.Equal(TimeSpan.Zero, result.DueAt.Offset);
                Assert.True(lowerRetention.DueAt >= higherRetention.DueAt);

                state = result.State;
            }
        }
    }

    [Fact]
    public void Higher_retention_never_schedules_later_for_same_review()
    {
        var scheduler = new Fsrs6Scheduler();
        var previous = new MemoryState(
            4.25,
            40.5,
            new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var reviewedAt = previous.LastReviewAt.AddDays(45.75);

        var low = scheduler.Review(previous, Rating.Good, reviewedAt, 0.85);
        var normal = scheduler.Review(previous, Rating.Good, reviewedAt, 0.90);
        var high = scheduler.Review(previous, Rating.Good, reviewedAt, 0.95);

        Assert.True(low.DueAt >= normal.DueAt);
        Assert.True(normal.DueAt >= high.DueAt);
    }

    [Fact]
    public void Trainable_decay_changes_curve_shape_but_preserves_stability_definition()
    {
        var values = FsrsParameters.Default.Values.ToArray();
        values[20] = 0.5;
        var custom = new Fsrs6Scheduler(new FsrsParameters(values));
        var standard = new Fsrs6Scheduler();
        var lastReviewAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var state = new MemoryState(5.0, 10.0, lastReviewAt);

        var standardHalfStability = standard.Retrievability(state, lastReviewAt.AddDays(5));
        var customHalfStability = custom.Retrievability(state, lastReviewAt.AddDays(5));

        Assert.NotEqual(standardHalfStability, customHalfStability);
        Assert.Equal(0.9, custom.Retrievability(state, lastReviewAt.AddDays(10)), precision: 12);
    }

    [Fact]
    public void Invalid_inputs_are_rejected()
    {
        var scheduler = new Fsrs6Scheduler();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var valid = new MemoryState(5.0, 2.0, at);

        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Review(null, Rating.Good, at, 0.849999));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Review(null, Rating.Good, at, 0.950001));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Review(null, (Rating)4, at, 0.90));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Review(valid, Rating.Good, at.AddTicks(-1), 0.90));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Retrievability(valid, at.AddTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Review(new MemoryState(5, double.NaN, at), Rating.Good, at, 0.90));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Review(new MemoryState(double.PositiveInfinity, 2, at), Rating.Good, at, 0.90));
    }

    [Fact]
    public void Parameters_require_exactly_twenty_one_finite_values()
    {
        Assert.Equal(21, FsrsParameters.Default.Values.Count);
        Assert.Throws<ArgumentException>(() => new FsrsParameters(new double[20]));

        var nonFinite = FsrsParameters.Default.Values.ToArray();
        nonFinite[20] = double.NaN;
        Assert.Throws<ArgumentOutOfRangeException>(() => new FsrsParameters(nonFinite));
    }

    [Theory]
    [MemberData(nameof(OfficialAcceptedParameterValues))]
    public void Every_official_parameter_boundary_is_accepted(int index, double boundary)
    {
        var values = FsrsParameters.Default.Values.ToArray();
        values[index] = boundary;

        var parameters = new FsrsParameters(values);

        Assert.Equal(boundary, parameters.Values[index]);
    }

    [Theory]
    [MemberData(nameof(OfficialRejectedParameterValues))]
    public void Every_parameter_value_just_outside_official_bounds_is_rejected(
        int index,
        double value,
        double lower,
        double upper)
    {
        AssertParameterRangeError(index, value, lower, upper);
    }

    [Fact]
    public void Parameter_values_are_immutable_snapshots()
    {
        var source = FsrsParameters.Default.Values.ToArray();
        var parameters = new FsrsParameters(source);

        source[0] = 999;

        Assert.Equal(0.212, parameters.Values[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<double>)parameters.Values)[0] = 999);
    }

    [Fact]
    public void Due_instant_outside_datetimeoffset_range_is_rejected_instead_of_wrapping()
    {
        var scheduler = new Fsrs6Scheduler();
        var reviewedAt = DateTimeOffset.MaxValue.AddDays(-1);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => scheduler.Review(null, Rating.Good, reviewedAt, 0.90));
    }

    [Fact]
    public void Persisted_stability_accepts_official_minimum_and_rejects_just_below_it()
    {
        var scheduler = new Fsrs6Scheduler();
        var reviewedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var atMinimum = new MemoryState(5.0, 0.001, reviewedAt);
        var belowMinimum = new MemoryState(5.0, Math.BitDecrement(0.001), reviewedAt);

        Assert.Equal(1.0, scheduler.Retrievability(atMinimum, reviewedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Retrievability(belowMinimum, reviewedAt));
    }

    private static void AssertParameterRangeError(int index, double value, double lower, double upper)
    {
        var values = FsrsParameters.Default.Values.ToArray();
        values[index] = value;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new FsrsParameters(values));

        Assert.Contains($"index {index}", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"value {value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"[{lower.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, {upper.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}]", exception.Message, StringComparison.Ordinal);
    }
}
