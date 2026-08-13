using WordFlow.Application.Ports;
using WordFlow.Application.Scheduling;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Application.Tests.Scheduling;

public sealed class OptimizeFsrsParametersTests
{
    [Fact]
    public void Optimization_appends_audit_snapshot_and_returns_preview_without_activation()
    {
        var candidate = PerturbedDefaults();
        var optimizer = new FakeOptimizer(Accepted(candidate));
        var store = new FakeStore(FsrsParameters.Default);
        var previewer = new FakePreviewer([new("card-1", Day(10), Day(12))]);
        var clock = new FakeClock(Day(5));
        var useCase = new OptimizeFsrsParameters(optimizer, store, previewer, clock);

        var output = useCase.Execute([], CancellationToken.None);

        var snapshot = Assert.Single(store.Snapshots);
        Assert.Equal(FsrsParameterOptimizerContract.AlgorithmVersion, snapshot.AlgorithmVersion);
        Assert.Equal(FsrsParameters.Default.Values, snapshot.SourceParameters.Values);
        Assert.Equal(candidate.Values, snapshot.CandidateParameters.Values);
        Assert.Equal(400, snapshot.SampleCount);
        Assert.Equal(OptimizationStatus.Accepted, snapshot.OptimizationStatus);
        Assert.Equal(400, snapshot.ValidEventCount);
        Assert.Equal(320, snapshot.TrainingEventCount);
        Assert.Equal(300, snapshot.TrainingObservationCount);
        Assert.Equal(Day(5), snapshot.CreatedAt);
        Assert.Equal(OptimizationSnapshotStatus.ReadyForActivation, snapshot.Status);
        Assert.Single(output.DueDatePreview);
        Assert.Empty(store.ActivationEvents);
        Assert.Equal(FsrsParameters.Default.Values, store.ActiveParameters.Values);
    }

    [Fact]
    public void Activation_is_future_only_and_does_not_rewrite_history_or_mass_reschedule()
    {
        var store = new FakeStore(FsrsParameters.Default);
        var useCase = new OptimizeFsrsParameters(
            new FakeOptimizer(Accepted(PerturbedDefaults())),
            store,
            new FakePreviewer([]),
            new FakeClock(Day(5)));
        var snapshot = useCase.Execute([], CancellationToken.None).Snapshot;

        useCase.Activate(snapshot.Id);

        var activation = Assert.Single(store.ActivationEvents);
        Assert.Equal(snapshot.Id, activation.SnapshotId);
        Assert.Equal(ActivationReason.Optimized, activation.Reason);
        Assert.Equal(snapshot.CandidateParameters.Values, store.ActiveParameters.Values);
        Assert.Equal(0, store.RewriteHistoryCalls);
        Assert.Equal(0, store.MassRescheduleCalls);
    }

    [Fact]
    public void Restore_reactivates_previous_snapshot_by_appending_an_event()
    {
        var store = new FakeStore(FsrsParameters.Default);
        var useCase = new OptimizeFsrsParameters(
            new FakeOptimizer(Accepted(PerturbedDefaults())),
            store,
            new FakePreviewer([]),
            new FakeClock(Day(5)));
        var optimized = useCase.Execute([], CancellationToken.None).Snapshot;
        useCase.Activate(optimized.Id);
        var originalActivation = store.SeedTrustedInitial(FsrsParameters.Default, Day(1));

        useCase.Restore(originalActivation.Id);

        Assert.Equal(3, store.ActivationEvents.Count);
        Assert.Equal(ActivationReason.Restored, store.ActivationEvents[^1].Reason);
        Assert.Equal(originalActivation.Id, store.ActivationEvents[^1].SourceActivationId);
        Assert.Equal(FsrsParameters.Default.Values, store.ActiveParameters.Values);
        Assert.Equal(0, store.RewriteHistoryCalls);
        Assert.Equal(0, store.MassRescheduleCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preview_failure_or_cancellation_leaves_snapshot_nonactivatable(bool cancelled)
    {
        var store = new FakeStore(FsrsParameters.Default);
        Exception exception = cancelled
            ? new OperationCanceledException("preview cancelled")
            : new InvalidOperationException("preview failed");
        var previewer = new ThrowingPreviewer(exception);
        var useCase = new OptimizeFsrsParameters(
            new FakeOptimizer(Accepted(PerturbedDefaults())), store, previewer, new FakeClock(Day(5)));

        var thrown = Assert.ThrowsAny<Exception>(() => useCase.Execute([], default));

        Assert.IsType(exception.GetType(), thrown);
        var snapshot = Assert.Single(store.Snapshots);
        Assert.Equal(OptimizationSnapshotStatus.PreviewFailed, snapshot.Status);
        Assert.Throws<InvalidOperationException>(() => useCase.Activate(snapshot.Id));
    }

    [Fact]
    public void Store_failure_during_ready_transition_never_leaves_activatable_snapshot()
    {
        var store = new FakeStore(FsrsParameters.Default) { ThrowOnReadyTransition = true };
        var useCase = new OptimizeFsrsParameters(
            new FakeOptimizer(Accepted(PerturbedDefaults())), store, new FakePreviewer([]), new FakeClock(Day(5)));

        Assert.Throws<InvalidOperationException>(() => useCase.Execute([], default));
        Assert.Equal(OptimizationSnapshotStatus.PendingPreview, Assert.Single(store.Snapshots).Status);
        Assert.Empty(store.ActivationEvents);
    }

    [Fact]
    public void Restore_rejects_forged_or_never_activated_snapshot()
    {
        var store = new FakeStore(FsrsParameters.Default);
        var useCase = new OptimizeFsrsParameters(
            new FakeOptimizer(Accepted(PerturbedDefaults())), store, new FakePreviewer([]), new FakeClock(Day(5)));
        var ready = useCase.Execute([], default).Snapshot;

        Assert.Throws<KeyNotFoundException>(() => useCase.Restore(Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => useCase.Restore(ready.Id));
    }

    [Fact]
    public void Audit_preserves_exact_optimizer_status_counts_and_rejection_reason()
    {
        var result = Accepted(PerturbedDefaults()) with
        {
            Status = OptimizationStatus.NotEligible,
            InputEventCount = 450,
            ValidEventCount = 400,
            FilteredEventCount = 50,
            TrainingEventCount = 300,
            ValidationEventCount = 100,
            TrainingObservationCount = 0,
            ValidationObservationCount = 0,
            RejectionReason = "No replayable observations",
        };
        var store = new FakeStore(FsrsParameters.Default);
        var output = new OptimizeFsrsParameters(
            new FakeOptimizer(result), store, new FakePreviewer([]), new FakeClock(Day(5))).Execute([], default);

        Assert.Equal(OptimizationStatus.NotEligible, output.Snapshot.OptimizationStatus);
        Assert.Equal(450, output.Snapshot.InputEventCount);
        Assert.Equal(400, output.Snapshot.ValidEventCount);
        Assert.Equal(50, output.Snapshot.FilteredEventCount);
        Assert.Equal(300, output.Snapshot.TrainingEventCount);
        Assert.Equal(100, output.Snapshot.ValidationEventCount);
        Assert.Equal(0, output.Snapshot.TrainingObservationCount);
        Assert.Equal("No replayable observations", output.Snapshot.RejectionReason);
    }

    [Fact]
    public void Rejected_result_is_audited_but_cannot_be_activated()
    {
        var candidate = PerturbedDefaults();
        var rejected = Accepted(candidate) with
        {
            Status = OptimizationStatus.BaselineRetained,
            Parameters = FsrsParameters.Default,
            CandidateParameters = candidate,
            CandidateValidationLoss = .62,
        };
        var store = new FakeStore(FsrsParameters.Default);
        var useCase = new OptimizeFsrsParameters(
            new FakeOptimizer(rejected), store, new FakePreviewer([]), new FakeClock(Day(5)));

        var output = useCase.Execute([], CancellationToken.None);

        Assert.Equal(OptimizationSnapshotStatus.Rejected, output.Snapshot.Status);
        Assert.Equal(candidate.Values, output.Snapshot.CandidateParameters.Values);
        Assert.Equal(.4, output.Snapshot.CandidateTrainingLoss);
        Assert.Equal(.62, output.Snapshot.CandidateValidationLoss);
        Assert.Empty(output.DueDatePreview);
        Assert.Throws<InvalidOperationException>(() => useCase.Activate(output.Snapshot.Id));
        Assert.Throws<InvalidOperationException>(() => useCase.Restore(output.Snapshot.Id));
    }

    private static OptimizationResult Accepted(FsrsParameters candidate) => new(
        OptimizationStatus.Accepted,
        candidate,
        SampleCount: 400,
        TrainingSampleCount: 320,
        ValidationSampleCount: 80,
        TrainingLoss: .4,
        ValidationLoss: .42,
        BaselineTrainingLoss: .5,
        BaselineValidationLoss: .52,
        CandidateParameters: candidate,
        CandidateTrainingLoss: .4,
        CandidateValidationLoss: .42,
        InputEventCount: 400,
        ValidEventCount: 400,
        FilteredEventCount: 0,
        TrainingEventCount: 320,
        ValidationEventCount: 80,
        TrainingObservationCount: 300,
        ValidationObservationCount: 75,
        RejectionReason: null);

    private static FsrsParameters PerturbedDefaults()
    {
        var values = FsrsParameters.Default.Values.ToArray();
        values[0] *= 1.01;
        return new(values);
    }

    private static DateTimeOffset Day(int day) =>
        new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day);

    private sealed class FakeOptimizer(OptimizationResult result) : IFsrsParameterOptimizer
    {
        public OptimizationResult Optimize(
            IReadOnlyList<ReviewSample> samples,
            FsrsParameters baseline,
            CancellationToken ct,
            IProgress<OptimizationProgress>? progress = null) => result;
    }

    private sealed class FakePreviewer(IReadOnlyList<DueDatePreview> preview) : IFsrsDueDatePreviewer
    {
        public IReadOnlyList<DueDatePreview> Preview(FsrsParameters source, FsrsParameters candidate, CancellationToken ct) => preview;
    }

    private sealed class ThrowingPreviewer(Exception exception) : IFsrsDueDatePreviewer
    {
        public IReadOnlyList<DueDatePreview> Preview(FsrsParameters source, FsrsParameters candidate, CancellationToken ct) => throw exception;
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IOptimizationClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class FakeStore(FsrsParameters initial) : IFsrsParameterSnapshotStore
    {
        public FsrsParameters ActiveParameters { get; private set; } = initial;
        public List<OptimizationSnapshot> Snapshots { get; } = [];
        public List<FsrsParametersActivated> ActivationEvents { get; } = [];
        public int RewriteHistoryCalls { get; private set; }
        public int MassRescheduleCalls { get; private set; }
        public bool ThrowOnReadyTransition { get; init; }

        public OptimizationSnapshot AppendSnapshot(OptimizationSnapshot snapshot)
        {
            Snapshots.Add(snapshot);
            return snapshot;
        }

        public OptimizationSnapshot? FindSnapshot(Guid id) => Snapshots.SingleOrDefault(x => x.Id == id);

        public OptimizationSnapshot UpdateSnapshot(OptimizationSnapshot snapshot)
        {
            if (ThrowOnReadyTransition && snapshot.Status == OptimizationSnapshotStatus.ReadyForActivation)
            {
                throw new InvalidOperationException("store transition failed");
            }
            var index = Snapshots.FindIndex(x => x.Id == snapshot.Id);
            Snapshots[index] = snapshot;
            return snapshot;
        }

        public FsrsParametersActivated? FindActivation(Guid id) => ActivationEvents.SingleOrDefault(x => x.Id == id);

        public FsrsParametersActivated SeedTrustedInitial(FsrsParameters parameters, DateTimeOffset at)
        {
            var snapshot = OptimizationSnapshot.TrustedInitial(Guid.NewGuid(), parameters, at);
            AppendSnapshot(snapshot);
            var activation = new FsrsParametersActivated(Guid.NewGuid(), snapshot.Id, at, ActivationReason.Initial, null);
            AppendActivation(activation);
            return activation;
        }

        public void AppendActivation(FsrsParametersActivated activation)
        {
            ActivationEvents.Add(activation);
            ActiveParameters = FindSnapshot(activation.SnapshotId)!.CandidateParameters;
        }
    }
}
