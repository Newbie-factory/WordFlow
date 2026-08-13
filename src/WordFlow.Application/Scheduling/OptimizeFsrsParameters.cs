using WordFlow.Application.Ports;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Application.Scheduling;

public enum OptimizationSnapshotStatus
{
    ReadyForActivation,
    Rejected,
    RestoredSource,
}

public enum ActivationReason
{
    Optimized,
    Restored,
}

public sealed record DueDatePreview(string CardId, DateTimeOffset CurrentDueAt, DateTimeOffset CandidateDueAt);

public sealed record FsrsParametersActivated(
    Guid SnapshotId,
    DateTimeOffset ActivatedAt,
    ActivationReason Reason);

public sealed record OptimizationSnapshot(
    Guid Id,
    string AlgorithmVersion,
    FsrsParameters SourceParameters,
    FsrsParameters CandidateParameters,
    int SampleCount,
    double TrainingLoss,
    double ValidationLoss,
    double BaselineTrainingLoss,
    double BaselineValidationLoss,
    double CandidateTrainingLoss,
    double CandidateValidationLoss,
    DateTimeOffset CreatedAt,
    OptimizationSnapshotStatus Status)
{
    public static OptimizationSnapshot RestoredSource(
        Guid id,
        FsrsParameters parameters,
        DateTimeOffset createdAt) => new(
            id,
            FsrsParameterOptimizerContract.AlgorithmVersion,
            parameters,
            parameters,
            0,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            createdAt.ToUniversalTime(),
            OptimizationSnapshotStatus.RestoredSource);
}

public sealed record OptimizeFsrsParametersOutput(
    OptimizationSnapshot Snapshot,
    IReadOnlyList<DueDatePreview> DueDatePreview);

public interface IFsrsParameterSnapshotStore
{
    FsrsParameters ActiveParameters { get; }

    OptimizationSnapshot AppendSnapshot(OptimizationSnapshot snapshot);

    OptimizationSnapshot? FindSnapshot(Guid id);

    void AppendActivation(FsrsParametersActivated activation);
}

public interface IFsrsDueDatePreviewer
{
    IReadOnlyList<DueDatePreview> Preview(FsrsParameters source, FsrsParameters candidate);
}

public interface IOptimizationClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class OptimizeFsrsParameters
{
    private readonly IFsrsParameterOptimizer optimizer;
    private readonly IFsrsParameterSnapshotStore store;
    private readonly IFsrsDueDatePreviewer previewer;
    private readonly IOptimizationClock clock;

    public OptimizeFsrsParameters(
        IFsrsParameterOptimizer optimizer,
        IFsrsParameterSnapshotStore store,
        IFsrsDueDatePreviewer previewer,
        IOptimizationClock clock)
    {
        this.optimizer = optimizer ?? throw new ArgumentNullException(nameof(optimizer));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.previewer = previewer ?? throw new ArgumentNullException(nameof(previewer));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public OptimizeFsrsParametersOutput Execute(
        IReadOnlyList<ReviewSample> samples,
        CancellationToken cancellationToken,
        IProgress<OptimizationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var source = store.ActiveParameters;
        var result = optimizer.Optimize(samples, source, cancellationToken, progress);
        var ready = result.Status == OptimizationStatus.Accepted;
        var snapshot = store.AppendSnapshot(new OptimizationSnapshot(
            Guid.NewGuid(),
            FsrsParameterOptimizerContract.AlgorithmVersion,
            source,
            result.CandidateParameters,
            result.SampleCount,
            result.TrainingLoss,
            result.ValidationLoss,
            result.BaselineTrainingLoss,
            result.BaselineValidationLoss,
            result.CandidateTrainingLoss,
            result.CandidateValidationLoss,
            clock.UtcNow.ToUniversalTime(),
            ready ? OptimizationSnapshotStatus.ReadyForActivation : OptimizationSnapshotStatus.Rejected));
        var preview = ready
            ? previewer.Preview(source, result.Parameters)
            : Array.Empty<DueDatePreview>();
        return new(snapshot, preview);
    }

    public void Activate(Guid snapshotId) => AppendActivation(snapshotId, ActivationReason.Optimized);

    public void Restore(Guid snapshotId) => AppendActivation(snapshotId, ActivationReason.Restored);

    private void AppendActivation(Guid snapshotId, ActivationReason reason)
    {
        var snapshot = store.FindSnapshot(snapshotId)
            ?? throw new KeyNotFoundException($"FSRS parameter snapshot '{snapshotId}' was not found.");
        var canActivate = reason switch
        {
            ActivationReason.Optimized => snapshot.Status == OptimizationSnapshotStatus.ReadyForActivation,
            ActivationReason.Restored => snapshot.Status is OptimizationSnapshotStatus.ReadyForActivation
                or OptimizationSnapshotStatus.RestoredSource,
            _ => false,
        };
        if (!canActivate)
        {
            throw new InvalidOperationException("A rejected optimization snapshot cannot be activated or restored.");
        }

        store.AppendActivation(new FsrsParametersActivated(
            snapshot.Id,
            clock.UtcNow.ToUniversalTime(),
            reason));
    }
}
