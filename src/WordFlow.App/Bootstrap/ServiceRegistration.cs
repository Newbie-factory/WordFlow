using Microsoft.Extensions.DependencyInjection;
using WordFlow.App.ViewModels;
using WordFlow.Application.Ports;
using WordFlow.Application.Learning;
using WordFlow.Application.Relations;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;
using WordFlow.Infrastructure.Data;
using WordFlow.Infrastructure.Audio;
using WordFlow.Infrastructure.Windows;

namespace WordFlow.App.Bootstrap;

public static class ServiceRegistration
{
    public static ServiceProvider BuildPrimaryServices(AppPaths paths, IShortcutDispatcher? shortcutDispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(new SqliteConnectionFactory(
            paths.UserDatabasePath, paths.VocabularyDatabasePath, paths.RelationDatabasePath));
        services.AddSingleton(provider => new MigrationRunner(
            provider.GetRequiredService<SqliteConnectionFactory>(), backupDirectory: paths.BackupsDirectory));
        services.AddSingleton<SqliteLearningStore>();
        services.AddSingleton<ILearningStore>(provider => provider.GetRequiredService<SqliteLearningStore>());
        services.AddSingleton<ILearningProgressReader>(provider => provider.GetRequiredService<SqliteLearningStore>());
        services.AddSingleton<SqliteDailyQueueStore>();
        services.AddSingleton<IDailyQueueStore>(provider => provider.GetRequiredService<SqliteDailyQueueStore>());
        services.AddSingleton<SqliteAppSettingStore>();
        services.AddSingleton<DailyPlanSettingsViewModel>();
        services.AddSingleton<LearningDataChangeNotifier>();
        services.AddSingleton<ImageThemeService>();
        services.AddSingleton<ThemeSettingsViewModel>();
        services.AddSingleton<IPronunciationService, WindowsSpeechPronunciationService>();
        services.AddSingleton(provider => new PronunciationSettingsViewModel(
            provider.GetRequiredService<IPronunciationService>(),
            provider.GetRequiredService<SqliteAppSettingStore>(),
            SynchronizationContext.Current));
        services.AddSingleton<IVocabularyRepository, SqliteVocabularyRepository>();
        services.AddSingleton<IRelationRepository, SqliteRelationRepository>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFsrsScheduler, Fsrs6Scheduler>();
        services.AddSingleton<QueuePolicy>();
        services.AddSingleton<DailyQueueCoordinator>();
        services.AddSingleton<GetNextCard>();
        services.AddSingleton<GetWordEntry>();
        services.AddSingleton<SubmitRating>();
        services.AddSingleton<SlashWord>();
        services.AddSingleton<RestoreSlashedWords>();
        services.AddSingleton<UndoLastAction>();
        services.AddSingleton<GetSynonyms>();
        services.AddSingleton<GetConfusables>();
        services.AddSingleton(shortcutDispatcher ?? new OwnerThreadShortcutDispatcher());
        services.AddSingleton<IShortcutService>(provider =>
            new GlobalShortcutService(provider.GetRequiredService<SqliteConnectionFactory>(),
                provider.GetRequiredService<IShortcutDispatcher>()));
        return services.BuildServiceProvider(validateScopes: true);
    }
}
