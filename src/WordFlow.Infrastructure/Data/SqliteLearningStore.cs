using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Infrastructure.Data;

public enum LearningCommitStage
{
    EventWritten,
}

public sealed class SqliteLearningStore : ILearningStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(), new CanonicalUtcConverter() },
    };

    private readonly SqliteConnectionFactory factory;
    private readonly Action<LearningCommitStage>? faultInjector;

    public SqliteLearningStore(SqliteConnectionFactory factory, Action<LearningCommitStage>? faultInjector = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.faultInjector = faultInjector;
    }

    public async Task<CommitResult> ApplyAsync(LearningCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEvent(command.Event);
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var existing = await FindCommandAsync(connection, transaction, command.CommandId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return existing;
            }

            var current = await ReadCardAsync(connection, transaction, command.Event.CardId, ct).ConfigureAwait(false);
            EnsureExpectedSnapshot(current, command.Event);

            await InsertEventAsync(connection, transaction, command, ct).ConfigureAwait(false);
            faultInjector?.Invoke(LearningCommitStage.EventWritten);
            await UpsertSnapshotAsync(connection, transaction, command.Event, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new CommitResult(true, command.Event.EventId, command.Event.After);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Preserve the originating failure; disposal will close the connection. */ }
            throw;
        }
    }

    internal async Task ApplyBatchAsync(IReadOnlyList<LearningCommand> commands, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0) return;
        foreach (var command in commands) ValidateEvent(command.Event);
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            foreach (var command in commands)
            {
                if (await FindCommandAsync(connection, transaction, command.CommandId, ct).ConfigureAwait(false) is not null) continue;
                var current = await ReadCardAsync(connection, transaction, command.Event.CardId, ct).ConfigureAwait(false);
                EnsureExpectedSnapshot(current, command.Event);
                await InsertEventAsync(connection, transaction, command, ct).ConfigureAwait(false);
                await UpsertSnapshotAsync(connection, transaction, command.Event, ct).ConfigureAwait(false);
            }
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async Task<CardState?> GetCardAsync(Guid cardId, CancellationToken ct)
    {
        if (cardId == Guid.Empty) throw new ArgumentException("A card ID cannot be empty.", nameof(cardId));
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        return await ReadCardAsync(connection, null, cardId, ct).ConfigureAwait(false);
    }

    private static void ValidateEvent(ReviewEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (@event.EventId == Guid.Empty || @event.CardId == Guid.Empty) throw new ArgumentException("Event and card IDs must be non-empty.", nameof(@event));
        if (@event.Before.Id != @event.CardId || @event.After.Id != @event.CardId) throw new ArgumentException("Event snapshots must belong to the event card.", nameof(@event));
        if (@event.Action == LearningAction.Undo && @event.CompensatesEventId is null) throw new ArgumentException("Undo requires a compensated event ID.", nameof(@event));
        if (@event.Action != LearningAction.Undo && @event.CompensatesEventId is not null) throw new ArgumentException("Only undo can compensate an event.", nameof(@event));
    }

    private static void EnsureExpectedSnapshot(CardState? current, ReviewEvent @event)
    {
        if (current is null && @event.Before.MemoryState is not null) throw new LearningConcurrencyException(@event.CardId);
        if (current is not null && current != @event.Before) throw new LearningConcurrencyException(@event.CardId);
    }

    private static async Task<CommitResult?> FindCommandAsync(SqliteConnection connection, SqliteTransaction transaction, Guid commandId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT event_id, after_json FROM review_event WHERE command_id=$commandId";
        command.Parameters.AddWithValue("$commandId", IdText(commandId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new CommitResult(false, Guid.Parse(reader.GetString(0)), DeserializeCard(reader.GetString(1)));
    }

    private static async Task InsertEventAsync(SqliteConnection connection, SqliteTransaction transaction, LearningCommand command, CancellationToken ct)
    {
        var @event = command.Event;
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO review_event(event_id, command_id, card_id, occurred_at_utc, action, compensates_event_id, before_json, after_json)
            VALUES ($eventId, $commandId, $cardId, $occurredAt, $action, $compensates, $before, $after)
            """;
        insert.Parameters.AddWithValue("$eventId", IdText(@event.EventId));
        insert.Parameters.AddWithValue("$commandId", IdText(command.CommandId));
        insert.Parameters.AddWithValue("$cardId", IdText(@event.CardId));
        insert.Parameters.AddWithValue("$occurredAt", MigrationRunner.UtcText(@event.OccurredAt));
        insert.Parameters.AddWithValue("$action", @event.Action.ToString());
        insert.Parameters.AddWithValue("$compensates", @event.CompensatesEventId is { } value ? IdText(value) : DBNull.Value);
        insert.Parameters.AddWithValue("$before", SerializeCard(@event.Before));
        insert.Parameters.AddWithValue("$after", SerializeCard(@event.After));
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if (ChangesSlashState(@event))
        {
            await using var slash = connection.CreateCommand();
            slash.Transaction = transaction;
            slash.CommandText = "INSERT INTO slash_event(event_id, card_id, action, occurred_at_utc) VALUES ($eventId, $cardId, $action, $occurredAt)";
            slash.Parameters.AddWithValue("$eventId", IdText(@event.EventId));
            slash.Parameters.AddWithValue("$cardId", IdText(@event.CardId));
            slash.Parameters.AddWithValue("$action", @event.Action.ToString());
            slash.Parameters.AddWithValue("$occurredAt", MigrationRunner.UtcText(@event.OccurredAt));
            await slash.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static bool ChangesSlashState(ReviewEvent @event) =>
        @event.Action is LearningAction.Slash or LearningAction.RestoreScheduled or LearningAction.RestoreImmediate
        || (@event.Action == LearningAction.Undo && @event.Before.Slash != @event.After.Slash);

    private static async Task UpsertSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction, ReviewEvent @event, CancellationToken ct)
    {
        var card = @event.After;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO card_state(card_id,difficulty,stability_days,last_review_at_utc,due_at_utc,is_slashed,slashed_at_utc,restored_at_utc,same_day_failure_count,failure_day_utc,hard_word_protected_until_utc,last_event_id)
            VALUES ($cardId,$difficulty,$stability,$lastReview,$dueAt,$isSlashed,$slashedAt,$restoredAt,$failureCount,$failureDay,$protectedUntil,$eventId)
            ON CONFLICT(card_id) DO UPDATE SET difficulty=excluded.difficulty,stability_days=excluded.stability_days,last_review_at_utc=excluded.last_review_at_utc,due_at_utc=excluded.due_at_utc,is_slashed=excluded.is_slashed,slashed_at_utc=excluded.slashed_at_utc,restored_at_utc=excluded.restored_at_utc,same_day_failure_count=excluded.same_day_failure_count,failure_day_utc=excluded.failure_day_utc,hard_word_protected_until_utc=excluded.hard_word_protected_until_utc,last_event_id=excluded.last_event_id
            """;
        BindCard(command, card);
        command.Parameters.AddWithValue("$eventId", IdText(@event.EventId));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void BindCard(SqliteCommand command, CardState card)
    {
        command.Parameters.AddWithValue("$cardId", IdText(card.Id));
        command.Parameters.AddWithValue("$difficulty", card.MemoryState?.Difficulty ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$stability", card.MemoryState?.StabilityDays ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$lastReview", card.MemoryState is null ? DBNull.Value : MigrationRunner.UtcText(card.MemoryState.LastReviewAt));
        command.Parameters.AddWithValue("$dueAt", MigrationRunner.UtcText(card.DueAt));
        command.Parameters.AddWithValue("$isSlashed", card.Slash.IsSlashed ? 1 : 0);
        command.Parameters.AddWithValue("$slashedAt", card.Slash.SlashedAt is { } slashed ? MigrationRunner.UtcText(slashed) : DBNull.Value);
        command.Parameters.AddWithValue("$restoredAt", card.Slash.RestoredAt is { } restored ? MigrationRunner.UtcText(restored) : DBNull.Value);
        command.Parameters.AddWithValue("$failureCount", card.SameDayFailureCount);
        command.Parameters.AddWithValue("$failureDay", card.FailureDayUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$protectedUntil", card.HardWordProtectedUntil is { } until ? MigrationRunner.UtcText(until) : DBNull.Value);
    }

    private static async Task<CardState?> ReadCardAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid cardId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT card_id,difficulty,stability_days,last_review_at_utc,due_at_utc,is_slashed,slashed_at_utc,restored_at_utc,same_day_failure_count,failure_day_utc,hard_word_protected_until_utc FROM card_state WHERE card_id=$cardId";
        command.Parameters.AddWithValue("$cardId", IdText(cardId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        try
        {
            MemoryState? memory = reader.IsDBNull(1) ? null : new MemoryState(reader.GetDouble(1), reader.GetDouble(2), ParseUtc(reader.GetString(3)));
            return new CardState(Guid.Parse(reader.GetString(0)), memory, ParseUtc(reader.GetString(4)))
            {
                Slash = new SlashState(reader.GetInt64(5) != 0, NullableUtc(reader, 6), NullableUtc(reader, 7)),
                SameDayFailureCount = reader.GetInt32(8),
                FailureDayUtc = reader.IsDBNull(9) ? null : DateOnly.ParseExact(reader.GetString(9), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                HardWordProtectedUntil = NullableUtc(reader, 10),
            };
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"Card {cardId:D} contains corrupt persisted data.", exception);
        }
    }

    private static DateTimeOffset? NullableUtc(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ParseUtc(reader.GetString(ordinal));

    private static DateTimeOffset ParseUtc(string value)
    {
        var parsed = DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        if (!value.EndsWith('Z')) throw new FormatException("Persisted timestamps must be canonical UTC.");
        return parsed;
    }

    private static string SerializeCard(CardState card) => JsonSerializer.Serialize(card, JsonOptions);

    private static CardState DeserializeCard(string json) => JsonSerializer.Deserialize<CardState>(json, JsonOptions) ?? throw new InvalidDataException("Persisted card JSON is null.");

    private static string IdText(Guid id) => id.ToString("D");

    private sealed class CanonicalUtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ParseUtc(reader.GetString() ?? throw new JsonException("A timestamp is required."));

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(MigrationRunner.UtcText(value));
    }
}
