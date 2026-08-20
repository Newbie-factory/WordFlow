using Microsoft.Extensions.DependencyInjection;
using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Data;
using WordFlow.Infrastructure.Windows;

namespace WordFlow.App.Bootstrap;

public static class ServiceRegistration
{
    public static ServiceProvider BuildPrimaryServices(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(new SqliteConnectionFactory(
            paths.UserDatabasePath, paths.VocabularyDatabasePath, paths.RelationDatabasePath));
        services.AddSingleton(provider => new MigrationRunner(
            provider.GetRequiredService<SqliteConnectionFactory>(), backupDirectory: paths.BackupsDirectory));
        services.AddSingleton<ILearningStore, SqliteLearningStore>();
        services.AddSingleton<IVocabularyRepository, SqliteVocabularyRepository>();
        services.AddSingleton<IRelationRepository, SqliteRelationRepository>();
        services.AddSingleton<IShortcutService>(provider =>
            new GlobalShortcutService(provider.GetRequiredService<SqliteConnectionFactory>()));
        return services.BuildServiceProvider(validateScopes: true);
    }
}
