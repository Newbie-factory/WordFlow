using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;

namespace WordFlow.Infrastructure.Data;

public sealed class SqliteRelationRepository : IRelationRepository
{
    private readonly SqliteConnectionFactory factory;

    public SqliteRelationRepository(SqliteConnectionFactory factory) =>
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<Page<WordRelation>> GetRelationsAsync(Guid sourceWordId, PageRequest page, CancellationToken ct)
    {
        if (sourceWordId == Guid.Empty) throw new ArgumentException("A source word ID cannot be empty.", nameof(sourceWordId));
        ArgumentNullException.ThrowIfNull(page);
        var source = sourceWordId.ToString("D");
        var merged = new Dictionary<(Guid Target, string Type), WordRelation>();

        await using (var corpus = await factory.OpenRelationsAsync(ct).ConfigureAwait(false))
        {
            await using var command = corpus.CreateCommand();
            command.CommandText = """
                SELECT target_entry_id, kind, direction
                FROM published_word_relation
                WHERE source_entry_id=$source
                UNION ALL
                SELECT source_entry_id, kind, direction
                FROM published_word_relation
                WHERE target_entry_id=$source AND direction='bidirectional' AND source_entry_id IS NOT NULL
                ORDER BY kind, target_entry_id
                """;
            command.Parameters.AddWithValue("$source", source);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var target = ParseId(reader.GetString(0));
                var type = reader.GetString(1);
                var direction = reader.GetString(2) switch
                {
                    "forward" => RelationDirection.Forward,
                    "bidirectional" => RelationDirection.Bidirectional,
                    var value => throw new InvalidDataException($"Unknown relation direction '{value}'."),
                };
                merged[(target, type)] = new WordRelation(sourceWordId, target, type, direction);
            }
        }

        await using (var user = await factory.OpenUserAsync(ct).ConfigureAwait(false))
        {
            await using var command = user.CreateCommand();
            command.CommandText = "SELECT target_word_id, relation_type, is_enabled FROM user_word_relation WHERE source_word_id=$source ORDER BY relation_type,target_word_id";
            command.Parameters.AddWithValue("$source", source);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var target = ParseId(reader.GetString(0));
                var type = reader.GetString(1);
                if (reader.GetInt64(2) == 0) merged.Remove((target, type));
                else merged[(target, type)] = new WordRelation(sourceWordId, target, type, RelationDirection.Forward);
            }
        }

        var ordered = merged.Values.OrderBy(relation => relation.RelationType, StringComparer.Ordinal)
            .ThenBy(relation => relation.TargetWordId).ToArray();
        return new Page<WordRelation>(ordered.Skip(page.Offset).Take(page.Limit).ToArray(), ordered.Length);
    }

    public async Task SetOverrideAsync(UserRelationOverride relationOverride, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(relationOverride);
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO user_word_relation(source_word_id,target_word_id,relation_type,is_enabled,updated_at_utc)
            VALUES ($source,$target,$type,$enabled,$updated)
            ON CONFLICT(source_word_id,target_word_id,relation_type) DO UPDATE SET is_enabled=excluded.is_enabled,updated_at_utc=excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$source", relationOverride.SourceWordId.ToString("D"));
        command.Parameters.AddWithValue("$target", relationOverride.TargetWordId.ToString("D"));
        command.Parameters.AddWithValue("$type", relationOverride.RelationType);
        command.Parameters.AddWithValue("$enabled", relationOverride.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$updated", MigrationRunner.UtcText(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static Guid ParseId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw new InvalidDataException($"Relation ID '{value}' is not a canonical non-empty GUID.");
        return id;
    }
}
