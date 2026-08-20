using Microsoft.Extensions.DependencyInjection;
using WordFlow.App.Bootstrap;
using WordFlow.App.ViewModels;
using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Data;

namespace WordFlow.App.Tests.Bootstrap;

public sealed class ServiceRegistrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"WordFlow.Services.{Guid.NewGuid():N}");

    [Fact]
    public async Task Primary_migration_writes_pre_upgrade_backup_to_the_dedicated_backups_directory()
    {
        var paths = new AppPaths(root, Path.Combine(root, "bundle"));
        paths.Initialize();
        string migrationSql = await File.ReadAllTextAsync(Path.Combine(
            FindRepositoryRoot(), "src", "WordFlow.Infrastructure", "Data", "Migrations", "001_initial.sql"));
        var factory = new SqliteConnectionFactory(paths.UserDatabasePath);
        await new MigrationRunner(factory, [new SqliteMigration(1, "initial", migrationSql)]).MigrateAsync(default);

        using ServiceProvider services = ServiceRegistration.BuildPrimaryServices(paths);
        await services.GetRequiredService<MigrationRunner>().MigrateAsync(default);

        Assert.Single(Directory.GetFiles(paths.BackupsDirectory, "wordflow.sqlite3.v1.*.backup"));
        Assert.Empty(Directory.GetFiles(paths.DataDirectory, "wordflow.sqlite3.v1.*.backup"));
    }

    [Fact]
    public async Task Primary_services_own_one_offline_pronunciation_service_and_its_settings_model()
    {
        var paths = new AppPaths(Path.Combine(root, "pronunciation"), Path.Combine(root, "bundle"));
        paths.Initialize();
        using ServiceProvider services = ServiceRegistration.BuildPrimaryServices(paths);
        await services.GetRequiredService<MigrationRunner>().MigrateAsync(default);

        var service = services.GetRequiredService<IPronunciationService>();
        var settings = services.GetRequiredService<PronunciationSettingsViewModel>();

        Assert.Same(service, services.GetRequiredService<IPronunciationService>());
        Assert.Same(settings, services.GetRequiredService<PronunciationSettingsViewModel>());
        Assert.DoesNotContain("http", service.Availability.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WordFlow.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("WordFlow repository root not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
