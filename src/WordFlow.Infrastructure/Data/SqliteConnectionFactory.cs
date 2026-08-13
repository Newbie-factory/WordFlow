using Microsoft.Data.Sqlite;

namespace WordFlow.Infrastructure.Data;

public sealed class SqliteConnectionFactory
{
    private const int BusyTimeoutMilliseconds = 5000;

    public SqliteConnectionFactory(string userDatabasePath, string? corpusDatabasePath = null)
    {
        if (string.IsNullOrWhiteSpace(userDatabasePath)) throw new ArgumentException("A user database path is required.", nameof(userDatabasePath));
        UserDatabasePath = Path.GetFullPath(userDatabasePath);
        CorpusDatabasePath = corpusDatabasePath is null ? null : Path.GetFullPath(corpusDatabasePath);
    }

    public string UserDatabasePath { get; }

    public string? CorpusDatabasePath { get; }

    public SqliteConnectionFactory WithCorpus(string corpusDatabasePath) => new(UserDatabasePath, corpusDatabasePath);

    public async Task<SqliteConnection> OpenUserAsync(CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(UserDatabasePath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = UserDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ConfigureAsync(connection, writable: true, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SqliteConnection> OpenCorpusAsync(CancellationToken ct)
    {
        if (CorpusDatabasePath is null) throw new InvalidOperationException("A corpus database path was not configured.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = CorpusDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ConfigureAsync(connection, writable: false, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ConfigureAsync(SqliteConnection connection, bool writable, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = writable
            ? $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={BusyTimeoutMilliseconds}; PRAGMA journal_mode=WAL;"
            : $"PRAGMA query_only=ON; PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
