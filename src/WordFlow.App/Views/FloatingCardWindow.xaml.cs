using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using WordFlow.App.ViewModels;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;
using WordFlow.Infrastructure.Windows;

namespace WordFlow.App.Views;

public partial class FloatingCardWindow : Window
{
    private const int DpiChangedMessage = 0x02E0;
    private readonly FloatingCardViewModel? viewModel;
    private readonly IShortcutService? shortcutService;
    private readonly FocusedShortcutBindingBridge? focusedShortcuts;
    private readonly IDisposable? shortcutFaultConnection;
    private readonly WindowPlacementService? placementService;
    private readonly DispatcherTimer? fullscreenTimer;
    private HwndSource? source;
    private bool applyingPlacement;
    private bool suppressTopmostForFullscreen = true;

    public FloatingCardWindow() => InitializeComponent();

    public FloatingCardWindow(
        FloatingCardViewModel viewModel,
        WindowPlacementService placementService)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.placementService = placementService ?? throw new ArgumentNullException(nameof(placementService));
        InitializeComponent();
        DataContext = viewModel;

        viewModel.Synonyms.ActionRequested += OnRelationActionRequested;
        viewModel.Confusables.ActionRequested += OnRelationActionRequested;
        ContentRendered += OnContentRendered;
        LocationChanged += OnLocationChanged;
        SizeChanged += OnSizeChanged;
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        fullscreenTimer = new(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        fullscreenTimer.Tick += OnFullscreenTimerTick;
        fullscreenTimer.Start();
    }

    public FloatingCardWindow(
        FloatingCardViewModel viewModel,
        IShortcutService shortcutService,
        WindowPlacementService placementService,
        ShortcutCallbackFaultHub? faultHub = null)
        : this(viewModel, placementService)
    {
        this.shortcutService = shortcutService ?? throw new ArgumentNullException(nameof(shortcutService));

        focusedShortcuts = new(shortcutService);
        shortcutFaultConnection = (faultHub ?? new ShortcutCallbackFaultHub()).Connect(shortcutService, focusedShortcuts);
        shortcutService.ActionInvoked += OnShortcutActionInvoked;
        focusedShortcuts.ActionInvoked += OnShortcutActionInvoked;
        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public event EventHandler<RelationActionRequestedEventArgs>? RelationActionRequested;

    public bool SuppressTopmostForFullscreen
    {
        get => suppressTopmostForFullscreen;
        set
        {
            suppressTopmostForFullscreen = value;
            RefreshTopmost();
        }
    }

    public void SetPaused(bool paused)
    {
        StableWordBlock.IsEnabled = !paused;
        AccessibleStatus.Text = paused ? "学习已暂停" : viewModel?.AccessibleStatus;
    }

    private async void OnContentRendered(object? sender, EventArgs args)
    {
        RepairPlacement(useSavedPlacement: true);
        if (viewModel is not null) await viewModel.InitializeAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        if (shortcutService is null) return;
        var handle = new WindowInteropHelper(this).Handle;
        var attach = shortcutService.AttachWindowHandle(handle);
        if (!attach.Succeeded) throw new InvalidOperationException(attach.ConflictReason);
        shortcutService.RestorePersisted();
        source = HwndSource.FromHwnd(handle);
        source?.AddHook(WindowProcedure);
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (shortcutService?.ProcessWindowMessage(message, wParam, lParam) == true) handled = true;
        if (message == DpiChangedMessage) Dispatcher.BeginInvoke(() => RepairPlacement(useSavedPlacement: false));
        return 0;
    }

    private void DragRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.Left && args.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (focusedShortcuts is null) return;
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        args.Handled = focusedShortcuts.TryInvoke(WpfShortcutChordCapture.FromKey(key, Keyboard.Modifiers));
    }

    private async void OnShortcutActionInvoked(object? sender, ShortcutAction action)
    {
        if (viewModel is not null) await viewModel.HandleShortcutAsync(action);
    }

    private void OnRelationActionRequested(object? sender, RelationActionRequestedEventArgs args) =>
        RelationActionRequested?.Invoke(this, args);

    private void OnLocationChanged(object? sender, EventArgs args) => SavePlacement();

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!IsLoaded || applyingPlacement) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => RepairPlacement(useSavedPlacement: false));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs args) =>
        Dispatcher.BeginInvoke(() => RepairPlacement(useSavedPlacement: false));

    private void RepairPlacement(bool useSavedPlacement)
    {
        if (placementService is null) return;
        var monitors = WindowPlacementService.CaptureCurrentMonitors();
        if (monitors.Count == 0) return;
        var height = Math.Max(MinHeight, ActualHeight);
        WindowPlacement? candidate = useSavedPlacement
            ? placementService.Load()
            : new(WindowPlacementService.MonitorFor(Left, Top, Width, height, monitors), Left, Top);
        var resolved = WindowPlacementService.Resolve(candidate, monitors, Width, height);
        applyingPlacement = true;
        try
        {
            Left = resolved.LeftDip;
            Top = resolved.TopDip;
        }
        finally { applyingPlacement = false; }
        placementService.Save(resolved);
    }

    private void SavePlacement()
    {
        if (placementService is null || applyingPlacement || !IsLoaded || !double.IsFinite(Left) || !double.IsFinite(Top)) return;
        var monitors = WindowPlacementService.CaptureCurrentMonitors();
        if (monitors.Count == 0) return;
        var monitor = WindowPlacementService.MonitorFor(Left, Top, Width, Math.Max(MinHeight, ActualHeight), monitors);
        placementService.Save(new(monitor, Left, Top));
    }

    private void OnFullscreenTimerTick(object? sender, EventArgs args) => RefreshTopmost();

    private void RefreshTopmost()
    {
        bool fullscreen = WindowPlacementService.IsForegroundFullscreen();
        Topmost = WindowPlacementService.ShouldBeTopmost(SuppressTopmostForFullscreen, fullscreen);
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        fullscreenTimer?.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (source is not null)
        {
            source.RemoveHook(WindowProcedure);
            source = null;
        }
        if (shortcutService is not null) shortcutService.ActionInvoked -= OnShortcutActionInvoked;
        if (focusedShortcuts is not null)
        {
            focusedShortcuts.ActionInvoked -= OnShortcutActionInvoked;
            shortcutFaultConnection?.Dispose();
            focusedShortcuts.Dispose();
        }
        if (viewModel is not null)
        {
            viewModel.Synonyms.ActionRequested -= OnRelationActionRequested;
            viewModel.Confusables.ActionRequested -= OnRelationActionRequested;
            viewModel.Dispose();
        }
    }
}
