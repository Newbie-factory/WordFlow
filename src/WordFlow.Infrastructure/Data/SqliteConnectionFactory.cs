using Microsoft.Data.Sqlite;

namespace WordFlow.Infrastructure.Data;

public sealed class SqliteConnectionFactory
{
    private const int DefaultBusyTimeoutMilliseconds = 5000;
    private readonly int busyTimeoutMilliseconds;

    public SqliteConnectionFactory(
        string userDatabasePath,
        string? vocabularyDatabasePath = null,
        string? relationDatabasePath = null,
        int busyTimeoutMilliseconds = DefaultBusyTimeoutMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(userDatabasePath)) throw new ArgumentException("A user database path is required.", nameof(userDatabasePath));
        UserDatabasePath = Path.GetFullPath(userDatabasePath);
        VocabularyDatabasePath = vocabularyDatabasePath is null ? null : Path.GetFullPath(vocabularyDatabasePath);
        RelationDatabasePath = relationDatabasePath is null ? VocabularyDatabasePath : Path.GetFullPath(relationDatabasePath);
        this.busyTimeoutMilliseconds = busyTimeoutMilliseconds >= 0 ? busyTimeoutMilliseconds : throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));
    }

    public string UserDatabasePath { get; }

    public string? VocabularyDatabasePath { get; }

    public string? RelationDatabasePath { get; }

    public SqliteConnectionFactory WithCorpus(string corpusDatabasePath) => new(UserDatabasePath, corpusDatabasePath, corpusDatabasePath);

    public async Task<SqliteConnection> OpenUserAsync(CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(UserDatabasePath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = UserDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (busyTimeoutMilliseconds + 999) / 1000),
        }.ToString());
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ConfigureAsync(connection, writable: true, busyTimeoutMilliseconds, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<SqliteConnection> OpenVocabularyAsync(CancellationToken ct) =>
        OpenReadOnlyAsync(VocabularyDatabasePath, "A vocabulary database path was not configured.", busyTimeoutMilliseconds, ct);

    public Task<SqliteConnection> OpenRelationsAsync(CancellationToken ct) =>
        OpenReadOnlyAsync(RelationDatabasePath, "A relation database path was not configured.", busyTimeoutMilliseconds, ct);

    public Task<SqliteConnection> OpenCorpusAsync(CancellationToken ct) => OpenVocabularyAsync(ct);

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string? databasePath,
        string missingPathMessage,
        int busyTimeoutMilliseconds,
        CancellationToken ct)
    {
        if (databasePath is null) throw new InvalidOperationException(missingPathMessage);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (busyTimeoutMilliseconds + 999) / 1000),
        }.ToString());
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ConfigureAsync(connection, writable: false, busyTimeoutMilliseconds, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ConfigureAsync(SqliteConnection connection, bool writable, int busyTimeoutMilliseconds, CancellationToken ct)
    {
        if (writable)
        {
            await using var json = connection.CreateCommand();
            json.CommandText = "SELECT json_valid('{\"supported\":true}')";
            if (Convert.ToInt32(await json.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 1)
                throw new PlatformNotSupportedException("SQLite JSON functions are required for snapshot integrity.");
        }
        await using var command = connection.CreateCommand();
        command.CommandText = writable
            ? $"PRAGMA foreign_keys=ON; PRAGMA recursive_triggers=ON; PRAGMA busy_timeout={busyTimeoutMilliseconds}; PRAGMA journal_mode=WAL;"
            : $"PRAGMA query_only=ON; PRAGMA recursive_triggers=ON; PRAGMA busy_timeout={busyTimeoutMilliseconds};";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
