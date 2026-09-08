using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;

namespace WordFlow.Infrastructure.Data;

public sealed class SqliteVocabularyRepository : IVocabularyRepository
{
    // Promoted corpus databases are immutable for the repository lifetime; the
    // count-based vocabulary/sense snapshot identities rely on that deployment invariant.
    private readonly SqliteConnectionFactory factory;

    public SqliteVocabularyRepository(SqliteConnectionFactory factory) =>
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public Task<Page<VocabularyWord>> GetWordsAsync(PageRequest page, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => GetWordsCoreAsync(page, ct));

    private async Task<Page<VocabularyWord>> GetWordsCoreAsync(PageRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        await using var connection = await factory.OpenVocabularyAsync(ct).ConfigureAwait(false);
        var total = await CountAsync(connection, "SELECT COUNT(*) FROM vocabulary", ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stable_id,word,frequency_rank,phonetic,translation_zh_cn,definition_en,pos FROM vocabulary ORDER BY frequency_rank IS NULL, frequency_rank, word COLLATE NOCASE, stable_id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", page.Limit);
        command.Parameters.AddWithValue("$offset", page.Offset);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<VocabularyWord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(ReadWord(reader));
        }
        return new Page<VocabularyWord>(items, total, page.Offset + items.Count < total, $"vocabulary:{total}");
    }

    public Task<ExhaustionProbe> ProbeWordsEndAsync(int offset, string snapshotId, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => ProbeWordsEndCoreAsync(offset, ct));

    private async Task<ExhaustionProbe> ProbeWordsEndCoreAsync(int offset, CancellationToken ct)
    {
        await using var connection = await factory.OpenVocabularyAsync(ct).ConfigureAwait(false);
        var total = await CountAsync(connection, "SELECT COUNT(*) FROM vocabulary", ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM vocabulary ORDER BY frequency_rank IS NULL,frequency_rank,word COLLATE NOCASE,stable_id LIMIT 1 OFFSET $offset)";
        command.Parameters.AddWithValue("$offset", offset);
        return new(Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0, $"vocabulary:{total}");
    }

    public Task<VocabularyWord?> GetWordAsync(Guid wordId, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => GetWordCoreAsync(wordId, ct));

    private async Task<VocabularyWord?> GetWordCoreAsync(Guid wordId, CancellationToken ct)
    {
        if (wordId == Guid.Empty) throw new ArgumentException("A word ID cannot be empty.", nameof(wordId));
        await using var connection = await factory.OpenVocabularyAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stable_id,word,frequency_rank,phonetic,translation_zh_cn,definition_en,pos FROM vocabulary WHERE stable_id=$wordId";
        command.Parameters.AddWithValue("$wordId", wordId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return ReadWord(reader);
    }

    public Task<WordEntry?> GetWordEntryAsync(Guid wordId, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => GetWordEntryCoreAsync(wordId, ct));

    private async Task<WordEntry?> GetWordEntryCoreAsync(Guid wordId, CancellationToken ct)
    {
        if (wordId == Guid.Empty) throw new ArgumentException("A word ID cannot be empty.", nameof(wordId));
        await using var connection = await factory.OpenVocabularyAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stable_id,word,phonetic,translation_zh_cn,definition_en,pos,exchange,tags,tier,frequency_rank,collins,oxford,bnc_rank FROM vocabulary WHERE stable_id=$wordId";
        command.Parameters.AddWithValue("$wordId", wordId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return ReadEntry(reader);
    }

    public Task<Page<VocabularySense>> GetSensesAsync(Guid wordId, PageRequest page, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => GetSensesCoreAsync(wordId, page, ct));

    private async Task<Page<VocabularySense>> GetSensesCoreAsync(Guid wordId, PageRequest page, CancellationToken ct)
    {
        if (wordId == Guid.Empty) throw new ArgumentException("A word ID cannot be empty.", nameof(wordId));
        ArgumentNullException.ThrowIfNull(page);
        await using var connection = await factory.OpenRelationsAsync(ct).ConfigureAwait(false);
        var id = wordId.ToString("D");
        var total = await CountAsync(connection, "SELECT COUNT(*) FROM lexical_sense WHERE entry_id=$wordId", ct, id).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sense_id, entry_id, definition_en, pos FROM lexical_sense WHERE entry_id=$wordId ORDER BY sense_id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$wordId", id);
        command.Parameters.AddWithValue("$limit", page.Limit);
        command.Parameters.AddWithValue("$offset", page.Offset);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<VocabularySense>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var senseId = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(senseId)) throw new InvalidDataException("Corpus sense ID is blank.");
            items.Add(new VocabularySense(senseId, ParseId(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
        }
        return new Page<VocabularySense>(items, total, page.Offset + items.Count < total, $"senses:{wordId:D}:{total}");
    }

    public Task<ExhaustionProbe> ProbeSensesEndAsync(Guid wordId, int offset, string snapshotId, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => ProbeSensesEndCoreAsync(wordId, offset, ct));

    private async Task<ExhaustionProbe> ProbeSensesEndCoreAsync(Guid wordId, int offset, CancellationToken ct)
    {
        await using var connection = await factory.OpenRelationsAsync(ct).ConfigureAwait(false);
        var total = await CountAsync(connection, "SELECT COUNT(*) FROM lexical_sense WHERE entry_id=$wordId", ct, wordId.ToString("D")).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM lexical_sense WHERE entry_id=$wordId ORDER BY sense_id LIMIT 1 OFFSET $offset)";
        command.Parameters.AddWithValue("$wordId", wordId.ToString("D"));
        command.Parameters.AddWithValue("$offset", offset);
        return new(Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0, $"senses:{wordId:D}:{total}");
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

    private static string NormalizeNewlines(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value
            .Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal);
    }

    private static VocabularyWord ReadWord(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetInt32(2),
        true,
        reader.GetString(3),
        NormalizeNewlines(reader.GetString(4)),
        reader.IsDBNull(5) ? "" : NormalizeNewlines(reader.GetString(5)),
        reader.IsDBNull(6) ? "" : reader.GetString(6));

    private static WordEntry ReadEntry(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        NormalizeNewlines(reader.GetString(3)),
        reader.IsDBNull(4) ? null : NormalizeNewlines(reader.GetString(4)),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : NormalizeNewlines(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetInt32(9),
        reader.IsDBNull(10) ? null : reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetInt32(11),
        reader.IsDBNull(12) ? null : reader.GetInt32(12));
}
