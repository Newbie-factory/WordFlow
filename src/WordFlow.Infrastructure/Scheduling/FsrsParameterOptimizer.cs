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
            return EmptyResult(
                OptimizationStatus.NotEligible,
                baseline,
                samples.Count,
                valid.Count,
                "At least 400 valid review events are required.");
        }

        var partition = CreateEvaluationPartition(valid, ValidationFraction, ct);
        var baselineEvaluator = (Func<IReadOnlyList<ReviewSample>, FsrsParameters, CancellationToken, LossEvaluation>)EvaluateLogLoss;
        var trainingOk = TryEvaluate(
            partition.Training, baseline, baselineEvaluator, ct, out var baselineTraining, out var baselineError);
        var validationOk = TryEvaluate(
            partition.Validation, baseline, baselineEvaluator, ct, out var baselineValidation, out var validationError);
        if (!trainingOk || !validationOk)
        {
            var reason = baselineError ?? validationError ?? "Baseline objective is unusable.";
            return Result(
                OptimizationStatus.InvalidData,
                baseline,
                baseline,
                samples.Count,
                valid.Count,
                partition,
                baselineTraining,
                baselineValidation,
                baselineTraining,
                baselineValidation,
                $"Baseline evaluation failed: {reason}");
        }

        if (!IsUsable(baselineTraining) || !IsUsable(baselineValidation))
        {
            return Result(
                OptimizationStatus.NotEligible,
                baseline,
                baseline,
                samples.Count,
                valid.Count,
                partition,
                baselineTraining,
                baselineValidation,
                baselineTraining,
                baselineValidation,
                "Training and validation partitions must both contain replayable supervised observations.");
        }

        var candidateEvaluator = (Func<IReadOnlyList<ReviewSample>, FsrsParameters, CancellationToken, LossEvaluation>)EvaluateLogLoss;
        var totalEvaluations = 1 + (options.FixedCandidate is null ? options.SearchRounds * 21 * 2 : 1);
        var completed = 0;
        var candidate = options.FixedCandidate ?? baseline;
        var candidateTraining = baselineTraining;
        string? candidateFailure = null;

        if (options.FixedCandidate is not null)
        {
            if (!TryEvaluate(partition.Training, candidate, candidateEvaluator, ct, out candidateTraining, out candidateFailure)
                || !IsUsable(candidateTraining))
            {
                candidateFailure ??= "Candidate training objective is non-finite or empty.";
            }
            completed++;
            progress?.Report(new(
                (double)completed / totalEvaluations,
                completed,
                totalEvaluations,
                baselineTraining.Loss));
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
                        if (TryEvaluate(partition.Training, trial, candidateEvaluator, ct, out var trialEvaluation, out _)
                            && IsUsable(trialEvaluation)
                            && trialEvaluation.Loss < bestLoss)
                        {
                            best = trial;
                            bestLoss = trialEvaluation.Loss;
                        }

                        completed++;
                        progress?.Report(new(
                            (double)completed / totalEvaluations,
                            completed,
                            totalEvaluations,
                            bestLoss));
                    }
                }
            }

            candidate = best;
            if (!TryEvaluate(partition.Training, candidate, candidateEvaluator, ct, out candidateTraining, out candidateFailure)
                || !IsUsable(candidateTraining))
            {
                candidateFailure ??= "Final candidate training objective is non-finite or empty.";
            }
        }

        if (candidateFailure is not null)
        {
            return Result(
                OptimizationStatus.BaselineRetained,
                baseline,
                candidate,
                samples.Count,
                valid.Count,
                partition,
                baselineTraining,
                baselineValidation,
                candidateTraining,
                new(double.NaN, 0, Array.Empty<double>()),
                $"Candidate rejected: {candidateFailure}");
        }

        if (!TryEvaluate(partition.Validation, candidate, candidateEvaluator, ct, out var candidateValidation, out candidateFailure)
            || !IsUsable(candidateValidation))
        {
            return Result(
                OptimizationStatus.BaselineRetained,
                baseline,
                candidate,
                samples.Count,
                valid.Count,
                partition,
                baselineTraining,
                baselineValidation,
                candidateTraining,
                candidateValidation,
                $"Candidate rejected: {candidateFailure ?? "validation objective is non-finite or empty"}.");
        }

        completed = totalEvaluations;
        progress?.Report(new(1.0, completed, totalEvaluations, candidateTraining.Loss));
        var accepted = candidateValidation.Loss <= baselineValidation.Loss + ValidationTolerance;
        return Result(
            accepted ? OptimizationStatus.Accepted : OptimizationStatus.BaselineRetained,
            accepted ? candidate : baseline,
            candidate,
            samples.Count,
            valid.Count,
            partition,
            baselineTraining,
            baselineValidation,
            candidateTraining,
            candidateValidation,
            accepted ? null : "Candidate validation loss exceeds the baseline tolerance.");
    }

    public static EvaluationPartition CreateEvaluationPartition(
        IReadOnlyList<ReviewSample> samples,
        double validationFraction,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!double.IsFinite(validationFraction) || validationFraction <= 0 || validationFraction >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(validationFraction));
        }

        ct.ThrowIfCancellationRequested();
        var ordered = samples.ToArray();
        ct.ThrowIfCancellationRequested();
        Array.Sort(ordered, CompareSamples);
        ct.ThrowIfCancellationRequested();
        if (ordered.Length < 2)
        {
            return new(ordered, Array.Empty<ReviewSample>());
        }

        var boundaryIndex = Math.Clamp(
            (int)Math.Floor(ordered.Length * (1.0 - validationFraction)),
            1,
            ordered.Length - 1);
        var boundary = ordered[boundaryIndex].ReviewedAt.ToUniversalTime();
        var histories = new Dictionary<string, List<ReviewSample>>(StringComparer.Ordinal);
        foreach (var sample in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (!histories.TryGetValue(sample.CardId, out var history))
            {
                history = [];
                histories.Add(sample.CardId, history);
            }
            history.Add(sample);
        }

        var training = new List<ReviewSample>();
        var validation = new List<ReviewSample>();
        foreach (var history in histories.Values)
        {
            ct.ThrowIfCancellationRequested();
            var first = history[0].ReviewedAt.ToUniversalTime();
            var last = history[^1].ReviewedAt.ToUniversalTime();
            if (last < boundary)
            {
                training.AddRange(history);
            }
            else if (first >= boundary)
            {
                validation.AddRange(history);
            }
        }

        ct.ThrowIfCancellationRequested();
        training.Sort(CompareSamples);
        validation.Sort(CompareSamples);
        ct.ThrowIfCancellationRequested();
        return new(training, validation);
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
        var ordered = samples.ToArray();
        ct.ThrowIfCancellationRequested();
        Array.Sort(ordered, CompareSamples);
        ct.ThrowIfCancellationRequested();

        foreach (var sample in ordered)
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
            }

            states[sample.CardId] = result.State;
        }

        return new(
            predictions.Count == 0 ? double.NaN : sum / predictions.Count,
            predictions.Count,
            predictions);
    }

    public static IReadOnlyList<ReviewSample> FilterValidSamples(
        IReadOnlyList<ReviewSample> samples,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var commandCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            ct.ThrowIfCancellationRequested();
            if (sample is not null && !string.IsNullOrWhiteSpace(sample.CommandId))
            {
                commandCounts.TryGetValue(sample.CommandId, out var count);
                commandCounts[sample.CommandId] = count + 1;
            }
        }

        ct.ThrowIfCancellationRequested();
        var lastByCard = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var valid = new List<ReviewSample>();
        var latestSchedulableInstant = DateTimeOffset.MaxValue.AddDays(-1);
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
                || commandCounts[sample.CommandId] > 1)
            {
                continue;
            }

            var at = sample.ReviewedAt.ToUniversalTime();
            if (at > latestSchedulableInstant
                || (lastByCard.TryGetValue(sample.CardId, out var previous) && at <= previous))
            {
                continue;
            }

            lastByCard[sample.CardId] = at;
            valid.Add(sample with { ReviewedAt = at });
        }

        ct.ThrowIfCancellationRequested();
        valid.Sort(CompareSamples);
        ct.ThrowIfCancellationRequested();
        return valid;
    }

    public static FsrsParameters Perturb(FsrsParameters baseline, int index, int direction, int round)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (index < 0 || index >= baseline.Values.Count || direction is not (-1 or 1) || round < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var values = baseline.Values.ToArray();
        var relativeStep = 0.08 / (round + 1.0);
        values[index] = values[index] == 0.0
            ? direction * (0.36 / (round + 1.0))
            : values[index] * Math.Exp(direction * relativeStep);
        try
        {
            return new(values);
        }
        catch (ArgumentOutOfRangeException)
        {
            return baseline;
        }
    }

    private static bool TryEvaluate(
        IReadOnlyList<ReviewSample> samples,
        FsrsParameters parameters,
        Func<IReadOnlyList<ReviewSample>, FsrsParameters, CancellationToken, LossEvaluation> evaluator,
        CancellationToken ct,
        out LossEvaluation evaluation,
        out string? error)
    {
        try
        {
            evaluation = evaluator(samples, parameters, ct);
            error = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArithmeticException
            or ArgumentException
            or InvalidOperationException)
        {
            evaluation = new(double.NaN, 0, Array.Empty<double>());
            error = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static bool IsUsable(LossEvaluation evaluation) =>
        evaluation.ObservationCount > 0 && double.IsFinite(evaluation.Loss);

    private static OptimizationResult EmptyResult(
        OptimizationStatus status,
        FsrsParameters baseline,
        int inputCount,
        int validCount,
        string reason) => new(
            status,
            baseline,
            validCount,
            0,
            0,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            baseline,
            double.NaN,
            double.NaN,
            inputCount,
            validCount,
            inputCount - validCount,
            0,
            0,
            0,
            0,
            reason);

    private static OptimizationResult Result(
        OptimizationStatus status,
        FsrsParameters applied,
        FsrsParameters candidate,
        int inputCount,
        int validCount,
        EvaluationPartition partition,
        LossEvaluation baselineTraining,
        LossEvaluation baselineValidation,
        LossEvaluation candidateTraining,
        LossEvaluation candidateValidation,
        string? rejectionReason) => new(
            status,
            applied,
            validCount,
            partition.Training.Count,
            partition.Validation.Count,
            status == OptimizationStatus.Accepted ? candidateTraining.Loss : baselineTraining.Loss,
            status == OptimizationStatus.Accepted ? candidateValidation.Loss : baselineValidation.Loss,
            baselineTraining.Loss,
            baselineValidation.Loss,
            candidate,
            candidateTraining.Loss,
            candidateValidation.Loss,
            inputCount,
            validCount,
            inputCount - validCount,
            partition.Training.Count,
            partition.Validation.Count,
            baselineTraining.ObservationCount,
            baselineValidation.ObservationCount,
            rejectionReason);

    private static int CompareSamples(ReviewSample? left, ReviewSample? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        var byTime = left.ReviewedAt.ToUniversalTime().CompareTo(right.ReviewedAt.ToUniversalTime());
        if (byTime != 0) return byTime;
        var byCommand = string.CompareOrdinal(left.CommandId, right.CommandId);
        return byCommand != 0 ? byCommand : string.CompareOrdinal(left.CardId, right.CardId);
    }
}
