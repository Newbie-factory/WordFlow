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

    [Fact]
    public async Task Activated_snapshot_payload_and_status_are_immutable_and_restore_recovers_exact_bytes()
    {
        var factory = await FactoryAsync();
        var store = new SqliteFsrsParameterSnapshotStore(factory);
        var trusted = store.AppendSnapshot(OptimizationSnapshot.TrustedInitial(Id(20), FsrsParameters.Default, Now));
        store.AppendActivation(new FsrsParametersActivated(Id(21), trusted.Id, Now, ActivationReason.Initial, null));
        var optimized = store.AppendSnapshot(Snapshot(Id(22), OptimizationSnapshotStatus.ReadyForActivation));
        store.AppendActivation(new FsrsParametersActivated(Id(23), optimized.Id, Now.AddMinutes(1), ActivationReason.Optimized, null));
        var optimizedBytes = await SnapshotJsonAsync(factory, optimized.Id);
        var changedValues = optimized.CandidateParameters.Values.ToArray();
        changedValues[0] *= 1.01;

        Assert.Throws<InvalidOperationException>(() => store.UpdateSnapshot(
            optimized with { CandidateParameters = new FsrsParameters(changedValues) }));
        Assert.Throws<InvalidOperationException>(() => store.UpdateSnapshot(
            trusted with { Status = OptimizationSnapshotStatus.ReadyForActivation }));
        await using (var connection = await factory.OpenUserAsync(default))
        {
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(connection,
                $"UPDATE fsrs_parameter_snapshot SET snapshot_json='{{}}' WHERE snapshot_id='{optimized.Id:D}'"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(connection,
                $"UPDATE fsrs_parameter_snapshot SET status='ReadyForActivation' WHERE snapshot_id='{trusted.Id:D}'"));
        }
        Assert.Equal(optimizedBytes, await SnapshotJsonAsync(factory, optimized.Id));
        store.AppendActivation(new FsrsParametersActivated(Id(24), trusted.Id, Now.AddMinutes(2), ActivationReason.Restored, Id(21)));
        Assert.Equal(FsrsParameters.Default.Values, store.ActiveParameters.Values);
        Assert.Equal(await SnapshotJsonAsync(factory, trusted.Id), await ActiveSnapshotJsonAsync(factory));
    }

    [Fact]
    public async Task Only_pending_preview_status_can_transition_without_payload_mutation()
    {
        var factory = await FactoryAsync();
        var store = new SqliteFsrsParameterSnapshotStore(factory);
        var pending = store.AppendSnapshot(Snapshot(Id(30), OptimizationSnapshotStatus.PendingPreview));

        var ready = store.UpdateSnapshot(pending with { Status = OptimizationSnapshotStatus.ReadyForActivation });

        Assert.Equal(OptimizationSnapshotStatus.ReadyForActivation, ready.Status);
        Assert.Throws<InvalidOperationException>(() => store.UpdateSnapshot(ready with { Status = OptimizationSnapshotStatus.PreviewFailed }));
        Assert.Throws<InvalidOperationException>(() => store.UpdateSnapshot(ready with { TrainingLoss = ready.TrainingLoss + .1 }));
    }

    [Fact]
    public async Task Conflict_replace_cannot_rewrite_activation_or_snapshot_history()
    {
        var factory = await FactoryAsync();
        var store = new SqliteFsrsParameterSnapshotStore(factory);
        var snapshot = store.AppendSnapshot(Snapshot(Id(40), OptimizationSnapshotStatus.ReadyForActivation));
        store.AppendActivation(new FsrsParametersActivated(Id(41), snapshot.Id, Now, ActivationReason.Optimized, null));
        await using var connection = await factory.OpenUserAsync(default);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(connection,
            "REPLACE INTO fsrs_parameter_activation SELECT activation_id,snapshot_id,activated_at_utc,'Restored',source_activation_id FROM fsrs_parameter_activation"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(connection,
            "INSERT OR REPLACE INTO fsrs_parameter_snapshot SELECT snapshot_id,status,'{}',created_at_utc FROM fsrs_parameter_snapshot"));
        Assert.Equal(ActivationReason.Optimized, store.FindActivation(Id(41))!.Reason);
        Assert.Equal(snapshot.CandidateParameters.Values, store.FindSnapshot(Id(40))!.CandidateParameters.Values);
    }

    private static async Task ExecuteAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqliteConnectionFactory> FactoryAsync()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "user.db"));
        await new MigrationRunner(factory).MigrateAsync(default);
        return factory;
    }

    private static async Task<string> SnapshotJsonAsync(SqliteConnectionFactory factory, Guid id)
    {
        await using var connection = await factory.OpenUserAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM fsrs_parameter_snapshot WHERE snapshot_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ActiveSnapshotJsonAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenUserAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.snapshot_json FROM app_setting a JOIN fsrs_parameter_snapshot s ON s.snapshot_id=a.value WHERE a.key='fsrs.active_snapshot_id'";
        return (string)(await command.ExecuteScalarAsync())!;
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
