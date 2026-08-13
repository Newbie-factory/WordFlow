using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;

namespace WordFlow.Infrastructure.Data;

internal static class SqliteStorageBoundary
{
    public static async Task<T> TranslateAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (SqliteException exception) when (IsTransient(exception))
        { throw new TransientStorageException("The SQLite repository is temporarily unavailable.", exception); }
    }

    public static async Task TranslateAsync(Func<Task> operation)
    {
        try { await operation().ConfigureAwait(false); }
        catch (SqliteException exception) when (IsTransient(exception))
        { throw new TransientStorageException("The SQLite repository is temporarily unavailable.", exception); }
    }

    private static bool IsTransient(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6 or 10 or 13 or 14 or 15;
}
