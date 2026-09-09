using System.Windows;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using WordFlow.App.Bootstrap;
using WordFlow.App.ViewModels;
using WordFlow.App.Views;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Application.Relations;
using WordFlow.Infrastructure.Data;
using WordFlow.Infrastructure.Windows;

namespace WordFlow.App;

public partial class App : System.Windows.Application
{
    private const string AlwaysOnTopSetting = "floating_card.always_on_top";
    private readonly CancellationTokenSource lifetime = new();
    private SingleInstanceCoordinator? coordinator;
    private ServiceProvider? services;
    private TrayIconService? trayIcon;
    private TrayLifecycleController? trayController;
    private LocalLifecycleLog? lifecycleLog;
    private ApplicationExitCoordinator? exitCoordinator;
    private Task? coordinatorMonitor;
    private FloatingCardWindow? card;
    private IShortcutService? shortcutService;
    private IFloatingCardActionHost? cardActionHost;
    private PronunciationSettingsViewModel? pronunciationSettings;
    private ThemeSettingsViewModel? themeSettings;
    private DailyPlanSettingsViewModel? dailyPlanSettings;
    private ControlCenterWindow? controlCenter;
    private ControlCenterViewModel? controlCenterViewModel;
    private bool exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool openControlCenterForSmoke = e.Args.Contains("--control-center-smoke", StringComparer.OrdinalIgnoreCase);
        if (e.Args.Contains("--ui-smoke", StringComparer.OrdinalIgnoreCase))
        {
            StartUiSmoke(e.Args.Contains("--ui-smoke-production", StringComparer.OrdinalIgnoreCase));
            return;
        }
        exitCoordinator = CreateExitCoordinator();
        try
        {
            AppPaths paths = AppPaths.ForCurrentUser();
            coordinator = await SingleInstanceCoordinator.StartAsync(
                "WordFlow.Desktop",
                _ => Dispatcher.InvokeAsync(ActivatePrimary).Task,
                TimeSpan.FromSeconds(5),
                lifetime.Token,
                exception => WriteLifecycle($"coordinator-background-fault type={exception.GetType().Name}"));
            if (!coordinator.IsPrimary)
            {
                await ExitAsync(0);
                return;
            }
            coordinatorMonitor = ObserveCoordinatorAsync(coordinator);

            services = ServiceRegistration.BuildPrimaryServices(paths, new WpfShortcutDispatcher(Dispatcher));
            await BootstrapSequence.RunAsync(
                () =>
                {
                    paths.Initialize();
                    lifecycleLog = new LocalLifecycleLog(paths.LogsDirectory);
                    WriteLifecycle($"primary-elected pid={Environment.ProcessId}");
                },
                ct => CorpusIntegrityVerifier.VerifyAsync(paths, ct),
                ct => services.GetRequiredService<MigrationRunner>().MigrateAsync(ct),
                RestorePronunciationSettingsAsync,
                CreateCardAndTrayAsync,
                lifetime.Token);
            await ImportVerificationThemeAsync();
            if (openControlCenterForSmoke) OpenControlCenter();
        }
        catch (CorpusIntegrityException exception)
        {
            WriteLifecycle($"fatal-corpus-error pid={Environment.ProcessId}");
            MessageBox.Show(
                $"WordFlow cannot start because its local vocabulary data failed verification.\n\n{exception.Message}\n\n{exception.RepairInstructions}",
                "WordFlow local data repair required", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync(2);
        }
        catch (Exception exception)
        {
            Exception root = exception.GetBaseException();
            string detail = root.Message.Replace('\r', ' ').Replace('\n', ' ');
            WriteLifecycle($"fatal-startup-error pid={Environment.ProcessId} type={exception.GetType().Name} root={root.GetType().Name} detail={detail}");
            MessageBox.Show($"WordFlow could not start.\n\n{exception.Message}", "WordFlow startup error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync(1);
        }
    }

    private async Task ImportVerificationThemeAsync()
    {
        string? importPath = AppPaths.ResolveVerificationImportPath(
            Environment.GetEnvironmentVariable(AppPaths.VerificationImportEnvironmentVariable),
            Environment.GetEnvironmentVariable(AppPaths.VerificationModeEnvironmentVariable),
            Environment.GetEnvironmentVariable(AppPaths.VerificationRootEnvironmentVariable));
        if (importPath is null) return;
        if (themeSettings is null) throw new InvalidOperationException("Theme settings are unavailable for verification import.");

        await themeSettings.ImportAsync(importPath, themeSettings.Opacity, lifetime.Token);
        await themeSettings.FlushAsync(lifetime.Token);
        WriteLifecycle($"verification-import-complete pid={Environment.ProcessId} extension={Path.GetExtension(importPath).ToLowerInvariant()} stored={themeSettings.DisplayName}");
    }

    private void StartUiSmoke(bool productionActions)
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var placementPath = Path.Combine(Path.GetTempPath(), $"wordflow-ui-smoke-{Environment.ProcessId}.json");
        cardActionHost = productionActions ? FloatingCardActionHost.Unavailable : null;
        var smokeViewModel = UiSmokeCardFactory.CreateViewModel(cardActionHost);
        card = new FloatingCardWindow(smokeViewModel, new WindowPlacementService(placementPath));
        card.EnableUiSmokeControlMessages = true;
        var workAreaArgument = Environment.GetCommandLineArgs().FirstOrDefault(argument => argument.StartsWith("--ui-smoke-work-area-height=", StringComparison.OrdinalIgnoreCase));
        if (workAreaArgument is not null && int.TryParse(workAreaArgument[(workAreaArgument.IndexOf('=') + 1)..], out var workAreaHeight))
            card.WorkAreaHeightLimitPx = workAreaHeight;
        card.RelationActionRequested += (_, request) => WriteLifecycle($"ui-smoke-card-action action={request.Action} word={request.Word}");
        MainWindow = card;
        card.Show();
        card.PrepareUiSmokeFocus();
    }

    private async Task RestorePronunciationSettingsAsync(CancellationToken cancellationToken)
    {
        pronunciationSettings = services?.GetRequiredService<PronunciationSettingsViewModel>()
            ?? throw new InvalidOperationException("Primary services are not available.");
        try
        {
            await pronunciationSettings.RestoreAsync(cancellationToken);
            if (pronunciationSettings.SettingsIssue is not null)
                WriteLifecycle("pronunciation-settings-sanitized");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            WriteLifecycle($"pronunciation-setting-read-fault type={exception.GetType().Name}");
        }
    }

    private async Task CreateCardAndTrayAsync(CancellationToken cancellationToken)
    {
        shortcutService = services?.GetRequiredService<IShortcutService>()
            ?? throw new InvalidOperationException("Primary services are not available.");
        var shortcutFaults = new ShortcutCallbackFaultHub();
        shortcutFaults.CallbackFaulted += (_, args) =>
            WriteLifecycle($"shortcut-callback-fault pid={Environment.ProcessId} type={args.Exception.GetType().Name}");
        var pronunciation = pronunciationSettings
            ?? throw new InvalidOperationException("Pronunciation settings were not prepared.");
        dailyPlanSettings = services.GetRequiredService<DailyPlanSettingsViewModel>();
        await dailyPlanSettings.RestoreAsync(cancellationToken);
        var getWordEntry = services.GetRequiredService<GetWordEntry>();
        cardActionHost = new FloatingCardActionHost(
            offlineSpeech: pronunciation,
            details: new DelegateFloatingCardActionPort((wordId, word) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    var entryViewModel = new WordEntryViewModel(getWordEntry, wordId, word);
                    var window = new WordEntryWindow(entryViewModel)
                    {
                        Owner = card is { IsVisible: true } ? card : null,
                    };
                    window.Show();
                });
                return FloatingCardActionResult.Completed($"已打开 {word} 的完整词条");
            }));
        var viewModel = FloatingCardComposition.Create(
            services.GetRequiredService<GetNextCard>(),
            services.GetRequiredService<SubmitRating>(),
            services.GetRequiredService<SlashWord>(),
            services.GetRequiredService<UndoLastAction>(),
            services.GetRequiredService<GetSynonyms>(),
            services.GetRequiredService<GetConfusables>(),
            new ShortcutLabelMap(shortcutService),
            cardActionHost,
            pronunciation,
            dailyPlanSettings,
            services.GetRequiredService<LearningDataChangeNotifier>());
        var paths = services.GetRequiredService<AppPaths>();
        themeSettings = services.GetRequiredService<ThemeSettingsViewModel>();
        await themeSettings.RestoreAsync(cancellationToken);
        var appSettings = services.GetRequiredService<SqliteAppSettingStore>();
        bool alwaysOnTopEnabled;
        try { alwaysOnTopEnabled = await appSettings.GetBooleanAsync(AlwaysOnTopSetting, true, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            alwaysOnTopEnabled = true;
            WriteLifecycle($"app-setting-read-fault key={AlwaysOnTopSetting} type={exception.GetType().Name}");
        }
        card = new FloatingCardWindow(
            viewModel,
            shortcutService,
            new WindowPlacementService(Path.Combine(paths.DataDirectory, "floating-card-placement.json")),
            shortcutFaults,
            themeSettings,
            appSettings)
        {
            AlwaysOnTopEnabled = alwaysOnTopEnabled,
        };
        await card.RestoreCardScaleAsync(cancellationToken);
        controlCenterViewModel = new ControlCenterViewModel(
            dailyPlanSettings,
            new ShortcutSettingsViewModel(shortcutService),
            pronunciation,
            themeSettings,
            services.GetRequiredService<ILearningProgressReader>(),
            SynchronizationContext.Current,
            services.GetRequiredService<IVocabularyRepository>(),
            services.GetRequiredService<GetConfusables>(),
            services.GetRequiredService<ILearningStore>(),
            services.GetRequiredService<RestoreSlashedWords>(),
            services.GetRequiredService<IDailyQueueStore>(),
            services.GetRequiredService<TimeProvider>(),
            alwaysOnTopEnabled,
            services.GetRequiredService<LearningDataChangeNotifier>());
        await controlCenterViewModel.LoadAsync(cancellationToken);
        controlCenter = new ControlCenterWindow(controlCenterViewModel);
        controlCenter.AlwaysOnTopChanged += (_, _) =>
        {
            bool enabled = controlCenterViewModel.AlwaysOnTopEnabled;
            card.AlwaysOnTopEnabled = enabled;
            if (!appSettings.TrySetBoolean(AlwaysOnTopSetting, enabled, out var error) && error is not null)
                WriteLifecycle($"app-setting-save-fault key={AlwaysOnTopSetting} type={error.GetType().Name}");
        };
        card.RelationActionRequested += (_, request) =>
        {
            WriteLifecycle($"card-action-requested action={request.Action} word={request.Word}");
        };
        card.Closing += (_, args) =>
        {
            if (exiting) return;
            args.Cancel = true;
            card.PrepareForHide();
            card.Hide();
            trayController?.SynchronizeCardVisibility(isVisible: false);
        };
        MainWindow = card;
        card.Show();

        trayController = new TrayLifecycleController(
            initiallyCardVisible: true,
            SetCardVisibility,
            paused => card.SetPaused(paused),
            OpenControlCenter,
            OpenTodayProgress,
            ExitFromTray);
        trayIcon = new TrayIconService(trayController);
        WriteLifecycle($"primary-ready pid={Environment.ProcessId}");
    }

    private void ActivatePrimary()
    {
        WriteLifecycle($"activation-received pid={Environment.ProcessId}");
        if (card is null) return;
        SetCardVisibility(true);
        if (card.WindowState == WindowState.Minimized) card.WindowState = WindowState.Normal;
        card.Activate();
        card.Focus();
    }

    private void SetCardVisibility(bool visible)
    {
        if (card is null) return;
        if (visible) { card.Show(); card.Activate(); }
        else { card.PrepareForHide(); card.Hide(); }
        trayController?.SynchronizeCardVisibility(visible);
    }

    private void ShowLocalInformation(string title, string message) =>
        MessageBox.Show(card, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private void OpenControlCenter()
    {
        if (controlCenter is null)
        {
            if (controlCenterViewModel is null) return;
            controlCenter = new ControlCenterWindow(controlCenterViewModel);
        }
        if (!controlCenter.IsVisible) controlCenter.Show();
        if (controlCenter.WindowState == WindowState.Minimized) controlCenter.WindowState = WindowState.Normal;
        controlCenter.Activate();
        controlCenter.Focus();
    }

    private void OpenTodayProgress()
    {
        OpenControlCenter();
        controlCenter?.OpenDashboard();
    }

    private async void ExitFromTray() => await ExitAsync(0);

    private Task ExitAsync(int exitCode)
    {
        WriteLifecycle($"exit-requested pid={Environment.ProcessId} code={exitCode}");
        return exitCoordinator?.ExitAsync(exitCode)
            ?? throw new InvalidOperationException("The application exit coordinator is not initialized.");
    }

    private ApplicationExitCoordinator CreateExitCoordinator() => new(
        [
            CancelLifetimeAsync,
            DisposeTrayAsync,
            DisposeShortcutsAsync,
            CloseControlCenterAsync,
            CloseCardAsync,
            DisposeServicesAsync,
            DisposeCoordinatorAsync,
            DisposeLifetimeAsync,
        ],
        exitCode =>
        {
            WriteLifecycle($"exit-complete pid={Environment.ProcessId} code={exitCode}");
            Shutdown(exitCode);
        },
        exception => WriteLifecycle($"cleanup-fault pid={Environment.ProcessId} type={exception.GetType().Name}"));

    private ValueTask CancelLifetimeAsync()
    {
        exiting = true;
        lifetime.Cancel();
        return ValueTask.CompletedTask;
    }

    private ValueTask DisposeTrayAsync()
    {
        trayIcon?.Dispose();
        trayIcon = null;
        trayController = null;
        return ValueTask.CompletedTask;
    }

    private ValueTask DisposeShortcutsAsync()
    {
        shortcutService?.Dispose();
        shortcutService = null;
        return ValueTask.CompletedTask;
    }

    private async ValueTask CloseCardAsync()
    {
        if (card is not null) await card.CloseAsync();
        card = null;
        cardActionHost = null;
    }

    private ValueTask CloseControlCenterAsync()
    {
        if (controlCenter is not null)
        {
            controlCenter.PermitClose = true;
            controlCenter.Close();
        }
        controlCenter = null;
        controlCenterViewModel?.Dispose();
        controlCenterViewModel = null;
        return ValueTask.CompletedTask;
    }

    private ValueTask DisposeServicesAsync()
    {
        services?.Dispose();
        services = null;
        return ValueTask.CompletedTask;
    }

    private async ValueTask DisposeCoordinatorAsync()
    {
        if (coordinator is not null)
        {
            await coordinator.DisposeAsync();
            coordinator = null;
        }
    }

    private ValueTask DisposeLifetimeAsync()
    {
        lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ObserveCoordinatorAsync(SingleInstanceCoordinator observedCoordinator)
    {
        try { await observedCoordinator.Completion; }
        catch (Exception exception)
        {
            WriteLifecycle($"terminal-listener-fault pid={Environment.ProcessId} type={exception.GetType().Name}");
            Task exitTask = await Dispatcher.InvokeAsync(() => ExitAsync(1)).Task;
            await exitTask;
        }
    }

    private void WriteLifecycle(string eventName)
    {
        try { lifecycleLog?.Write(eventName); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
