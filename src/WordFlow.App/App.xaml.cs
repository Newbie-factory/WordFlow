using System.Windows;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using WordFlow.App.Bootstrap;
using WordFlow.App.ViewModels;
using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Data;
using WordFlow.Infrastructure.Windows;

namespace WordFlow.App;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource lifetime = new();
    private SingleInstanceCoordinator? coordinator;
    private ServiceProvider? services;
    private TrayIconService? trayIcon;
    private TrayLifecycleController? trayController;
    private LocalLifecycleLog? lifecycleLog;
    private ApplicationExitCoordinator? exitCoordinator;
    private Task? coordinatorMonitor;
    private MainWindow? card;
    private IShortcutService? shortcutService;
    private bool exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
                CreateCardAndTray,
                lifetime.Token);
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
            WriteLifecycle($"fatal-startup-error pid={Environment.ProcessId} type={exception.GetType().Name}");
            MessageBox.Show($"WordFlow could not start.\n\n{exception.Message}", "WordFlow startup error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync(1);
        }
    }

    private void CreateCardAndTray()
    {
        shortcutService = services?.GetRequiredService<IShortcutService>()
            ?? throw new InvalidOperationException("Primary services are not available.");
        shortcutService.CallbackFaulted += (_, args) =>
            WriteLifecycle($"shortcut-callback-fault pid={Environment.ProcessId} type={args.Exception.GetType().Name}");
        card = new MainWindow(shortcutService);
        card.Closing += (_, args) =>
        {
            if (exiting) return;
            args.Cancel = true;
            card.Hide();
            trayController?.SynchronizeCardVisibility(isVisible: false);
        };
        MainWindow = card;
        card.Show();

        trayController = new TrayLifecycleController(
            initiallyCardVisible: true,
            SetCardVisibility,
            paused => card.SetPaused(paused),
            () => ShowLocalInformation("Control Center", "The control center shell is ready."),
            () => ShowLocalInformation("Today Progress", "Today’s progress will appear here."),
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
        card.Topmost = true;
        card.Topmost = false;
        card.Focus();
    }

    private void SetCardVisibility(bool visible)
    {
        if (card is null) return;
        if (visible) { card.Show(); card.Activate(); }
        else card.Hide();
        trayController?.SynchronizeCardVisibility(visible);
    }

    private void ShowLocalInformation(string title, string message) =>
        MessageBox.Show(card, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

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

    private ValueTask CloseCardAsync()
    {
        card?.Close();
        card = null;
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
