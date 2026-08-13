using WordFlow.Application.Ports;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Infrastructure.Scheduling;

public sealed class FsrsOptimizerOptions
{
    public int Seed { get; init; } = 0x5F3759DF;

    public int SearchRounds { get; init; } = 4;

    public FsrsParameters? FixedCandidate { get; init; }
}

public sealed record EvaluationPartition(
    IReadOnlyList<ReviewSample> Training,
    IReadOnlyList<ReviewSample> Validation);

public sealed record LossEvaluation(
    double Loss,
    int ObservationCount,
    IReadOnlyList<double> Predictions);

public sealed class FsrsParameterOptimizer : IFsrsParameterOptimizer
{
    public const int MinimumEligibleSampleCount = 400;
    public const double ValidationTolerance = 1e-12;

    private const double ProbabilityEpsilon = 1e-15;
    private const double ValidationFraction = 0.20;

    private readonly FsrsOptimizerOptions options;

    public FsrsParameterOptimizer(int seed = 0x5F3759DF, int searchRounds = 4)
        : this(new FsrsOptimizerOptions { Seed = seed, SearchRounds = searchRounds })
    {
    }

    public FsrsParameterOptimizer(FsrsOptimizerOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.SearchRounds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Search rounds cannot be negative.");
        }
    }

    public OptimizationResult Optimize(
        IReadOnlyList<ReviewSample> samples,
        FsrsParameters baseline,
        CancellationToken ct,
        IProgress<OptimizationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(baseline);
        ct.ThrowIfCancellationRequested();

        var valid = FilterValidSamples(samples, ct);
        if (valid.Count < MinimumEligibleSampleCount)
        {
            return new(
                OptimizationStatus.NotEligible,
                baseline,
                valid.Count,
                0,
                0,
                double.NaN,
                double.NaN,
                double.NaN,
                double.NaN,
                baseline,
                double.NaN,
                double.NaN);
        }

        var partition = CreateEvaluationPartition(valid, ValidationFraction);
        if (partition.Training.Count == 0 || partition.Validation.Count == 0)
        {
            return new(
                OptimizationStatus.NotEligible,
                baseline,
                valid.Count,
                partition.Training.Count,
                partition.Validation.Count,
                double.NaN,
                double.NaN,
                double.NaN,
                double.NaN,
                baseline,
                double.NaN,
                double.NaN);
        }

        var baselineTraining = EvaluateLogLoss(partition.Training, baseline, ct);
        var baselineValidation = EvaluateLogLoss(partition.Validation, baseline, ct);
        EnsureUsableLoss(baselineTraining);
        EnsureUsableLoss(baselineValidation);

        var totalEvaluations = 1 + (options.FixedCandidate is null ? options.SearchRounds * 21 * 2 : 1);
        var completed = 0;
        var candidate = options.FixedCandidate ?? baseline;
        var candidateTraining = options.FixedCandidate is null
            ? baselineTraining
            : EvaluateLogLoss(partition.Training, candidate, ct);
        if (options.FixedCandidate is not null)
        {
            completed++;
            progress?.Report(new(
                (double)completed / totalEvaluations,
                completed,
                totalEvaluations,
                candidateTraining.Loss));
        }
        else
        {
            var random = new Random(options.Seed);
            var best = baseline;
            var bestLoss = baselineTraining.Loss;
            for (var round = 0; round < options.SearchRounds; round++)
            {
                for (var index = 0; index < 21; index++)
                {
                    var directionOrder = random.Next(2) == 0 ? new[] { -1, 1 } : new[] { 1, -1 };
                    foreach (var direction in directionOrder)
                    {
                        ct.ThrowIfCancellationRequested();
                        var trial = Perturb(best, index, direction, round);
                        var trialEvaluation = EvaluateLogLoss(partition.Training, trial, ct);
                        completed++;
                        if (double.IsFinite(trialEvaluation.Loss)
                            && trialEvaluation.ObservationCount > 0
                            && trialEvaluation.Loss < bestLoss)
                        {
                            best = trial;
                            bestLoss = trialEvaluation.Loss;
                        }

                        progress?.Report(new(
                            (double)completed / totalEvaluations,
                            completed,
                            totalEvaluations,
                            bestLoss));
                    }
                }
            }

            candidate = best;
            candidateTraining = EvaluateLogLoss(partition.Training, candidate, ct);
        }

        EnsureUsableLoss(candidateTraining);
        var candidateValidation = EvaluateLogLoss(partition.Validation, candidate, ct);
        EnsureUsableLoss(candidateValidation);
        completed = totalEvaluations;
        progress?.Report(new(1.0, completed, totalEvaluations, candidateTraining.Loss));

        var accepted = candidateValidation.Loss <= baselineValidation.Loss + ValidationTolerance;
        return new(
            accepted ? OptimizationStatus.Accepted : OptimizationStatus.BaselineRetained,
            accepted ? candidate : baseline,
            valid.Count,
            partition.Training.Count,
            partition.Validation.Count,
            accepted ? candidateTraining.Loss : baselineTraining.Loss,
            accepted ? candidateValidation.Loss : baselineValidation.Loss,
            baselineTraining.Loss,
            baselineValidation.Loss,
            candidate,
            candidateTraining.Loss,
            candidateValidation.Loss);
    }

    public static EvaluationPartition CreateEvaluationPartition(
        IReadOnlyList<ReviewSample> samples,
        double validationFraction)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!double.IsFinite(validationFraction) || validationFraction <= 0 || validationFraction >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(validationFraction));
        }

        var ordered = samples.OrderBy(x => x.ReviewedAt.ToUniversalTime()).ToArray();
        if (ordered.Length < 2)
        {
            return new(ordered, Array.Empty<ReviewSample>());
        }

        var boundaryIndex = Math.Clamp(
            (int)Math.Floor(ordered.Length * (1.0 - validationFraction)),
            1,
            ordered.Length - 1);
        var boundary = ordered[boundaryIndex].ReviewedAt.ToUniversalTime();
        var training = new List<ReviewSample>();
        var validation = new List<ReviewSample>();
        foreach (var history in ordered.GroupBy(x => x.CardId, StringComparer.Ordinal))
        {
            var cardSamples = history.OrderBy(x => x.ReviewedAt.ToUniversalTime()).ToArray();
            var first = cardSamples[0].ReviewedAt.ToUniversalTime();
            var last = cardSamples[^1].ReviewedAt.ToUniversalTime();
            if (last < boundary)
            {
                training.AddRange(cardSamples);
            }
            else if (first >= boundary)
            {
                validation.AddRange(cardSamples);
            }
        }

        return new(
            training.OrderBy(x => x.ReviewedAt.ToUniversalTime()).ToArray(),
            validation.OrderBy(x => x.ReviewedAt.ToUniversalTime()).ToArray());
    }

    public static LossEvaluation EvaluateLogLoss(
        IReadOnlyList<ReviewSample> samples,
        FsrsParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(parameters);
        var scheduler = new Fsrs6Scheduler(parameters);
        var states = new Dictionary<string, MemoryState>(StringComparer.Ordinal);
        var predictions = new List<double>();
        var sum = 0.0;
        var count = 0;

        foreach (var sample in samples.OrderBy(x => x.ReviewedAt.ToUniversalTime()))
        {
            ct.ThrowIfCancellationRequested();
            var rating = (Rating)sample.RatingValue!.Value;
            states.TryGetValue(sample.CardId, out var state);
            var result = scheduler.Review(state, rating, sample.ReviewedAt, desiredRetention: 0.9);
            if (state is not null)
            {
                var probability = Math.Clamp(
                    result.RetrievabilityBeforeReview,
                    ProbabilityEpsilon,
                    1.0 - ProbabilityEpsilon);
                var recalled = rating is Rating.Hard or Rating.Good;
                sum += recalled ? -Math.Log(probability) : -Math.Log(1.0 - probability);
                predictions.Add(result.RetrievabilityBeforeReview);
                count++;
            }

            states[sample.CardId] = result.State;
        }

        return new(count == 0 ? double.NaN : sum / count, count, predictions);
    }

    private static IReadOnlyList<ReviewSample> FilterValidSamples(
        IReadOnlyList<ReviewSample> samples,
        CancellationToken ct)
    {
        var duplicateCommands = samples
            .Where(sample => sample is not null && !string.IsNullOrWhiteSpace(sample.CommandId))
            .GroupBy(sample => sample.CommandId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var lastByCard = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var valid = new List<ReviewSample>();
        foreach (var sample in samples)
        {
            ct.ThrowIfCancellationRequested();
            if (sample is null
                || string.IsNullOrWhiteSpace(sample.CommandId)
                || string.IsNullOrWhiteSpace(sample.CardId)
                || sample.IsUndone
                || sample.IsInverse
                || sample.Kind != ReviewSampleKind.Rating
                || sample.RatingValue is not (1 or 2 or 3)
                || duplicateCommands.Contains(sample.CommandId))
            {
                continue;
            }

            var at = sample.ReviewedAt.ToUniversalTime();
            if (lastByCard.TryGetValue(sample.CardId, out var previous) && at <= previous)
            {
                continue;
            }

            lastByCard[sample.CardId] = at;
            valid.Add(sample with { ReviewedAt = at });
        }

        return valid.OrderBy(x => x.ReviewedAt).ToArray();
    }

    private static FsrsParameters Perturb(FsrsParameters baseline, int index, int direction, int round)
    {
        var values = baseline.Values.ToArray();
        var relativeStep = 0.08 / (round + 1.0);
        var proposed = values[index] * Math.Exp(direction * relativeStep);
        values[index] = proposed;
        try
        {
            return new(values);
        }
        catch (ArgumentOutOfRangeException)
        {
            return baseline;
        }
    }

    private static void EnsureUsableLoss(LossEvaluation evaluation)
    {
        if (evaluation.ObservationCount <= 0 || !double.IsFinite(evaluation.Loss))
        {
            throw new InvalidOperationException("FSRS optimization produced a non-finite or empty objective.");
        }
    }
}
