using Microsoft.Extensions.DependencyInjection;
using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Data;

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
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<ILearningStore, SqliteLearningStore>();
        services.AddSingleton<IVocabularyRepository, SqliteVocabularyRepository>();
        services.AddSingleton<IRelationRepository, SqliteRelationRepository>();
        return services.BuildServiceProvider(validateScopes: true);
    }
}
