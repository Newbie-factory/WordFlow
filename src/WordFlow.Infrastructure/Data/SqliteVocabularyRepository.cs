using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;

namespace WordFlow.Infrastructure.Data;

public sealed class SqliteVocabularyRepository : IVocabularyRepository
{
    private readonly SqliteConnectionFactory factory;

    public SqliteVocabularyRepository(SqliteConnectionFactory factory) =>
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<Page<VocabularyWord>> GetWordsAsync(PageRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        await using var connection = await factory.OpenVocabularyAsync(ct).ConfigureAwait(false);
        var total = await CountAsync(connection, "SELECT COUNT(*) FROM vocabulary", ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stable_id, word, frequency_rank FROM vocabulary ORDER BY frequency_rank IS NULL, frequency_rank, word COLLATE NOCASE, stable_id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", page.Limit);
        command.Parameters.AddWithValue("$offset", page.Offset);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<VocabularyWord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new VocabularyWord(ParseId(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        }
        return new Page<VocabularyWord>(items, total);
    }

    public async Task<Page<VocabularySense>> GetSensesAsync(Guid wordId, PageRequest page, CancellationToken ct)
    {
        if (wordId == Guid.Empty) throw new ArgumentException("A word ID cannot be empty.", nameof(wordId));
        ArgumentNullException.ThrowIfNull(page);
        await using var connection = await factory.OpenRelationsAsync(ct).ConfigureAwait(false);
        var id = wordId.ToString("D");
        var total = await CountAsync(connection, "SELECT COUNT(*) FROM lexical_sense WHERE entry_id=$wordId", ct, id).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sense_id, entry_id, definition_en FROM lexical_sense WHERE entry_id=$wordId ORDER BY sense_id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$wordId", id);
        command.Parameters.AddWithValue("$limit", page.Limit);
        command.Parameters.AddWithValue("$offset", page.Offset);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<VocabularySense>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var senseId = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(senseId)) throw new InvalidDataException("Corpus sense ID is blank.");
            items.Add(new VocabularySense(senseId, ParseId(reader.GetString(1)), reader.GetString(2)));
        }
        return new Page<VocabularySense>(items, total);
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string sql, CancellationToken ct, string? wordId = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (wordId is not null) command.Parameters.AddWithValue("$wordId", wordId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static Guid ParseId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw new InvalidDataException($"Corpus ID '{value}' is not a canonical non-empty GUID.");
        return id;
    }
}
