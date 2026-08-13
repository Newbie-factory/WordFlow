using System.Reflection;
using Microsoft.Data.Sqlite;

namespace WordFlow.Infrastructure.Data;

public sealed record SqliteMigration(int Version, string Name, string Sql);

public sealed class MigrationRunner
{
    private readonly SqliteConnectionFactory factory;
    private readonly IReadOnlyList<SqliteMigration> migrations;

    public MigrationRunner(SqliteConnectionFactory factory, IEnumerable<SqliteMigration>? migrations = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.migrations = (migrations ?? new[] { LoadInitialMigration() })
            .OrderBy(migration => migration.Version)
            .ToArray();
        if (this.migrations.Any(migration => migration.Version <= 0 || string.IsNullOrWhiteSpace(migration.Sql))
            || this.migrations.Select(migration => migration.Version).Distinct().Count() != this.migrations.Count)
        {
            throw new ArgumentException("Migrations require unique positive versions and non-empty SQL.", nameof(migrations));
        }
    }

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var connection = await factory.OpenUserAsync(ct).ConfigureAwait(false);
        var currentVersion = await GetCurrentVersionAsync(connection, ct).ConfigureAwait(false);
        var pending = migrations.Where(migration => migration.Version > currentVersion).ToArray();
        if (pending.Length == 0) return;

        if (currentVersion > 0 && File.Exists(factory.UserDatabasePath))
        {
            var backupPath = BuildBackupPath(currentVersion);
            await BackupAsync(connection, backupPath, ct).ConfigureAwait(false);
        }

        await using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var migration in pending)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = migration.Sql;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var version = connection.CreateCommand();
            version.Transaction = transaction;
            version.CommandText = "INSERT INTO schema_version(version, name, applied_at_utc) VALUES ($version, $name, $appliedAt)";
            version.Parameters.AddWithValue("$version", migration.Version);
            version.Parameters.AddWithValue("$name", migration.Name);
            version.Parameters.AddWithValue("$appliedAt", UtcText(DateTimeOffset.UtcNow));
            await version.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private string BuildBackupPath(int currentVersion)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture);
        var path = $"{factory.UserDatabasePath}.v{currentVersion}.{timestamp}.backup";
        for (var suffix = 1; File.Exists(path); suffix++) path = $"{factory.UserDatabasePath}.v{currentVersion}.{timestamp}.{suffix}.backup";
        return path;
    }

    private static async Task BackupAsync(SqliteConnection source, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await destination.OpenAsync(ct).ConfigureAwait(false);
        source.BackupDatabase(destination);
        ct.ThrowIfCancellationRequested();
    }

    private static async Task<int> GetCurrentVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_version'";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0) return 0;
        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        return Convert.ToInt32(await version.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static SqliteMigration LoadInitialMigration()
    {
        const string suffix = ".Data.Migrations.001_initial.sql";
        var assembly = typeof(MigrationRunner).Assembly;
        var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Initial migration resource is missing.");
        using var reader = new StreamReader(stream);
        return new SqliteMigration(1, "initial", reader.ReadToEnd());
    }

    internal static string UtcText(DateTimeOffset value) => value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
