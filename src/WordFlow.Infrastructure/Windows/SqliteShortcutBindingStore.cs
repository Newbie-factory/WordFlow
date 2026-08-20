using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Data;

namespace WordFlow.Infrastructure.Windows;

public sealed class SqliteShortcutBindingStore : IShortcutBindingStore
{
    private readonly string connectionString;
    private readonly int busyTimeoutMilliseconds;

    public SqliteShortcutBindingStore(SqliteConnectionFactory factory, int busyTimeoutMilliseconds = 5000)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (busyTimeoutMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));
        this.busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = factory.UserDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (busyTimeoutMilliseconds + 999) / 1000),
        }.ToString();
    }

    public IReadOnlyList<StoredShortcutBinding> Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT command, gesture FROM shortcut_binding ORDER BY command";
        using var reader = command.ExecuteReader();
        var rows = new List<StoredShortcutBinding>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    public IShortcutBindingStoreTransaction BeginReplace(IReadOnlyCollection<StoredShortcutBinding> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var connection = Open();
        try
        {
            var transaction = connection.BeginTransaction(deferred: false);
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM shortcut_binding";
                delete.ExecuteNonQuery();
            }
            foreach (var row in replacement)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO shortcut_binding(command, gesture) VALUES ($command, $gesture)";
                insert.Parameters.AddWithValue("$command", row.Command);
                insert.Parameters.AddWithValue("$gesture", row.Value);
                insert.ExecuteNonQuery();
            }
            return new Transaction(connection, transaction);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={busyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
        return connection;
    }

    private sealed class Transaction(SqliteConnection connection, SqliteTransaction transaction) : IShortcutBindingStoreTransaction
    {
        private bool committed;
        public void Commit() { transaction.Commit(); committed = true; }
        public void Dispose()
        {
            try
            {
                if (!committed)
                {
                    try { transaction.Rollback(); }
                    catch (SqliteException) { }
                }
            }
            finally
            {
                try { transaction.Dispose(); }
                finally { connection.Dispose(); }
            }
        }
    }
}
