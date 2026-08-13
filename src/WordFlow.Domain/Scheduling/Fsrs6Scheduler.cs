namespace WordFlow.Domain.Scheduling;

public sealed class Fsrs6Scheduler : IFsrsScheduler
{
    private const double MinimumStabilityDays = 0.001;
    private const double MinimumDesiredRetention = 0.85;
    private const double MaximumDesiredRetention = 0.95;
    private const double StabilityRetention = 0.9;

    private readonly FsrsParameters parameters;
    private readonly double decay;
    private readonly double factor;

    public Fsrs6Scheduler()
        : this(FsrsParameters.Default)
    {
    }

    public Fsrs6Scheduler(FsrsParameters parameters)
    {
        this.parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        decay = parameters[20];
        factor = Math.Pow(StabilityRetention, -1.0 / decay) - 1.0;
        EnsureFinitePositive(factor, nameof(parameters));
    }

    public ScheduleResult Review(
        MemoryState? previous,
        Rating rating,
        DateTimeOffset reviewedAt,
        double desiredRetention)
    {
        ValidateRating(rating);
        ValidateDesiredRetention(desiredRetention);

        var reviewedAtUtc = reviewedAt.ToUniversalTime();
        double difficulty;
        double stability;
        double retrievabilityBeforeReview;

        if (previous is null)
        {
            difficulty = InitialDifficulty(rating);
            stability = InitialStability(rating);
            retrievabilityBeforeReview = 0.0;
        }
        else
        {
            ValidateState(previous);
            var lastReviewAtUtc = previous.LastReviewAt.ToUniversalTime();
            if (reviewedAtUtc < lastReviewAtUtc)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(reviewedAt),
                    "A review cannot precede the previous review instant.");
            }

            var elapsedDays = (reviewedAtUtc - lastReviewAtUtc).TotalDays;
            retrievabilityBeforeReview = ForgettingCurve(previous.StabilityDays, elapsedDays);
            difficulty = NextDifficulty(previous.Difficulty, rating);
            stability = elapsedDays < 1.0
                ? ShortTermStability(previous.StabilityDays, rating)
                : NextLongTermStability(
                    previous.Difficulty,
                    previous.StabilityDays,
                    retrievabilityBeforeReview,
                    rating);
        }

        difficulty = ClampDifficulty(difficulty);
        stability = ClampStability(stability);
        EnsureFinite(difficulty, nameof(difficulty));
        EnsureFinitePositive(stability, nameof(stability));
        EnsureProbability(retrievabilityBeforeReview, nameof(retrievabilityBeforeReview));

        var intervalDays = NextIntervalDays(stability, desiredRetention);
        var interval = TimeSpan.FromDays(intervalDays);
        DateTimeOffset dueAt;
        try
        {
            dueAt = reviewedAtUtc.Add(interval);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reviewedAt),
                "The computed due instant is outside DateTimeOffset's supported range.");
        }

        var state = new MemoryState(difficulty, stability, reviewedAtUtc);
        return new ScheduleResult(state, retrievabilityBeforeReview, interval, dueAt);
    }

    public double Retrievability(MemoryState state, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);

        var atUtc = at.ToUniversalTime();
        var lastReviewAtUtc = state.LastReviewAt.ToUniversalTime();
        if (atUtc < lastReviewAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(at), "Retrievability cannot be evaluated before the last review.");
        }

        return ForgettingCurve(state.StabilityDays, (atUtc - lastReviewAtUtc).TotalDays);
    }

    private double InitialStability(Rating rating) =>
        ClampStability(parameters[(int)rating - 1]);

    private double InitialDifficulty(Rating rating) =>
        ClampDifficulty(parameters[4] - Math.Exp(parameters[5] * ((int)rating - 1)) + 1.0);

    private double NextDifficulty(double difficulty, Rating rating)
    {
        var deltaDifficulty = -parameters[6] * ((int)rating - 3);
        var dampedDelta = (10.0 - difficulty) * deltaDifficulty / 9.0;
        var meanReversionTarget = parameters[4] - Math.Exp(parameters[5] * 3.0) + 1.0;

        return ClampDifficulty(
            (parameters[7] * meanReversionTarget)
            + ((1.0 - parameters[7]) * (difficulty + dampedDelta)));
    }

    private double ShortTermStability(double stability, Rating rating)
    {
        var increase = Math.Exp(parameters[17] * ((int)rating - 3 + parameters[18]))
            * Math.Pow(stability, -parameters[19]);

        if (rating == Rating.Good)
        {
            increase = Math.Max(increase, 1.0);
        }

        return ClampStability(stability * increase);
    }

    private double NextLongTermStability(
        double difficulty,
        double stability,
        double retrievability,
        Rating rating)
    {
        if (rating == Rating.Again)
        {
            var longTerm = parameters[11]
                * Math.Pow(difficulty, -parameters[12])
                * (Math.Pow(stability + 1.0, parameters[13]) - 1.0)
                * Math.Exp((1.0 - retrievability) * parameters[14]);
            var shortTermBound = stability / Math.Exp(parameters[17] * parameters[18]);
            return ClampStability(Math.Min(longTerm, shortTermBound));
        }

        var hardPenalty = rating == Rating.Hard ? parameters[15] : 1.0;
        return ClampStability(
            stability
            * (1.0
                + Math.Exp(parameters[8])
                * (11.0 - difficulty)
                * Math.Pow(stability, -parameters[9])
                * (Math.Exp((1.0 - retrievability) * parameters[10]) - 1.0)
                * hardPenalty));
    }

    private double ForgettingCurve(double stability, double elapsedDays)
    {
        var retrievability = Math.Pow(1.0 + (factor * elapsedDays / stability), -decay);
        EnsureProbability(retrievability, nameof(retrievability));
        return retrievability;
    }

    private int NextIntervalDays(double stability, double desiredRetention)
    {
        var unroundedDays = (stability / factor)
            * (Math.Pow(desiredRetention, -1.0 / decay) - 1.0);
        EnsureFinitePositive(unroundedDays, nameof(unroundedDays));

        var roundedDays = Math.Round(unroundedDays, MidpointRounding.ToEven);
        roundedDays = Math.Max(roundedDays, 1.0);
        if (roundedDays > int.MaxValue || roundedDays > TimeSpan.MaxValue.TotalDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stability),
                "The computed interval cannot be represented by TimeSpan.");
        }

        return (int)roundedDays;
    }

    private static double ClampDifficulty(double difficulty) => Math.Clamp(difficulty, 1.0, 10.0);

    private static double ClampStability(double stability) => Math.Max(stability, MinimumStabilityDays);

    private static void ValidateRating(Rating rating)
    {
        if (rating is not Rating.Again and not Rating.Hard and not Rating.Good)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), "Only Again, Hard, and Good are product ratings.");
        }
    }

    private static void ValidateDesiredRetention(double desiredRetention)
    {
        if (!double.IsFinite(desiredRetention)
            || desiredRetention < MinimumDesiredRetention
            || desiredRetention > MaximumDesiredRetention)
        {
            throw new ArgumentOutOfRangeException(
                nameof(desiredRetention),
                $"Desired retention must be finite and within [{MinimumDesiredRetention}, {MaximumDesiredRetention}].");
        }
    }

    private static void ValidateState(MemoryState state)
    {
        EnsureFinite(state.Difficulty, nameof(state.Difficulty));
        if (!double.IsFinite(state.StabilityDays) || state.StabilityDays < MinimumStabilityDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state.StabilityDays,
                $"Memory-state stability must be finite and at least {MinimumStabilityDays} day.");
        }

        if (state.Difficulty < 1.0 || state.Difficulty > 10.0)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Memory-state difficulty must be within [1, 10].");
        }
    }

    private static void EnsureFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite.");
        }
    }

    private static void EnsureFinitePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite and positive.");
        }
    }

    private static void EnsureProbability(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0.0 || value > 1.0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be a finite probability.");
        }
    }
}
