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
        await using var connection = await factory.OpenCorpusAsync(ct).ConfigureAwait(false);
        var total = await CountAsync(connection, "SELECT COUNT(*) FROM word", ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT word_id, lemma, frequency_rank FROM word ORDER BY frequency_rank, word_id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", page.Limit);
        command.Parameters.AddWithValue("$offset", page.Offset);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<VocabularyWord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new VocabularyWord(ParseId(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2)));
        }
        return new Page<VocabularyWord>(items, total);
    }

    public async Task<Page<VocabularySense>> GetSensesAsync(Guid wordId, PageRequest page, CancellationToken ct)
    {
        if (wordId == Guid.Empty) throw new ArgumentException("A word ID cannot be empty.", nameof(wordId));
        ArgumentNullException.ThrowIfNull(page);
        await using var connection = await factory.OpenCorpusAsync(ct).ConfigureAwait(false);
        var id = wordId.ToString("D");
        var total = await CountAsync(connection, "SELECT COUNT(*) FROM sense WHERE word_id=$wordId", ct, id).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sense_id, word_id, definition FROM sense WHERE word_id=$wordId ORDER BY sense_id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$wordId", id);
        command.Parameters.AddWithValue("$limit", page.Limit);
        command.Parameters.AddWithValue("$offset", page.Offset);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<VocabularySense>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new VocabularySense(ParseId(reader.GetString(0)), ParseId(reader.GetString(1)), reader.GetString(2)));
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
