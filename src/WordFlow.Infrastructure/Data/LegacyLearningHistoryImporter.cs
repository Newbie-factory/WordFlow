using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;

namespace WordFlow.Infrastructure.Data;

public sealed record LegacyImportResult(
    IReadOnlyDictionary<long, Guid> StableIdsByLegacyId,
    int ReviewEventCount,
    int SlashEventCount);

public sealed class LegacyLearningHistoryImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly SqliteConnectionFactory factory;

    public LegacyLearningHistoryImporter(SqliteConnectionFactory factory) =>
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<LegacyImportResult> ImportAsync(
        string legacyVocabularyPath,
        string legacyLearningPath,
        CancellationToken ct)
    {
        if (factory.VocabularyDatabasePath is null)
            throw new InvalidOperationException("A promoted vocabulary database is required for legacy import.");
        var mappings = await LegacyVocabularyIdMapper.ReadAsync(
            legacyVocabularyPath, factory.VocabularyDatabasePath, ct).ConfigureAwait(false);
        var commands = new List<TrustedReplayCommand>();
        var expectedSlashEvents = new List<(Guid EventId, long CardId, string Action, string At)>();
        await using var legacy = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(legacyLearningPath), Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        await legacy.OpenAsync(ct).ConfigureAwait(false);
        await using (var query = legacy.CreateCommand())
        {
            query.CommandText = "SELECT event_id,command_id,card_id,occurred_at_utc,action,compensates_event_id,before_json,after_json FROM review_event ORDER BY occurred_at_utc,event_id";
            await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var legacyCardId = reader.GetInt64(2);
                if (!mappings.TryGetValue(legacyCardId, out var stableId)) throw new InvalidDataException($"Learning event refers to unknown legacy word {legacyCardId}.");
                var before = ReadCard(reader.GetString(6), legacyCardId, stableId);
                var after = ReadCard(reader.GetString(7), legacyCardId, stableId);
                var @event = new ReviewEvent(
                    Guid.ParseExact(reader.GetString(0), "D"), stableId, ParseUtc(reader.GetString(3)),
                    Enum.Parse<LearningAction>(reader.GetString(4), ignoreCase: false), before, after,
                    reader.IsDBNull(5) ? null : Guid.ParseExact(reader.GetString(5), "D"));
                commands.Add(new TrustedReplayCommand(Guid.ParseExact(reader.GetString(1), "D"), @event));
            }
        }
        await using (var slash = legacy.CreateCommand())
        {
            slash.CommandText = "SELECT event_id,card_id,action,occurred_at_utc FROM slash_event ORDER BY occurred_at_utc,event_id";
            await using var reader = await slash.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                expectedSlashEvents.Add((Guid.ParseExact(reader.GetString(0), "D"), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
        }
        var projectedSlashEvents = commands.Where(command => command.Event.Before.Slash != command.Event.After.Slash).ToArray();
        if (projectedSlashEvents.Length != expectedSlashEvents.Count)
            throw new InvalidDataException("Legacy slash projection count does not match review history.");
        for (var index = 0; index < projectedSlashEvents.Length; index++)
        {
            var expected = expectedSlashEvents[index];
            var actual = projectedSlashEvents[index].Event;
            if (actual.EventId != expected.EventId || actual.Action.ToString() != expected.Action
                || actual.OccurredAt != ParseUtc(expected.At) || mappings[expected.CardId] != actual.CardId)
                throw new InvalidDataException("Legacy slash projection content does not match review history.");
        }

        await new SqliteLearningStore(factory).ApplyTrustedReplayBatchAsync(commands, ct).ConfigureAwait(false);
        return new LegacyImportResult(mappings, commands.Count, expectedSlashEvents.Count);
    }

    private static CardState ReadCard(string json, long expectedLegacyId, Guid stableId)
    {
        try
        {
            var node = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Legacy card JSON is null.");
            if (node["Id"]?.GetValue<long>() != expectedLegacyId) throw new InvalidDataException("Legacy card JSON ID disagrees with its event.");
            node["Id"] = stableId.ToString("D");
            return node.Deserialize<CardState>(JsonOptions) ?? throw new InvalidDataException("Legacy card JSON is null.");
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("Legacy card JSON is corrupt.", exception);
        }
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        var parsed = DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        if (!value.EndsWith('Z')) throw new InvalidDataException("Legacy timestamp must be canonical UTC.");
        return parsed;
    }
}
