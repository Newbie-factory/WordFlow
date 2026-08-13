using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;
using WordFlow.Infrastructure.Data;

namespace WordFlow.Infrastructure.Tests.Data;

public sealed class LegacyLearningHistoryImporterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wordflow-legacy-{Guid.NewGuid():N}");

    public LegacyLearningHistoryImporterTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Actual_8000_baseline_spellings_map_exactly_to_promoted_uuidv5_ids()
    {
        var baseline = CopyExternalBaseline();
        var promoted = CopyPromotedVocabulary();

        var mappings = await LegacyVocabularyIdMapper.ReadAsync(baseline, promoted, default);

        Assert.Equal(8000, mappings.Count);
        Assert.Equal(8000, mappings.Values.Distinct().Count());
        Assert.All(mappings, pair => Assert.True(pair.Key > 0 && pair.Value != Guid.Empty));
        await using var baselineConnection = new SqliteConnection($"Data Source={baseline};Mode=ReadOnly;Pooling=False");
        await baselineConnection.OpenAsync();
        await using var command = baselineConnection.CreateCommand();
        command.CommandText = "SELECT id FROM vocabulary WHERE word='in' COLLATE NOCASE";
        var inId = Convert.ToInt64(await command.ExecuteScalarAsync());
        Assert.Equal(Guid.Parse("15de6704-6eae-598d-b1fb-3fe9765ec297"), mappings[inId]);
    }

    [Fact]
    public async Task Import_maps_integer_word_id_and_preserves_full_state_and_all_history_content()
    {
        var baseline = CopyExternalBaseline();
        var promoted = CopyPromotedVocabulary();
        var legacyLearning = Path.Combine(directory, "legacy-learning.db");
        var user = Path.Combine(directory, "user.db");
        var mapping = await LegacyVocabularyIdMapper.ReadAsync(baseline, promoted, default);
        const long legacyWordId = 1;
        var stableId = mapping[legacyWordId];
        var history = BuildHistory(stableId);
        await CreateLegacyLearningAsync(legacyLearning, legacyWordId, history);
        var factory = new SqliteConnectionFactory(user, promoted);
        await new MigrationRunner(factory).MigrateAsync(default);

        var result = await new LegacyLearningHistoryImporter(factory)
            .ImportAsync(baseline, legacyLearning, default);

        Assert.Equal(stableId, result.StableIdsByLegacyId[legacyWordId]);
        Assert.Equal(history.Count, result.ReviewEventCount);
        Assert.Equal(history.Count(EventChangesSlash), result.SlashEventCount);
        Assert.Equal(history[^1].After, await new SqliteLearningStore(factory).GetCardAsync(stableId, default));
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(history.Count, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
        Assert.Equal(history.Count(EventChangesSlash), await ScalarAsync(connection, "SELECT COUNT(*) FROM slash_event"));
        var actual = await EventRowsAsync(connection);
        Assert.Equal(history.Count, actual.Count);
        for (var index = 0; index < history.Count; index++)
        {
            Assert.Equal(history[index].EventId, actual[index].EventId);
            Assert.Equal(history[index].Action.ToString(), actual[index].Action);
            Assert.Equal(history[index].CompensatesEventId, actual[index].Compensates);
            Assert.Equal(history[index].OccurredAt, actual[index].At);
            Assert.Equal(history[index].Before, actual[index].Before);
            Assert.Equal(history[index].After, actual[index].After);
        }
    }

    private static IReadOnlyList<ReviewEvent> BuildHistory(Guid cardId)
    {
        var initial = new CardState(cardId, null, Now);
        var good = EngineAt(Now).Review(Id(201), initial, Rating.Good);
        var slash = EngineAt(Now.AddMinutes(1)).Slash(Id(202), good.After);
        var restore = EngineAt(Now.AddMinutes(2)).Restore(Id(203), slash.After, RestoreMode.Immediate);
        var undo = EngineAt(Now.AddMinutes(3)).Undo(Id(204), restore);
        return [good, slash, restore, undo];
    }

    private static async Task CreateLegacyLearningAsync(string path, long legacyWordId, IReadOnlyList<ReviewEvent> history)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE review_event(
                event_id TEXT PRIMARY KEY,
                command_id TEXT NOT NULL UNIQUE,
                card_id INTEGER NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                action TEXT NOT NULL,
                compensates_event_id TEXT NULL,
                before_json TEXT NOT NULL,
                after_json TEXT NOT NULL);
            CREATE TABLE slash_event(
                event_id TEXT PRIMARY KEY,
                card_id INTEGER NOT NULL,
                action TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL);
            """);
        foreach (var item in history)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO review_event VALUES ($event,$command,$card,$at,$action,$compensates,$before,$after)";
            command.Parameters.AddWithValue("$event", item.EventId.ToString("D"));
            command.Parameters.AddWithValue("$command", CommandId(item.EventId).ToString("D"));
            command.Parameters.AddWithValue("$card", legacyWordId);
            command.Parameters.AddWithValue("$at", item.OccurredAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$action", item.Action.ToString());
            command.Parameters.AddWithValue("$compensates", item.CompensatesEventId?.ToString("D") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$before", LegacyJson(item.Before, legacyWordId));
            command.Parameters.AddWithValue("$after", LegacyJson(item.After, legacyWordId));
            await command.ExecuteNonQueryAsync();
            if (EventChangesSlash(item))
            {
                await using var slash = connection.CreateCommand();
                slash.CommandText = "INSERT INTO slash_event VALUES ($event,$card,$action,$at)";
                slash.Parameters.AddWithValue("$event", item.EventId.ToString("D"));
                slash.Parameters.AddWithValue("$card", legacyWordId);
                slash.Parameters.AddWithValue("$action", item.Action.ToString());
                slash.Parameters.AddWithValue("$at", item.OccurredAt.UtcDateTime.ToString("O"));
                await slash.ExecuteNonQueryAsync();
            }
        }
    }

    private static string LegacyJson(CardState card, long legacyWordId)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(card, JsonOptions))!.AsObject();
        node["Id"] = legacyWordId;
        return node.ToJsonString(JsonOptions);
    }

    private static bool EventChangesSlash(ReviewEvent item) => item.Before.Slash != item.After.Slash;
    private static Guid CommandId(Guid eventId) => new(eventId.ToByteArray().Select(value => (byte)(value ^ 0x5A)).ToArray());
    private static LearningActions EngineAt(DateTimeOffset time) => new(new Fsrs6Scheduler(), new FixedTimeProvider(time));
    private static Guid Id(int value) => new($"00000000-0000-0000-0000-{value:D12}");

    private string CopyExternalBaseline()
    {
        const string source = @"D:\baicizhan\data\ielts\ielts_vocabulary.sqlite3";
        Assert.True(File.Exists(source), $"Required retained baseline missing: {source}");
        var target = Path.Combine(directory, "ielts_vocabulary.sqlite3");
        File.Copy(source, target);
        return target;
    }

    private string CopyPromotedVocabulary()
    {
        var target = Path.Combine(directory, "vocabulary.sqlite3");
        File.Copy(Path.Combine(FindRepositoryRoot(), "data", "ielts", "vocabulary.sqlite3"), target);
        return target;
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "WordFlow.sln"))) current = Directory.GetParent(current)?.FullName;
        return current ?? throw new DirectoryNotFoundException();
    }

    private static async Task<IReadOnlyList<(Guid EventId, string Action, Guid? Compensates, DateTimeOffset At, CardState Before, CardState After)>> EventRowsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id,action,compensates_event_id,occurred_at_utc,before_json,after_json FROM review_event ORDER BY occurred_at_utc,event_id";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<(Guid, string, Guid?, DateTimeOffset, CardState, CardState)>();
        while (await reader.ReadAsync()) result.Add((
            Guid.Parse(reader.GetString(0)), reader.GetString(1),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(3)),
            JsonSerializer.Deserialize<CardState>(reader.GetString(4), JsonOptions)!,
            JsonSerializer.Deserialize<CardState>(reader.GetString(5), JsonOptions)!));
        return result;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
