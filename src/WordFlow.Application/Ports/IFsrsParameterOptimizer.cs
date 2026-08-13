using WordFlow.Domain.Scheduling;

namespace WordFlow.Application.Ports;

public static class FsrsParameterOptimizerContract
{
    public const string AlgorithmVersion = "wordflow-fsrs6-local-v1";
}

public enum OptimizationStatus
{
    NotEligible,
    InvalidData,
    Accepted,
    BaselineRetained,
}

public sealed record OptimizationProgress(
    double FractionComplete,
    int CompletedEvaluations,
    int TotalEvaluations,
    double BestTrainingLoss);

public sealed record OptimizationResult(
    OptimizationStatus Status,
    FsrsParameters Parameters,
    int SampleCount,
    int TrainingSampleCount,
    int ValidationSampleCount,
    double TrainingLoss,
    double ValidationLoss,
    double BaselineTrainingLoss,
    double BaselineValidationLoss,
    FsrsParameters CandidateParameters,
    double CandidateTrainingLoss,
    double CandidateValidationLoss,
    int InputEventCount,
    int ValidEventCount,
    int FilteredEventCount,
    int TrainingEventCount,
    int ValidationEventCount,
    int TrainingObservationCount,
    int ValidationObservationCount,
    string? RejectionReason);

public interface IFsrsParameterOptimizer
{
    OptimizationResult Optimize(
        IReadOnlyList<ReviewSample> samples,
        FsrsParameters baseline,
        CancellationToken ct,
        IProgress<OptimizationProgress>? progress = null);
}
