using WordFlow.Application.Ports;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Application.Scheduling;

public enum OptimizationSnapshotStatus
{
    PendingPreview,
    ReadyForActivation,
    PreviewFailed,
    Rejected,
    TrustedInitial,
}

public enum ActivationReason
{
    Initial,
    Optimized,
    Restored,
}

public sealed record DueDatePreview(string CardId, DateTimeOffset CurrentDueAt, DateTimeOffset CandidateDueAt);

public sealed record FsrsParametersActivated(
    Guid Id,
    Guid SnapshotId,
    DateTimeOffset ActivatedAt,
    ActivationReason Reason,
    Guid? SourceActivationId);

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
    OptimizationSnapshotStatus Status,
    OptimizationStatus OptimizationStatus,
    int InputEventCount,
    int ValidEventCount,
    int FilteredEventCount,
    int TrainingEventCount,
    int ValidationEventCount,
    int TrainingObservationCount,
    int ValidationObservationCount,
    string? RejectionReason)
{
    public static OptimizationSnapshot TrustedInitial(
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
            OptimizationSnapshotStatus.TrustedInitial,
            OptimizationStatus.Accepted,
            0, 0, 0, 0, 0, 0, 0,
            null);
}

public sealed record OptimizeFsrsParametersOutput(
    OptimizationSnapshot Snapshot,
    IReadOnlyList<DueDatePreview> DueDatePreview);

public interface IFsrsParameterSnapshotStore
{
    FsrsParameters ActiveParameters { get; }

    OptimizationSnapshot AppendSnapshot(OptimizationSnapshot snapshot);

    OptimizationSnapshot UpdateSnapshot(OptimizationSnapshot snapshot);

    OptimizationSnapshot? FindSnapshot(Guid id);

    FsrsParametersActivated? FindActivation(Guid id);

    void AppendActivation(FsrsParametersActivated activation);
}

public interface IFsrsDueDatePreviewer
{
    IReadOnlyList<DueDatePreview> Preview(
        FsrsParameters source,
        FsrsParameters candidate,
        CancellationToken cancellationToken);
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
        var accepted = result.Status == OptimizationStatus.Accepted;
        var snapshot = store.AppendSnapshot(ToSnapshot(
            result,
            source,
            accepted ? OptimizationSnapshotStatus.PendingPreview : OptimizationSnapshotStatus.Rejected));
        if (!accepted)
        {
            return new(snapshot, Array.Empty<DueDatePreview>());
        }

        IReadOnlyList<DueDatePreview> preview;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            preview = previewer.Preview(source, result.Parameters, cancellationToken);
        }
        catch
        {
            store.UpdateSnapshot(snapshot with { Status = OptimizationSnapshotStatus.PreviewFailed });
            throw;
        }

        snapshot = store.UpdateSnapshot(snapshot with { Status = OptimizationSnapshotStatus.ReadyForActivation });
        return new(snapshot, preview);
    }

    public void Activate(Guid snapshotId)
    {
        var snapshot = store.FindSnapshot(snapshotId)
            ?? throw new KeyNotFoundException($"FSRS parameter snapshot '{snapshotId}' was not found.");
        if (snapshot.Status != OptimizationSnapshotStatus.ReadyForActivation)
        {
            throw new InvalidOperationException("Only a successfully previewed optimization can be activated.");
        }

        store.AppendActivation(new FsrsParametersActivated(
            Guid.NewGuid(), snapshot.Id, clock.UtcNow.ToUniversalTime(), ActivationReason.Optimized, null));
    }

    public void Restore(Guid sourceActivationId)
    {
        var sourceActivation = store.FindActivation(sourceActivationId);
        if (sourceActivation is null)
        {
            if (store.FindSnapshot(sourceActivationId) is not null)
            {
                throw new InvalidOperationException("Restore requires a prior successful activation event, not a snapshot ID.");
            }

            throw new KeyNotFoundException($"FSRS activation '{sourceActivationId}' was not found.");
        }

        var snapshot = store.FindSnapshot(sourceActivation.SnapshotId)
            ?? throw new InvalidOperationException("The source activation's parameter snapshot is unavailable.");
        store.AppendActivation(new FsrsParametersActivated(
            Guid.NewGuid(),
            snapshot.Id,
            clock.UtcNow.ToUniversalTime(),
            ActivationReason.Restored,
            sourceActivation.Id));
    }

    private OptimizationSnapshot ToSnapshot(
        OptimizationResult result,
        FsrsParameters source,
        OptimizationSnapshotStatus status) => new(
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
            status,
            result.Status,
            result.InputEventCount,
            result.ValidEventCount,
            result.FilteredEventCount,
            result.TrainingEventCount,
            result.ValidationEventCount,
            result.TrainingObservationCount,
            result.ValidationObservationCount,
            result.RejectionReason);
}
