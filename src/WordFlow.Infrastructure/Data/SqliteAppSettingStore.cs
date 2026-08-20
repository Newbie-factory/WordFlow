namespace WordFlow.Infrastructure.Data;

public sealed class SqliteAppSettingStore(SqliteConnectionFactory factory)
{
    private readonly SqliteConnectionFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<bool> GetBooleanAsync(string key, bool defaultValue, CancellationToken ct)
    {
        ValidateKey(key);
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT value FROM app_setting WHERE key=$key";
        read.Parameters.AddWithValue("$key", key);
        var raw = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (raw is string value && bool.TryParse(value, out var parsed)) return parsed;
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO app_setting(key,value) VALUES($key,$value) ON CONFLICT(key) DO NOTHING";
        insert.Parameters.AddWithValue("$key", key);
        insert.Parameters.AddWithValue("$value", defaultValue.ToString());
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return defaultValue;
    }

    public async Task SetBooleanAsync(string key, bool value, CancellationToken ct)
    {
        ValidateKey(key);
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO app_setting(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public bool TrySetBoolean(string key, bool value, out Exception? error)
    {
        try { SetBooleanAsync(key, value, default).GetAwaiter().GetResult(); error = null; return true; }
        catch (Exception exception) { error = exception; return false; }
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetManyAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (string key in keys) ValidateKey(key);
        var result = keys.Distinct(StringComparer.Ordinal).ToDictionary(key => key, _ => (string?)null, StringComparer.Ordinal);
        if (result.Count == 0) return result;
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key,value FROM app_setting";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string key = reader.GetString(0);
            if (result.ContainsKey(key)) result[key] = reader.GetString(1);
        }
        return result;
    }

    public async Task SetManyAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var pair in values)
        {
            ValidateKey(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
        }
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var pair in values)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO app_setting(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$key", pair.Key);
            command.Parameters.AddWithValue("$value", pair.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A setting key is required.", nameof(key));
    }
}
