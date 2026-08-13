using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using WordFlow.Application.Scheduling;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Infrastructure.Data;

public enum FsrsCommitStage
{
    ActivationWritten,
}

public sealed class SqliteFsrsParameterSnapshotStore : IFsrsParameterSnapshotStore
{
    private const string ActiveSnapshotKey = "fsrs.active_snapshot_id";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly SqliteConnectionFactory factory;
    private readonly Action<FsrsCommitStage>? faultInjector;

    public SqliteFsrsParameterSnapshotStore(SqliteConnectionFactory factory, Action<FsrsCommitStage>? faultInjector = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.faultInjector = faultInjector;
    }

    public FsrsParameters ActiveParameters
    {
        get
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.snapshot_json
                FROM app_setting a JOIN fsrs_parameter_snapshot s ON s.snapshot_id=a.value
                WHERE a.key=$key
                """;
            command.Parameters.AddWithValue("$key", ActiveSnapshotKey);
            var value = command.ExecuteScalar();
            return value is string json ? DeserializeSnapshot(json).CandidateParameters : FsrsParameters.Default;
        }
    }

    public OptimizationSnapshot AppendSnapshot(OptimizationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Id == Guid.Empty) throw new ArgumentException("A snapshot ID cannot be empty.", nameof(snapshot));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO fsrs_parameter_snapshot(snapshot_id,status,snapshot_json,created_at_utc) VALUES ($id,$status,$json,$createdAt)";
        BindSnapshot(command, snapshot);
        command.ExecuteNonQuery();
        return snapshot;
    }

    public OptimizationSnapshot UpdateSnapshot(OptimizationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = FindSnapshotWithinTransaction(connection, transaction, snapshot.Id)
            ?? throw new KeyNotFoundException($"FSRS parameter snapshot '{snapshot.Id:D}' was not found.");
        if (existing.Status != OptimizationSnapshotStatus.PendingPreview
            || snapshot.Status is not (OptimizationSnapshotStatus.ReadyForActivation or OptimizationSnapshotStatus.PreviewFailed))
            throw new InvalidOperationException("Only a pending preview can transition to ready or preview-failed.");
        if (JsonSerializer.Serialize(existing with { Status = snapshot.Status }, JsonOptions)
            != JsonSerializer.Serialize(snapshot, JsonOptions))
            throw new InvalidOperationException("FSRS snapshot parameters, audit, and provenance are immutable.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE fsrs_parameter_snapshot SET status=$status,snapshot_json=$json WHERE snapshot_id=$id AND status='PendingPreview'";
        command.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
        command.Parameters.AddWithValue("$status", snapshot.Status.ToString());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, JsonOptions));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("FSRS snapshot status changed concurrently or is activated.");
        transaction.Commit();
        return snapshot;
    }

    public OptimizationSnapshot? FindSnapshot(Guid id)
    {
        if (id == Guid.Empty) return null;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM fsrs_parameter_snapshot WHERE snapshot_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return command.ExecuteScalar() is string json ? DeserializeSnapshot(json) : null;
    }

    public FsrsParametersActivated? FindActivation(Guid id)
    {
        if (id == Guid.Empty) return null;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT activation_id,snapshot_id,activated_at_utc,reason,source_activation_id FROM fsrs_parameter_activation WHERE activation_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        try
        {
            return new FsrsParametersActivated(
                Guid.ParseExact(reader.GetString(0), "D"),
                Guid.ParseExact(reader.GetString(1), "D"),
                ParseUtc(reader.GetString(2)),
                Enum.Parse<ActivationReason>(reader.GetString(3), ignoreCase: false),
                reader.IsDBNull(4) ? null : Guid.ParseExact(reader.GetString(4), "D"));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new InvalidDataException($"FSRS activation {id:D} contains corrupt persisted data.", exception);
        }
    }

    public void AppendActivation(FsrsParametersActivated activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (activation.Id == Guid.Empty || activation.SnapshotId == Guid.Empty) throw new ArgumentException("Activation and snapshot IDs must be non-empty.", nameof(activation));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var snapshot = FindSnapshotWithinTransaction(connection, transaction, activation.SnapshotId)
                ?? throw new KeyNotFoundException($"FSRS parameter snapshot '{activation.SnapshotId:D}' was not found.");
            if (activation.Reason == ActivationReason.Optimized
                && snapshot.Status != OptimizationSnapshotStatus.ReadyForActivation)
            {
                throw new InvalidOperationException("Only a successfully previewed optimization can be activated.");
            }
            if (activation.Reason == ActivationReason.Restored)
            {
                if (activation.SourceActivationId is not { } sourceId)
                {
                    throw new InvalidOperationException("Restore requires a prior durable activation event.");
                }
                var sourceActivation = FindActivationWithinTransaction(connection, transaction, sourceId);
                if (sourceActivation is null || sourceActivation.SnapshotId != activation.SnapshotId)
                {
                    throw new InvalidOperationException("Restore must reactivate the source activation's snapshot.");
                }
            }
            else if (activation.Reason == ActivationReason.Initial
                     && snapshot.Status != OptimizationSnapshotStatus.TrustedInitial)
            {
                throw new InvalidOperationException("Initial activation requires a trusted initial snapshot.");
            }
            else if (activation.SourceActivationId is not null)
            {
                throw new InvalidOperationException("Only restore activations can reference a source activation.");
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO fsrs_parameter_activation(activation_id,snapshot_id,activated_at_utc,reason,source_activation_id) VALUES ($id,$snapshot,$at,$reason,$source)";
                insert.Parameters.AddWithValue("$id", activation.Id.ToString("D"));
                insert.Parameters.AddWithValue("$snapshot", activation.SnapshotId.ToString("D"));
                insert.Parameters.AddWithValue("$at", MigrationRunner.UtcText(activation.ActivatedAt));
                insert.Parameters.AddWithValue("$reason", activation.Reason.ToString());
                insert.Parameters.AddWithValue("$source", activation.SourceActivationId is { } source ? source.ToString("D") : DBNull.Value);
                insert.ExecuteNonQuery();
            }
            faultInjector?.Invoke(FsrsCommitStage.ActivationWritten);
            using (var setting = connection.CreateCommand())
            {
                setting.Transaction = transaction;
                setting.CommandText = "INSERT INTO app_setting(key,value) VALUES ($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                setting.Parameters.AddWithValue("$key", ActiveSnapshotKey);
                setting.Parameters.AddWithValue("$value", activation.SnapshotId.ToString("D"));
                setting.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = factory.UserDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA recursive_triggers=ON; PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static OptimizationSnapshot? FindSnapshotWithinTransaction(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT snapshot_json FROM fsrs_parameter_snapshot WHERE snapshot_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return command.ExecuteScalar() is string json ? DeserializeSnapshot(json) : null;
    }

    private static FsrsParametersActivated? FindActivationWithinTransaction(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT activation_id,snapshot_id,activated_at_utc,reason,source_activation_id FROM fsrs_parameter_activation WHERE activation_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new FsrsParametersActivated(
            Guid.ParseExact(reader.GetString(0), "D"),
            Guid.ParseExact(reader.GetString(1), "D"),
            ParseUtc(reader.GetString(2)),
            Enum.Parse<ActivationReason>(reader.GetString(3), ignoreCase: false),
            reader.IsDBNull(4) ? null : Guid.ParseExact(reader.GetString(4), "D"));
    }

    private static void BindSnapshot(SqliteCommand command, OptimizationSnapshot snapshot)
    {
        command.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
        command.Parameters.AddWithValue("$status", snapshot.Status.ToString());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, JsonOptions));
        command.Parameters.AddWithValue("$createdAt", MigrationRunner.UtcText(snapshot.CreatedAt));
    }

    private static OptimizationSnapshot DeserializeSnapshot(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OptimizationSnapshot>(json, JsonOptions)
                ?? throw new InvalidDataException("Persisted FSRS snapshot is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Persisted FSRS snapshot is corrupt.", exception);
        }
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        var result = DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        if (!value.EndsWith('Z')) throw new FormatException("Persisted timestamps must be canonical UTC.");
        return result;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new CanonicalUtcConverter());
        options.Converters.Add(new FsrsParametersConverter());
        return options;
    }

    private sealed class CanonicalUtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ParseUtc(reader.GetString() ?? throw new JsonException("A timestamp is required."));

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(MigrationRunner.UtcText(value));
    }

    private sealed class FsrsParametersConverter : JsonConverter<FsrsParameters>
    {
        public override FsrsParameters Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var values = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement
                : document.RootElement.GetProperty("Values");
            return new FsrsParameters(values.EnumerateArray().Select(value => value.GetDouble()));
        }

        public override void Write(Utf8JsonWriter writer, FsrsParameters value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Values, options);
    }
}
