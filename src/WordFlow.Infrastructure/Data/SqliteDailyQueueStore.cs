using System.Globalization;
using Microsoft.Data.Sqlite;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;

namespace WordFlow.Infrastructure.Data;

public sealed class SqliteDailyQueueStore : IDailyQueueStore
{
    private readonly SqliteConnectionFactory factory;

    public SqliteDailyQueueStore(SqliteConnectionFactory factory) =>
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public Task<DailySessionSnapshot?> GetAsync(DateOnly localDay, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(async () =>
        {
            await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: true);
            var snapshot = await ReadSnapshotAsync(connection, transaction, localDay, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return snapshot;
        });

    public Task<DailySessionSnapshot> GetOrCreateAsync(DailyQueueSeed seed, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => GetOrCreateCoreAsync(seed, ct));

    private async Task<DailySessionSnapshot> GetOrCreateCoreAsync(DailyQueueSeed seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(seed.ConfiguredPlan);
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var existing = await ReadSnapshotAsync(connection, transaction, seed.LocalDay, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return existing;
            }

            await InsertSessionAsync(connection, transaction, seed, ct).ConfigureAwait(false);
            var includedCards = new HashSet<Guid>();
            var newCount = 0;
            var reviewCount = 0;
            var ordinal = 0;
            var priorPending = await ReadPriorPendingAsync(connection, transaction, seed.LocalDay, ct).ConfigureAwait(false);
            foreach (var source in priorPending)
            {
                var withinTarget = source.Kind switch
                {
                    DailyQueueItemKind.New => newCount < seed.ConfiguredPlan.NewLimit,
                    DailyQueueItemKind.Review => reviewCount < seed.ConfiguredPlan.SoftReviewLimit,
                    _ => false,
                };
                if (!withinTarget || !includedCards.Add(source.CardId)) continue;

                await MarkCarriedForwardAsync(connection, transaction, source.ItemId, seed.CreatedAt, ct).ConfigureAwait(false);
                await InsertItemAsync(connection, transaction, Guid.NewGuid(), seed.LocalDay, ordinal++, source.CardId,
                    source.Kind, source.OriginDay, source.ItemId, seed.CreatedAt, ct).ConfigureAwait(false);
                if (source.Kind == DailyQueueItemKind.New) newCount++;
                else reviewCount++;
            }

            foreach (var cardId in ValidDistinct(seed.OrderedReviewCandidates))
            {
                if (reviewCount >= seed.ConfiguredPlan.SoftReviewLimit) break;
                if (!includedCards.Add(cardId)) continue;
                await InsertItemAsync(connection, transaction, Guid.NewGuid(), seed.LocalDay, ordinal++, cardId,
                    DailyQueueItemKind.Review, seed.LocalDay, null, seed.CreatedAt, ct).ConfigureAwait(false);
                reviewCount++;
            }
            foreach (var cardId in ValidDistinct(seed.OrderedNewCandidates))
            {
                if (newCount >= seed.ConfiguredPlan.NewLimit) break;
                if (!includedCards.Add(cardId)) continue;
                await InsertItemAsync(connection, transaction, Guid.NewGuid(), seed.LocalDay, ordinal++, cardId,
                    DailyQueueItemKind.New, seed.LocalDay, null, seed.CreatedAt, ct).ConfigureAwait(false);
                newCount++;
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE daily_sessions
                    SET effective_new_target=$newTarget,
                        effective_review_target=$reviewTarget,
                        completed_at_utc=CASE WHEN $itemCount=0 THEN $updatedAt ELSE NULL END,
                        updated_at_utc=$updatedAt
                    WHERE local_day=$day
                    """;
                update.Parameters.AddWithValue("$newTarget", newCount);
                update.Parameters.AddWithValue("$reviewTarget", reviewCount);
                update.Parameters.AddWithValue("$itemCount", newCount + reviewCount);
                update.Parameters.AddWithValue("$updatedAt", UtcText(seed.CreatedAt));
                update.Parameters.AddWithValue("$day", DayText(seed.LocalDay));
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var created = await ReadSnapshotAsync(connection, transaction, seed.LocalDay, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The daily session was not visible inside its creation transaction.");
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return created;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public Task<DailyQueueItem?> GetNextPendingAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(async () =>
        {
            _ = now;
            await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT item_id,local_day,ordinal,card_id,kind,origin_day,source_item_id,status,completed_event_id,completed_at_utc
                FROM daily_queue_items
                WHERE local_day=$day AND status='Pending'
                ORDER BY CASE kind WHEN 'Relearning' THEN 0 WHEN 'Review' THEN 1 ELSE 2 END, ordinal
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$day", DayText(localDay));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadItem(reader) : null;
        });

    public Task EnsureDueRelearningAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => EnsureDueRelearningCoreAsync(localDay, now, ct));

    public Task<DateTimeOffset?> GetNextRelearningDueAsync(
        DateOnly localDay, DateTimeOffset now, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync<DateTimeOffset?>(async () =>
        {
            await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT MIN(c.due_at_utc)
                FROM review_event r
                JOIN daily_queue_items completed
                  ON completed.completed_event_id=r.event_id AND completed.local_day=$day
                JOIN card_state c ON c.card_id=r.card_id
                WHERE r.action='Again'
                  AND c.is_slashed=0
                  AND c.due_at_utc > $now
                  AND NOT EXISTS (SELECT 1 FROM review_event undo WHERE undo.compensates_event_id=r.event_id)
                  AND NOT EXISTS (
                      SELECT 1 FROM daily_queue_items pending
                      WHERE pending.local_day=$day AND pending.card_id=r.card_id
                        AND pending.kind='Relearning' AND pending.status='Pending')
                """;
            command.Parameters.AddWithValue("$day", DayText(localDay));
            command.Parameters.AddWithValue("$now", UtcText(now));
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is null or DBNull ? null : ParseUtc(Convert.ToString(value, CultureInfo.InvariantCulture)!);
        });

    private async Task EnsureDueRelearningCoreAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var cards = new List<Guid>();
            await using (var due = connection.CreateCommand())
            {
                due.Transaction = transaction;
                due.CommandText = """
                    SELECT DISTINCT r.card_id
                    FROM review_event r
                    JOIN daily_queue_items completed
                      ON completed.completed_event_id=r.event_id AND completed.local_day=$day
                    JOIN card_state c ON c.card_id=r.card_id
                    WHERE r.action='Again'
                      AND c.is_slashed=0
                      AND c.due_at_utc <= $now
                      AND NOT EXISTS (SELECT 1 FROM review_event undo WHERE undo.compensates_event_id=r.event_id)
                      AND NOT EXISTS (
                          SELECT 1 FROM daily_queue_items pending
                          WHERE pending.local_day=$day AND pending.card_id=r.card_id
                            AND pending.kind='Relearning' AND pending.status='Pending')
                    ORDER BY r.card_id
                    """;
                due.Parameters.AddWithValue("$day", DayText(localDay));
                due.Parameters.AddWithValue("$now", UtcText(now));
                await using var reader = await due.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) cards.Add(ParseId(reader.GetString(0)));
            }

            if (cards.Count > 0)
            {
                var ordinal = await ReadNextOrdinalAsync(connection, transaction, localDay, ct).ConfigureAwait(false);
                foreach (var cardId in cards)
                {
                    await InsertItemAsync(connection, transaction, Guid.NewGuid(), localDay, ordinal++, cardId,
                        DailyQueueItemKind.Relearning, localDay, null, now, ct).ConfigureAwait(false);
                }
                await using var reopen = connection.CreateCommand();
                reopen.Transaction = transaction;
                reopen.CommandText = "UPDATE daily_sessions SET completed_at_utc=NULL,updated_at_utc=$now WHERE local_day=$day";
                reopen.Parameters.AddWithValue("$now", UtcText(now));
                reopen.Parameters.AddWithValue("$day", DayText(localDay));
                await reopen.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public Task<IReadOnlyList<DailyHistoryEntry>> GetHistoryAsync(DateOnly throughDay, int dayCount, CancellationToken ct)
    {
        if (dayCount < 0) throw new ArgumentOutOfRangeException(nameof(dayCount));
        return SqliteStorageBoundary.TranslateAsync<IReadOnlyList<DailyHistoryEntry>>(
            () => GetHistoryCoreAsync(throughDay, dayCount, ct));
    }

    private async Task<IReadOnlyList<DailyHistoryEntry>> GetHistoryCoreAsync(
        DateOnly throughDay, int dayCount, CancellationToken ct)
    {
        if (dayCount == 0) return [];
        var firstDay = throughDay.AddDays(1 - dayCount);
        var byDay = new Dictionary<DateOnly, DailyHistoryEntry>();
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.local_day,s.effective_new_target,s.effective_review_target,s.completed_at_utc,
                   COALESCE(SUM(CASE WHEN q.kind='New' AND q.status IN ('Completed','Slashed') THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN q.kind='Review' AND q.status IN ('Completed','Slashed') THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN q.kind='Relearning' AND q.status='Pending' THEN 1 ELSE 0 END),0)
            FROM daily_sessions s
            LEFT JOIN daily_queue_items q ON q.local_day=s.local_day
            WHERE s.local_day BETWEEN $firstDay AND $throughDay
            GROUP BY s.local_day
            ORDER BY s.local_day
            """;
        command.Parameters.AddWithValue("$firstDay", DayText(firstDay));
        command.Parameters.AddWithValue("$throughDay", DayText(throughDay));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var day = ParseDay(reader.GetString(0));
            byDay.Add(day, new DailyHistoryEntry(day, reader.GetInt32(4), reader.GetInt32(1),
                reader.GetInt32(5), reader.GetInt32(2), reader.GetInt32(6), true, !reader.IsDBNull(3)));
        }

        var result = new List<DailyHistoryEntry>(dayCount);
        for (var offset = 0; offset < dayCount; offset++)
        {
            var day = firstDay.AddDays(offset);
            result.Add(byDay.TryGetValue(day, out var entry)
                ? entry
                : new DailyHistoryEntry(day, 0, 0, 0, 0, 0, false, false));
        }
        return result;
    }

    public Task<GoalProgress> GetGoalProgressAsync(CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(async () =>
        {
            await using var user = await factory.OpenUserAsync(ct).ConfigureAwait(false);
            await using var slashed = user.CreateCommand();
            slashed.CommandText = "SELECT COUNT(DISTINCT card_id) FROM card_state WHERE is_slashed=1";
            var slashedWords = Convert.ToInt32(await slashed.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);

            await using var vocabulary = await factory.OpenVocabularyAsync(ct).ConfigureAwait(false);
            await using var total = vocabulary.CreateCommand();
            total.CommandText = "SELECT COUNT(*) FROM vocabulary";
            var totalWords = Convert.ToInt32(await total.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            return new GoalProgress(slashedWords, totalWords);
        });

    private static async Task InsertSessionAsync(
        SqliteConnection connection, SqliteTransaction transaction, DailyQueueSeed seed, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO daily_sessions(local_day,configured_new_target,configured_review_target,effective_new_target,effective_review_target,completed_at_utc,created_at_utc,updated_at_utc)
            VALUES ($day,$newTarget,$reviewTarget,0,0,NULL,$createdAt,$createdAt)
            """;
        command.Parameters.AddWithValue("$day", DayText(seed.LocalDay));
        command.Parameters.AddWithValue("$newTarget", seed.ConfiguredPlan.NewLimit);
        command.Parameters.AddWithValue("$reviewTarget", seed.ConfiguredPlan.SoftReviewLimit);
        command.Parameters.AddWithValue("$createdAt", UtcText(seed.CreatedAt));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<PersistedQueueItem>> ReadPriorPendingAsync(
        SqliteConnection connection, SqliteTransaction transaction, DateOnly beforeDay, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT item_id,card_id,kind,origin_day
            FROM daily_queue_items
            WHERE local_day < $day AND status='Pending'
            ORDER BY local_day,ordinal
            """;
        command.Parameters.AddWithValue("$day", DayText(beforeDay));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<PersistedQueueItem>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new PersistedQueueItem(ParseId(reader.GetString(0)), ParseId(reader.GetString(1)),
                ParseKind(reader.GetString(2)), ParseDay(reader.GetString(3))));
        }
        return result;
    }

    private static async Task MarkCarriedForwardAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid itemId, DateTimeOffset completedAt, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE daily_queue_items SET status='CarriedForward',completed_at_utc=$completedAt WHERE item_id=$itemId AND status='Pending'";
        command.Parameters.AddWithValue("$completedAt", UtcText(completedAt));
        command.Parameters.AddWithValue("$itemId", IdText(itemId));
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException($"Carryover source {itemId:D} was no longer pending.");
    }

    private static async Task InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId,
        DateOnly localDay,
        int ordinal,
        Guid cardId,
        DailyQueueItemKind kind,
        DateOnly originDay,
        Guid? sourceItemId,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO daily_queue_items(item_id,local_day,ordinal,card_id,kind,origin_day,source_item_id,status,completed_event_id,created_at_utc,completed_at_utc)
            VALUES ($itemId,$day,$ordinal,$cardId,$kind,$originDay,$sourceItemId,'Pending',NULL,$createdAt,NULL)
            """;
        command.Parameters.AddWithValue("$itemId", IdText(itemId));
        command.Parameters.AddWithValue("$day", DayText(localDay));
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$cardId", IdText(cardId));
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$originDay", DayText(originDay));
        command.Parameters.AddWithValue("$sourceItemId", sourceItemId is { } source ? IdText(source) : DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", UtcText(createdAt));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadNextOrdinalAsync(
        SqliteConnection connection, SqliteTransaction transaction, DateOnly localDay, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(ordinal),-1)+1 FROM daily_queue_items WHERE local_day=$day";
        command.Parameters.AddWithValue("$day", DayText(localDay));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<DailySessionSnapshot?> ReadSnapshotAsync(
        SqliteConnection connection, SqliteTransaction transaction, DateOnly localDay, CancellationToken ct)
    {
        DailyPlan configured;
        DailyPlan effective;
        DateTimeOffset? completedAt;
        await using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = "SELECT configured_new_target,configured_review_target,effective_new_target,effective_review_target,completed_at_utc FROM daily_sessions WHERE local_day=$day";
            session.Parameters.AddWithValue("$day", DayText(localDay));
            await using var reader = await session.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            configured = new DailyPlan(reader.GetInt32(0), reader.GetInt32(1));
            effective = new DailyPlan(reader.GetInt32(2), reader.GetInt32(3));
            completedAt = reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4));
        }

        var items = new List<DailyQueueItem>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT item_id,local_day,ordinal,card_id,kind,origin_day,source_item_id,status,completed_event_id,completed_at_utc
                FROM daily_queue_items WHERE local_day=$day ORDER BY ordinal
                """;
            command.Parameters.AddWithValue("$day", DayText(localDay));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(ReadItem(reader));
        }
        return new DailySessionSnapshot(localDay, configured, effective, completedAt, items);
    }

    private static DailyQueueItem ReadItem(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        ParseDay(reader.GetString(1)),
        reader.GetInt32(2),
        ParseId(reader.GetString(3)),
        ParseKind(reader.GetString(4)),
        ParseDay(reader.GetString(5)),
        reader.IsDBNull(6) ? null : ParseId(reader.GetString(6)),
        ParseStatus(reader.GetString(7)),
        reader.IsDBNull(8) ? null : ParseId(reader.GetString(8)),
        reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9)));

    private static IEnumerable<Guid> ValidDistinct(IEnumerable<Guid> candidates) =>
        (candidates ?? throw new ArgumentNullException(nameof(candidates))).Where(id => id != Guid.Empty).Distinct();

    private static DailyQueueItemKind ParseKind(string value) =>
        Enum.TryParse<DailyQueueItemKind>(value, ignoreCase: false, out var kind)
            ? kind : throw new InvalidDataException($"Unknown daily queue kind '{value}'.");

    private static DailyQueueItemStatus ParseStatus(string value) =>
        Enum.TryParse<DailyQueueItemStatus>(value, ignoreCase: false, out var status)
            ? status : throw new InvalidDataException($"Unknown daily queue status '{value}'.");

    private static DateOnly ParseDay(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value)
    {
        var parsed = DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        if (!value.EndsWith('Z')) throw new FormatException("Persisted timestamps must be canonical UTC.");
        return parsed;
    }

    private static Guid ParseId(string value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
            ? id : throw new InvalidDataException($"Persisted queue ID '{value}' is invalid.");

    private static string DayText(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string UtcText(DateTimeOffset value) => MigrationRunner.UtcText(value);
    private static string IdText(Guid value) => value.ToString("D");

    private sealed record PersistedQueueItem(
        Guid ItemId,
        Guid CardId,
        DailyQueueItemKind Kind,
        DateOnly OriginDay);
}
