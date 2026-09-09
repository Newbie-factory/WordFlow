using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;

namespace WordFlow.Infrastructure.Data;

public sealed class SqliteNotebookRepository(SqliteConnectionFactory factory) : INotebookRepository
{
    private readonly SqliteConnectionFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public Task<bool> AddAsync(Guid wordId, DateTimeOffset addedAtUtc, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => AddCoreAsync(wordId, addedAtUtc, ct));

    private async Task<bool> AddCoreAsync(Guid wordId, DateTimeOffset addedAtUtc, CancellationToken ct)
    {
        if (wordId == Guid.Empty) throw new ArgumentException("A word ID cannot be empty.", nameof(wordId));
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO notebook_entry(word_id, added_at_utc) VALUES($wordId, $addedAt) ON CONFLICT(word_id) DO NOTHING";
        command.Parameters.AddWithValue("$wordId", wordId.ToString("D"));
        command.Parameters.AddWithValue("$addedAt", SqliteStorageBoundaryText(addedAtUtc));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public Task<bool> RemoveAsync(Guid wordId, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => RemoveCoreAsync(wordId, ct));

    private async Task<bool> RemoveCoreAsync(Guid wordId, CancellationToken ct)
    {
        if (wordId == Guid.Empty) throw new ArgumentException("A word ID cannot be empty.", nameof(wordId));
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notebook_entry WHERE word_id=$wordId";
        command.Parameters.AddWithValue("$wordId", wordId.ToString("D"));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public Task<bool> ContainsAsync(Guid wordId, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => ContainsCoreAsync(wordId, ct));

    private async Task<bool> ContainsCoreAsync(Guid wordId, CancellationToken ct)
    {
        if (wordId == Guid.Empty) throw new ArgumentException("A word ID cannot be empty.", nameof(wordId));
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM notebook_entry WHERE word_id=$wordId)";
        command.Parameters.AddWithValue("$wordId", wordId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0;
    }

    public Task<Page<NotebookEntry>> GetEntriesAsync(PageRequest page, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => GetEntriesCoreAsync(page, ct));

    private async Task<Page<NotebookEntry>> GetEntriesCoreAsync(PageRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM notebook_entry";
        int total = Convert.ToInt32(await count.ExecuteScalarAsync(ct).ConfigureAwait(false));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT word_id, added_at_utc FROM notebook_entry ORDER BY added_at_utc DESC, word_id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", page.Limit);
        command.Parameters.AddWithValue("$offset", page.Offset);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<NotebookEntry>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var wordId = ParseId(reader.GetString(0));
            items.Add(new NotebookEntry(wordId, ParseUtc(reader.GetString(1))));
        }
        return new Page<NotebookEntry>(items, total, page.Offset + items.Count < total, $"notebook:{total}");
    }

    public Task<ExhaustionProbe> ProbeEntriesEndAsync(int offset, string snapshotId, CancellationToken ct) =>
        SqliteStorageBoundary.TranslateAsync(() => ProbeEntriesEndCoreAsync(offset, snapshotId, ct));

    private async Task<ExhaustionProbe> ProbeEntriesEndCoreAsync(int offset, string snapshotId, CancellationToken ct)
    {
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        var total = await CountAsync(connection, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM notebook_entry ORDER BY added_at_utc DESC, word_id LIMIT 1 OFFSET $offset)";
        command.Parameters.AddWithValue("$offset", offset);
        return new(Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0, $"notebook:{total}");
    }

    private static async Task<int> CountAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM notebook_entry";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static Guid ParseId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw new InvalidDataException($"Notebook entry ID '{value}' is not a canonical non-empty GUID.");
        return id;
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Notebook entry timestamp '{value}' is invalid.");

    private static string SqliteStorageBoundaryText(DateTimeOffset value) => value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}