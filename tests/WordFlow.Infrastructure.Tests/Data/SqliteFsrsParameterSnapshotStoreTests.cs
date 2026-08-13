using WordFlow.Application.Scheduling;
using WordFlow.Application.Ports;
using WordFlow.Domain.Scheduling;
using WordFlow.Infrastructure.Data;

namespace WordFlow.Infrastructure.Tests.Data;

public sealed class SqliteFsrsParameterSnapshotStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wordflow-fsrs-store-{Guid.NewGuid():N}");

    public SqliteFsrsParameterSnapshotStoreTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Activation_persists_event_and_active_setting_atomically()
    {
        var database = Path.Combine(directory, "user.db");
        var factory = new SqliteConnectionFactory(database);
        await new MigrationRunner(factory).MigrateAsync(default);
        var store = new SqliteFsrsParameterSnapshotStore(factory);
        var snapshot = store.AppendSnapshot(Snapshot(Id(1), OptimizationSnapshotStatus.ReadyForActivation));

        store.AppendActivation(new FsrsParametersActivated(Id(2), snapshot.Id, Now, ActivationReason.Optimized, null));

        var reopened = new SqliteFsrsParameterSnapshotStore(factory);
        Assert.Equal(snapshot.CandidateParameters.Values, reopened.ActiveParameters.Values);
        Assert.Equal(snapshot.Id, reopened.FindActivation(Id(2))!.SnapshotId);
    }

    [Fact]
    public async Task Injected_failure_between_activation_event_and_active_setting_rolls_back_both()
    {
        var database = Path.Combine(directory, "user.db");
        var factory = new SqliteConnectionFactory(database);
        await new MigrationRunner(factory).MigrateAsync(default);
        var seed = new SqliteFsrsParameterSnapshotStore(factory);
        var snapshot = seed.AppendSnapshot(Snapshot(Id(3), OptimizationSnapshotStatus.ReadyForActivation));
        var failing = new SqliteFsrsParameterSnapshotStore(factory, stage =>
        {
            if (stage == FsrsCommitStage.ActivationWritten) throw new InjectedFailureException();
        });

        await Task.CompletedTask;
        Assert.Throws<InjectedFailureException>(() => failing.AppendActivation(
            new FsrsParametersActivated(Id(4), snapshot.Id, Now, ActivationReason.Optimized, null)));

        var reopened = new SqliteFsrsParameterSnapshotStore(factory);
        Assert.Null(reopened.FindActivation(Id(4)));
        Assert.Equal(FsrsParameters.Default.Values, reopened.ActiveParameters.Values);
    }

    [Fact]
    public async Task Durable_store_rejects_activation_without_valid_snapshot_state_and_provenance()
    {
        var database = Path.Combine(directory, "user.db");
        var factory = new SqliteConnectionFactory(database);
        await new MigrationRunner(factory).MigrateAsync(default);
        var store = new SqliteFsrsParameterSnapshotStore(factory);
        var rejected = store.AppendSnapshot(Snapshot(Id(5), OptimizationSnapshotStatus.Rejected));
        var ready = store.AppendSnapshot(Snapshot(Id(6), OptimizationSnapshotStatus.ReadyForActivation));
        var trusted = store.AppendSnapshot(OptimizationSnapshot.TrustedInitial(Id(9), FsrsParameters.Default, Now));

        Assert.Throws<InvalidOperationException>(() => store.AppendActivation(
            new FsrsParametersActivated(Id(7), rejected.Id, Now, ActivationReason.Optimized, null)));
        Assert.Throws<InvalidOperationException>(() => store.AppendActivation(
            new FsrsParametersActivated(Id(8), ready.Id, Now, ActivationReason.Restored, Id(999))));
        Assert.Throws<InvalidOperationException>(() => store.AppendActivation(
            new FsrsParametersActivated(Id(10), rejected.Id, Now, ActivationReason.Initial, null)));
        store.AppendActivation(new FsrsParametersActivated(Id(11), trusted.Id, Now, ActivationReason.Initial, null));
        Assert.Throws<InvalidOperationException>(() => store.AppendActivation(
            new FsrsParametersActivated(Id(12), ready.Id, Now, ActivationReason.Restored, Id(11))));
        Assert.Null(store.FindActivation(Id(7)));
        Assert.Null(store.FindActivation(Id(8)));
        Assert.Null(store.FindActivation(Id(10)));
        Assert.Null(store.FindActivation(Id(12)));
    }

    private static OptimizationSnapshot Snapshot(Guid id, OptimizationSnapshotStatus status)
    {
        var values = FsrsParameters.Default.Values.ToArray();
        values[0] *= 1.01;
        var candidate = new FsrsParameters(values);
        return new OptimizationSnapshot(
            id, "wordflow-fsrs6-local-v1", FsrsParameters.Default, candidate, 400,
            .4, .42, .5, .52, .4, .42, Now, status, OptimizationStatus.Accepted,
            400, 400, 0, 320, 80, 300, 75, null);
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
    private static Guid Id(int value) => new($"00000000-0000-0000-0000-{value:D12}");

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class InjectedFailureException : Exception;
}
