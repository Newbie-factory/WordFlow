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

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A setting key is required.", nameof(key));
    }
}
