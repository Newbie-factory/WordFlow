using WordFlow.Application.Ports;
using WordFlow.Domain.Scheduling;
using WordFlow.Infrastructure.Scheduling;

namespace WordFlow.Infrastructure.Tests.Scheduling;

public sealed class FsrsParameterOptimizerTests
{
    [Fact]
    public void Exactly_399_valid_reviews_are_not_eligible_but_400_are()
    {
        var optimizer = new FsrsParameterOptimizer(searchRounds: 1);

        var below = optimizer.Optimize(SyntheticSamples(399), FsrsParameters.Default, CancellationToken.None);
        var exact = optimizer.Optimize(SyntheticSamples(400), FsrsParameters.Default, CancellationToken.None);

        Assert.Equal(OptimizationStatus.NotEligible, below.Status);
        Assert.Equal(399, below.SampleCount);
        Assert.NotEqual(OptimizationStatus.NotEligible, exact.Status);
        Assert.Equal(400, exact.SampleCount);
    }

    [Fact]
    public void Eligibility_excludes_undone_inverse_slash_other_duplicate_invalid_rating_and_nonmonotonic_reviews()
    {
        var samples = SyntheticSamples(400).ToList();
        var at = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        samples.Add(new("undone", "bad-a", at, 3, ReviewSampleKind.Rating, IsUndone: true));
        samples.Add(new("inverse", "bad-b", at, 3, ReviewSampleKind.Rating, IsInverse: true));
        samples.Add(new("slash", "bad-c", at, null, ReviewSampleKind.Slash));
        samples.Add(new("other", "bad-d", at, null, ReviewSampleKind.Other));
        samples.Add(new("invalid-rating", "bad-e", at, 4));
        samples.Add(samples[0] with { CardId = "duplicate-command" });
        samples.Add(new("late", "clock", at.AddDays(2), 3));
        samples.Add(new("backward", "clock", at.AddDays(1), 3));

        var result = new FsrsParameterOptimizer(searchRounds: 1)
            .Optimize(samples, FsrsParameters.Default, CancellationToken.None);

        Assert.Equal(400, result.SampleCount);
        Assert.NotEqual(OptimizationStatus.NotEligible, result.Status);
    }

    [Fact]
    public void Optimization_is_deterministic_finite_and_bounded()
    {
        var samples = SyntheticSamples(480);
        var first = new FsrsParameterOptimizer(seed: 1776, searchRounds: 2)
            .Optimize(samples, FsrsParameters.Default, CancellationToken.None);
        var second = new FsrsParameterOptimizer(seed: 1776, searchRounds: 2)
            .Optimize(samples, FsrsParameters.Default, CancellationToken.None);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.TrainingLoss, second.TrainingLoss, 12);
        Assert.Equal(first.ValidationLoss, second.ValidationLoss, 12);
        Assert.Equal(first.Parameters.Values, second.Parameters.Values);
        Assert.Equal(OptimizationStatus.Accepted, first.Status);
        Assert.NotEqual(FsrsParameters.Default.Values, first.Parameters.Values);
        Assert.True(first.TrainingLoss < first.BaselineTrainingLoss);
        Assert.True(first.ValidationLoss <= first.BaselineValidationLoss + FsrsParameterOptimizer.ValidationTolerance);
        Assert.Equal(21, first.Parameters.Values.Count);
        Assert.All(first.Parameters.Values, value => Assert.True(double.IsFinite(value)));
        _ = new FsrsParameters(first.Parameters.Values);
    }

    [Fact]
    public void Chronological_partition_uses_whole_card_histories_and_drops_boundary_crossers()
    {
        var samples = InterleavedBoundarySamples();

        var partition = FsrsParameterOptimizer.CreateEvaluationPartition(samples, validationFraction: 0.20);

        Assert.Empty(partition.Training.Select(x => x.CardId).Intersect(partition.Validation.Select(x => x.CardId)));
        Assert.True(partition.Training.Max(x => x.ReviewedAt) < partition.Validation.Min(x => x.ReviewedAt));
        Assert.DoesNotContain(partition.Training, x => x.CardId == "crossing");
        Assert.DoesNotContain(partition.Validation, x => x.CardId == "crossing");
    }

    [Fact]
    public void Binary_recall_log_loss_matches_frozen_continuous_time_fixture()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var fixture = new[]
        {
            new ReviewSample("1", "fixture", start, 3),
            new ReviewSample("2", "fixture", start.AddDays(10.5), 3),
            new ReviewSample("3", "fixture", start.AddDays(40.5), 1),
        };

        var evaluation = FsrsParameterOptimizer.EvaluateLogLoss(fixture, FsrsParameters.Default);

        Assert.Equal(2, evaluation.ObservationCount);
        Assert.Equal(1.2292513965155736, evaluation.Loss, 12);
        Assert.Equal(0.7696434024095495, evaluation.Predictions[0], 12);
        Assert.Equal(0.8888277865983849, evaluation.Predictions[1], 12);
    }

    [Fact]
    public void Validation_candidate_worse_than_baseline_is_rejected()
    {
        var options = new FsrsOptimizerOptions
        {
            SearchRounds = 0,
            FixedCandidate = new FsrsParameters(new[]
            {
                100d, 100d, 100d, 100d, 10d, 4d, 4d, .75d, 4.5d, .8d, 3.5d,
                5d, .25d, .9d, 4d, 1d, 6d, 2d, 2d, .8d, .8d,
            }),
        };

        var result = new FsrsParameterOptimizer(options)
            .Optimize(SyntheticSamples(480), FsrsParameters.Default, CancellationToken.None);

        Assert.Equal(OptimizationStatus.BaselineRetained, result.Status);
        Assert.Equal(FsrsParameters.Default.Values, result.Parameters.Values);
        Assert.True(result.CandidateValidationLoss > result.BaselineValidationLoss + FsrsParameterOptimizer.ValidationTolerance);
    }

    [Fact]
    public void Cancellation_is_observed_and_progress_is_reported_without_UI_dependency()
    {
        var cancelled = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => new FsrsParameterOptimizer()
            .Optimize(SyntheticSamples(400), FsrsParameters.Default, cancelled));

        var updates = new List<OptimizationProgress>();
        var progress = new SynchronousProgress<OptimizationProgress>(updates.Add);
        _ = new FsrsParameterOptimizer(searchRounds: 1)
            .Optimize(SyntheticSamples(400), FsrsParameters.Default, CancellationToken.None, progress);

        Assert.NotEmpty(updates);
        Assert.Equal(1d, updates[^1].FractionComplete);
        Assert.All(updates, update => Assert.InRange(update.FractionComplete, 0d, 1d));
    }

    [Fact]
    public void Cancellation_is_observed_during_search()
    {
        using var source = new CancellationTokenSource();
        var progress = new SynchronousProgress<OptimizationProgress>(_ => source.Cancel());

        Assert.Throws<OperationCanceledException>(() => new FsrsParameterOptimizer(searchRounds: 4)
            .Optimize(SyntheticSamples(480), FsrsParameters.Default, source.Token, progress));
    }

    [Fact]
    public void Four_hundred_singleton_cards_return_not_eligible_with_zero_observations()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 400)
            .Select(i => new ReviewSample($"single-{i}", $"single-card-{i}", start.AddMinutes(i), 3))
            .ToArray();

        var result = new FsrsParameterOptimizer().Optimize(samples, FsrsParameters.Default, default);

        Assert.Equal(OptimizationStatus.NotEligible, result.Status);
        Assert.Equal(400, result.ValidEventCount);
        Assert.Equal(0, result.FilteredEventCount);
        Assert.Equal(0, result.TrainingObservationCount + result.ValidationObservationCount);
        Assert.Contains("observation", result.RejectionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boundary_spanning_cards_return_not_eligible_with_accurate_partition_counts()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 200)
            .SelectMany(i => new[]
            {
                new ReviewSample($"span-{i}-a", $"span-{i}", start.AddMinutes(i), 3),
                new ReviewSample($"span-{i}-b", $"span-{i}", start.AddYears(1).AddMinutes(i), 3),
            }).OrderBy(x => x.ReviewedAt).ToArray();

        var result = new FsrsParameterOptimizer().Optimize(samples, FsrsParameters.Default, default);

        Assert.Equal(OptimizationStatus.NotEligible, result.Status);
        Assert.Equal(400, result.ValidEventCount);
        Assert.Equal(240, result.TrainingEventCount);
        Assert.Equal(0, result.ValidationEventCount);
        Assert.Equal(120, result.TrainingObservationCount);
        Assert.Equal(0, result.ValidationObservationCount);
    }

    [Fact]
    public void Schedule_impossible_instant_is_filtered_without_aborting_run()
    {
        var samples = SyntheticSamples(400).Append(
            new ReviewSample("max-time", "corrupt-time", DateTimeOffset.MaxValue, 3)).ToArray();

        var result = new FsrsParameterOptimizer(searchRounds: 1)
            .Optimize(samples, FsrsParameters.Default, default);

        Assert.NotEqual(OptimizationStatus.InvalidData, result.Status);
        Assert.Equal(401, result.InputEventCount);
        Assert.Equal(400, result.ValidEventCount);
        Assert.Equal(1, result.FilteredEventCount);
    }

    [Fact]
    public void Unusable_candidate_is_rejected_and_baseline_retained_with_reason()
    {
        var samples = SyntheticSamples(480).Append(new ReviewSample(
            "near-limit", "near-limit-card", DateTimeOffset.MaxValue.AddDays(-2), 3)).ToArray();
        var values = new[]
        {
            100d, 100d, 100d, 100d, 10d, 4d, 4d, .75d, 4.5d, .8d, 3.5d,
            5d, .25d, .9d, 4d, 1d, 6d, 2d, 2d, .8d, .8d,
        };
        var result = new FsrsParameterOptimizer(new FsrsOptimizerOptions
        {
            SearchRounds = 0,
            FixedCandidate = new FsrsParameters(values),
        }).Optimize(samples, FsrsParameters.Default, default);

        Assert.Equal(OptimizationStatus.BaselineRetained, result.Status);
        Assert.Equal(FsrsParameters.Default.Values, result.Parameters.Values);
        Assert.Contains("rejected", result.RejectionReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ArgumentOutOfRange", result.RejectionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unusable_baseline_returns_invalid_data_instead_of_candidate_rejection()
    {
        var samples = SyntheticSamples(480).Append(new ReviewSample(
            "near-limit", "near-limit-card", DateTimeOffset.MaxValue.AddDays(-2), 3)).ToArray();
        var values = new[]
        {
            100d, 100d, 100d, 100d, 10d, 4d, 4d, .75d, 4.5d, .8d, 3.5d,
            5d, .25d, .9d, 4d, 1d, 6d, 2d, 2d, .8d, .8d,
        };
        var result = new FsrsParameterOptimizer().Optimize(samples, new FsrsParameters(values), default);

        Assert.Equal(OptimizationStatus.InvalidData, result.Status);
        Assert.Contains("baseline", result.RejectionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancellation_is_checked_in_duplicate_discovery_and_partitioning()
    {
        var samples = SyntheticSamples(480);
        using var first = new CancellationTokenSource();
        first.Cancel();
        Assert.Throws<OperationCanceledException>(() => FsrsParameterOptimizer.FilterValidSamples(samples, first.Token));

        using var second = new CancellationTokenSource();
        second.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            FsrsParameterOptimizer.CreateEvaluationPartition(samples, .2, second.Token));
    }

    [Fact]
    public void Zero_bound_coordinate_can_move_deterministically_within_official_bounds()
    {
        var values = FsrsParameters.Default.Values.ToArray();
        values[14] = 0;
        var source = new FsrsParameters(values);

        var first = FsrsParameterOptimizer.Perturb(source, 14, 1, 0);
        var second = FsrsParameterOptimizer.Perturb(source, 14, 1, 0);

        Assert.Equal(first.Values, second.Values);
        Assert.True(first.Values[14] > 0);
        _ = new FsrsParameters(first.Values);

        var optimized = new FsrsParameterOptimizer(seed: 1776, searchRounds: 2)
            .Optimize(SyntheticSamples(480), source, default);
        Assert.True(optimized.CandidateParameters.Values[14] > 0);
    }

    private static IReadOnlyList<ReviewSample> SyntheticSamples(int count)
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var samples = new List<ReviewSample>(count);
        for (var index = 0; index < count; index++)
        {
            var card = index / 8;
            var withinCard = index % 8;
            var reviewedAt = start.AddDays((card * 100) + (withinCard * (2 + (card % 5))));
            var rating = withinCard > 0 && (card + withinCard) % 5 == 0 ? 1 : 3;
            samples.Add(new($"command-{index}", $"card-{card}", reviewedAt, rating));
        }

        return samples.OrderBy(x => x.ReviewedAt).ToArray();
    }

    private static IReadOnlyList<ReviewSample> InterleavedBoundarySamples()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var samples = new List<ReviewSample>();
        for (var card = 0; card < 10; card++)
        {
            var baseDay = card < 8 ? card * 2 : 100 + (card * 2);
            samples.Add(new($"{card}-a", $"card-{card}", start.AddDays(baseDay), 3));
            samples.Add(new($"{card}-b", $"card-{card}", start.AddDays(baseDay + 1), 3));
        }

        samples.Add(new("cross-a", "crossing", start.AddDays(2), 3));
        samples.Add(new("cross-b", "crossing", start.AddDays(200), 3));
        return samples.OrderBy(x => x.ReviewedAt).ToArray();
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
