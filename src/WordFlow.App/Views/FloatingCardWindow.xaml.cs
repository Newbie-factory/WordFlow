using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using WordFlow.App.Bootstrap;
using WordFlow.App.ViewModels;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;
using WordFlow.Infrastructure.Windows;
using WordFlow.App.Views.Controls;

namespace WordFlow.App.Views;

public partial class FloatingCardWindow : Window
{
    private const int DpiChangedMessage = 0x02E0;
    private const int UiSmokeLifecycleMessage = 0x806F;
    private readonly FloatingCardViewModel? viewModel;
    private readonly IShortcutService? shortcutService;
    private readonly FocusedShortcutBindingBridge? focusedShortcuts;
    private readonly IDisposable? shortcutFaultConnection;
    private readonly WindowPlacementService? placementService;
    private readonly ThemeSettingsViewModel? themeSettings;
    private readonly DispatcherTimer? fullscreenTimer;
    private readonly DispatcherTimer? placementSaveTimer;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Task> pendingOperations = [];
    private HwndSource? source;
    private nint handle;
    private bool applyingPlacement;
    private bool suppressTopmostForFullscreen = true;
    private bool closing;
    private bool? uiSmokeTopmostOverride;
    private bool updatingThemeControls;

    public FloatingCardWindow() => InitializeComponent();

    public FloatingCardWindow(FloatingCardViewModel viewModel, WindowPlacementService placementService, ThemeSettingsViewModel? themeSettings = null)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.placementService = placementService ?? throw new ArgumentNullException(nameof(placementService));
        this.themeSettings = themeSettings;
        InitializeComponent();
        DataContext = viewModel;
        if (themeSettings is not null)
        {
            themeSettings.ThemeChanged += OnThemeChanged;
            themeSettings.Feedback += OnThemeFeedback;
            ApplyTheme(themeSettings.Current);
        }
        viewModel.ActionRequested += OnActionRequested;
        ContentRendered += OnContentRendered;
        LocationChanged += OnLocationChanged;
        SizeChanged += OnSizeChanged;
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SynonymsPopup.Opened += (_, _) => OnDrawerOpened(SynonymsDrawer);
        ConfusablesPopup.Opened += (_, _) => OnDrawerOpened(ConfusablesDrawer);
        SynonymsDrawer.DesiredHeightChanged += (_, _) => ConfigureDrawer(SynonymsDrawer);
        ConfusablesDrawer.DesiredHeightChanged += (_, _) => ConfigureDrawer(ConfusablesDrawer);
        SynonymsDrawer.DismissRequested += (_, _) => DismissDrawer(SynonymsDrawer);
        ConfusablesDrawer.DismissRequested += (_, _) => DismissDrawer(ConfusablesDrawer);
        WindowSurface.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(CardSurface_MouseUp), handledEventsToo: true);
        WindowSurface.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(CardSurface_KeyDown), handledEventsToo: true);

        placementSaveTimer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        placementSaveTimer.Tick += (_, _) => { placementSaveTimer.Stop(); SavePlacement(); };
        fullscreenTimer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        fullscreenTimer.Tick += (_, _) => RefreshTopmost();
        fullscreenTimer.Start();
    }

    public FloatingCardWindow(FloatingCardViewModel viewModel, IShortcutService shortcutService,
        WindowPlacementService placementService, ShortcutCallbackFaultHub? faultHub = null, ThemeSettingsViewModel? themeSettings = null) : this(viewModel, placementService, themeSettings)
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
    public event EventHandler<FullscreenSuppressionChangedEventArgs>? FullscreenSuppressionChanged;
    public int? WorkAreaHeightLimitPx { get; set; }
    public bool EnableUiSmokeControlMessages { get; set; }
    public bool SuppressTopmostForFullscreen
    {
        get => suppressTopmostForFullscreen;
        set
        {
            if (suppressTopmostForFullscreen == value) return;
            suppressTopmostForFullscreen = value;
            RefreshTopmost();
            FullscreenSuppressionChanged?.Invoke(this, new(value));
        }
    }

    public void SetPaused(bool paused)
    {
        viewModel?.SetPaused(paused);
        if (paused) CloseDrawers();
    }

    public void PrepareForHide() => CloseDrawers();

    public void PrepareUiSmokeFocus()
    {
        EnsureHandle();
        Activate();
        Topmost = true;
        NativeFocus.SetForegroundWindow(handle);
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            Activate();
            WordText.Focus();
            Keyboard.Focus(WordText);
        });
    }

    public async Task CloseAsync()
    {
        if (closing) return;
        closing = true;
        lifetime.Cancel();
        CloseDrawers();
        placementSaveTimer?.Stop();
        SavePlacement();
        Task[] pending;
        lock (pendingOperations) pending = pendingOperations.ToArray();
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        Close();
    }

    private void OnContentRendered(object? sender, EventArgs args)
    {
        EnsureHandle();
        RepairPlacement(useSavedPlacement: true);
        if (viewModel is not null) Track(viewModel.InitializeAsync(lifetime.Token));
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        EnsureHandle();
        if (shortcutService is null) return;
        var attach = shortcutService.AttachWindowHandle(handle);
        if (!attach.Succeeded) throw new InvalidOperationException(attach.ConflictReason);
        shortcutService.RestorePersisted();
    }

    private void EnsureHandle()
    {
        if (handle != 0) return;
        handle = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(handle);
        source?.AddHook(WindowProcedure);
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (shortcutService?.ProcessWindowMessage(message, wParam, lParam) == true) handled = true;
        if (EnableUiSmokeControlMessages && message == UiSmokeLifecycleMessage)
        {
            switch (wParam.ToInt32())
            {
                case 1: PrepareForHide(); Hide(); break;
                case 2: Show(); Activate(); break;
                case 3: SetPaused(true); break;
                case 4: SetPaused(false); break;
                case 5: uiSmokeTopmostOverride = false; RefreshTopmost(); break;
                case 6: uiSmokeTopmostOverride = true; RefreshTopmost(); break;
            }
            handled = true;
        }
        if (message == DpiChangedMessage && lParam != 0)
        {
            var native = Marshal.PtrToStructure<NativeRect>(lParam);
            var suggested = new PhysicalWindowRect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
            var monitors = CurrentMonitors();
            if (monitors.Count > 0)
            {
                var monitor = WindowPlacementService.MonitorFor(suggested, monitors);
                applyingPlacement = true;
                try { WindowPlacementService.SetWindowRectangle(hwnd, WindowPlacementService.ResolveSuggested(suggested, monitor)); }
                finally { applyingPlacement = false; }
                SchedulePlacementSave();
                handled = true;
            }
        }
        return 0;
    }

    private void DragRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.Left && args.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void CardSurface_MouseUp(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left || args.OriginalSource is not DependencyObject sourceElement) return;
        var route = RouteToRoot(sourceElement).Select(element => new CardSurfaceInteractionPolicy.RouteNode(
            element.GetType(),
            ReferenceEquals(element, WindowSurface),
            ReferenceEquals(element, DragRegion)));
        if (!CardSurfaceInteractionPolicy.ShouldOpenDetails(route, args.Handled)) return;
        args.Handled = true;
        viewModel?.RequestCurrentDetailsFromSurface();
    }

    private void CardSurface_KeyDown(object sender, KeyEventArgs args)
    {
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (args.Handled || !ReferenceEquals(args.OriginalSource, WindowSurface) || key is not (Key.Enter or Key.Space)) return;
        args.Handled = true;
        viewModel?.RequestCurrentDetailsFromSurface();
    }

    private static IEnumerable<DependencyObject> RouteToRoot(DependencyObject origin)
    {
        for (DependencyObject? current = origin; current is not null; current = ParentOf(current)) yield return current;
    }

    private static DependencyObject? ParentOf(DependencyObject element) => element switch
    {
        FrameworkContentElement content => content.Parent,
        ContentElement content => ContentOperations.GetParent(content),
        Visual or Visual3D => VisualTreeHelper.GetParent(element),
        _ => LogicalTreeHelper.GetParent(element),
    };
    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (focusedShortcuts is null) return;
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        args.Handled = focusedShortcuts.TryInvoke(WpfShortcutChordCapture.FromKey(key, Keyboard.Modifiers));
    }
    private void OnShortcutActionInvoked(object? sender, ShortcutAction action)
    {
        if (viewModel is not null && !viewModel.IsPaused) Track(viewModel.HandleShortcutAsync(action, lifetime.Token));
    }
    private void OnActionRequested(object? sender, RelationActionRequestedEventArgs args) => RelationActionRequested?.Invoke(this, args);
    private void OnThemeChanged(object? sender, EventArgs args) => ApplyTheme(themeSettings?.Current ?? ImageTheme.Default);
    private void OnThemeFeedback(string message) { }

    private void ThemeOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!updatingThemeControls && themeSettings is not null) themeSettings.Opacity = args.NewValue;
    }

    private void ApplyTheme(ImageTheme theme)
    {
        ImageBrush? brush = null;
        if (!theme.IsDefault)
        {
            brush = new ImageBrush(new BitmapImage(new Uri(theme.ImagePath, UriKind.Absolute))) { Stretch = Stretch.Fill };
        }
        ThemeImageLayer.Background = brush;
        updatingThemeControls = true;
        ThemeOpacitySlider.Value = theme.Opacity;
        updatingThemeControls = false;
        ThemeImageLayer.Opacity = theme.IsDefault ? 0 : theme.Opacity;
        ThemeImageLayer.Visibility = theme.IsDefault ? Visibility.Collapsed : Visibility.Visible;
        ThemeReadabilityVeil.Opacity = theme.IsDefault ? 0 : 0.82;
        SynonymsDrawer.ThemeBrush = brush;
        ConfusablesDrawer.ThemeBrush = brush;
        SynonymsDrawer.ThemeOpacity = theme.IsDefault ? 0 : theme.Opacity;
        ConfusablesDrawer.ThemeOpacity = theme.IsDefault ? 0 : theme.Opacity;
    }

    private void Track(Task operation)
    {
        lock (pendingOperations) pendingOperations.Add(operation);
        _ = ObserveAsync(operation);
    }
    private async Task ObserveAsync(Task operation)
    {
        try { await operation; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { viewModel?.ReportActionFeedback($"操作失败：{exception.Message}", true); }
        finally { lock (pendingOperations) pendingOperations.Remove(operation); }
    }

    private void OnLocationChanged(object? sender, EventArgs args) => SchedulePlacementSave();
    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!IsLoaded || applyingPlacement) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => RepairPlacement(useSavedPlacement: false));
    }
    private void OnDisplaySettingsChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(() => RepairPlacement(useSavedPlacement: false));

    private void RepairPlacement(bool useSavedPlacement)
    {
        if (placementService is null) return;
        EnsureHandle();
        var monitors = CurrentMonitors();
        if (monitors.Count == 0) return;
        var current = WindowPlacementService.GetWindowRectangle(handle);
        var candidate = useSavedPlacement ? placementService.Load() : WindowPlacementService.Capture(current, monitors);
        var resolved = WindowPlacementService.Resolve(candidate, monitors, Width, Math.Max(MinHeight, ActualHeight));
        applyingPlacement = true;
        try { WindowPlacementService.SetWindowRectangle(handle, resolved.Rect); }
        finally { applyingPlacement = false; }
        TryPersist(WindowPlacementService.Capture(resolved.Rect, monitors));
    }

    private void SchedulePlacementSave()
    {
        if (placementService is null || applyingPlacement || !IsLoaded || closing) return;
        placementSaveTimer?.Stop();
        placementSaveTimer?.Start();
    }
    private void SavePlacement()
    {
        if (placementService is null || applyingPlacement || handle == 0) return;
        try
        {
            var monitors = CurrentMonitors();
            if (monitors.Count > 0) TryPersist(WindowPlacementService.Capture(WindowPlacementService.GetWindowRectangle(handle), monitors));
        }
        catch (Exception exception) { viewModel?.ReportActionFeedback($"窗口位置未保存：{exception.Message}", true); }
    }
    private void TryPersist(WindowPlacement placement)
    {
        if (placementService?.TrySave(placement, out var error) == false && error is not null)
            viewModel?.ReportActionFeedback($"窗口位置未保存：{error.Message}", true);
    }

    private void ConfigureDrawer(FrameworkElement drawer)
    {
        if (handle == 0) return;
        var monitors = CurrentMonitors();
        if (monitors.Count == 0) return;
        var cardRect = WindowPlacementService.GetWindowRectangle(handle);
        var monitor = WindowPlacementService.MonitorFor(cardRect, monitors);
        int below = Math.Max(0, monitor.BottomPx - cardRect.BottomPx);
        int above = Math.Max(0, cardRect.TopPx - monitor.TopPx);
        double belowDip = Math.Max(0, below / monitor.ScaleY - 8);
        double aboveDip = Math.Max(0, above / monitor.ScaleY - 8);
        var popup = ReferenceEquals(drawer, SynonymsDrawer) ? SynonymsPopup : ConfusablesPopup;
        var relationDrawer = (RelationDrawer)drawer;
        var plan = RelationDrawerPlacementPlanner.Plan(relationDrawer.DesiredFiveRowHeight(), belowDip, aboveDip);
        popup.Placement = plan.Direction == RelationDrawerDirection.Below ? PlacementMode.Bottom : PlacementMode.Top;
        drawer.MaxHeight = plan.MaxHeight;
        relationDrawer.IsCompact = plan.UseCompactRows;
        relationDrawer.ScheduleViewportMeasure();
        SetPopupTopmost(relationDrawer, Topmost);
    }

    private void RefreshTopmost()
    {
        Topmost = uiSmokeTopmostOverride ?? WindowPlacementService.ShouldBeTopmost(SuppressTopmostForFullscreen,
            WindowPlacementService.IsForegroundFullscreen(handle));
        SetPopupTopmost(SynonymsDrawer, Topmost);
        SetPopupTopmost(ConfusablesDrawer, Topmost);
    }

    private void OnDrawerOpened(RelationDrawer drawer)
    {
        ConfigureDrawer(drawer);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, drawer.FocusSearch);
    }

    private void DismissDrawer(RelationDrawer drawer)
    {
        if (ReferenceEquals(drawer, SynonymsDrawer)) { viewModel?.Synonyms.Close(); SynonymsButton.Focus(); }
        else { viewModel?.Confusables.Close(); ConfusablesButton.Focus(); }
    }

    private void CloseDrawers()
    {
        viewModel?.Synonyms.Close();
        viewModel?.Confusables.Close();
    }

    private static void SetPopupTopmost(Visual drawer, bool topmost)
    {
        if (PresentationSource.FromVisual(drawer) is not HwndSource popupSource) return;
        NativePopup.SetWindowPos(popupSource.Handle, topmost ? new nint(-1) : new nint(-2), 0, 0, 0, 0,
            NativePopup.NoMove | NativePopup.NoSize | NativePopup.NoActivate);
    }

    private IReadOnlyList<MonitorWorkArea> CurrentMonitors()
    {
        var monitors = WindowPlacementService.CaptureCurrentMonitors();
        if (WorkAreaHeightLimitPx is not { } limit || limit <= 0) return monitors;
        return monitors.Select(monitor => monitor with { HeightPx = Math.Min(monitor.HeightPx, limit) }).ToArray();
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        lifetime.Cancel();
        placementSaveTimer?.Stop();
        fullscreenTimer?.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (source is not null) { source.RemoveHook(WindowProcedure); source = null; }
        if (shortcutService is not null) shortcutService.ActionInvoked -= OnShortcutActionInvoked;
        if (focusedShortcuts is not null)
        {
            focusedShortcuts.ActionInvoked -= OnShortcutActionInvoked;
            shortcutFaultConnection?.Dispose();
            focusedShortcuts.Dispose();
        }
        if (viewModel is not null) { viewModel.ActionRequested -= OnActionRequested; viewModel.Dispose(); }
        if (themeSettings is not null) { themeSettings.ThemeChanged -= OnThemeChanged; themeSettings.Feedback -= OnThemeFeedback; }
        lifetime.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private static class NativeFocus
    {
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint window);
    }

    private static class NativePopup
    {
        public const uint NoSize = 0x0001;
        public const uint NoMove = 0x0002;
        public const uint NoActivate = 0x0010;
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    }
}

public sealed class FullscreenSuppressionChangedEventArgs(bool enabled) : EventArgs
{
    public bool Enabled { get; } = enabled;
}
