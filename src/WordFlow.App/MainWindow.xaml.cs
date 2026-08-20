using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WordFlow.App.ViewModels;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;

namespace WordFlow.App;

public partial class MainWindow : Window
{
    private readonly IShortcutService? shortcutService;
    private readonly FocusedShortcutBindingBridge? focusedShortcuts;
    private HwndSource? source;

    public MainWindow() => InitializeComponent();

    public MainWindow(IShortcutService shortcutService)
    {
        this.shortcutService = shortcutService ?? throw new ArgumentNullException(nameof(shortcutService));
        focusedShortcuts = new FocusedShortcutBindingBridge(shortcutService);
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += OnClosed;
        shortcutService.ActionInvoked += OnShortcutActionInvoked;
        focusedShortcuts.ActionInvoked += OnShortcutActionInvoked;
    }

    public event EventHandler<ShortcutAction>? ShortcutActionInvoked;

    public void SetPaused(bool paused) => StatusText.Text = paused ? "Paused" : "Offline corpus verified";

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
        return 0;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (focusedShortcuts is null) return;
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        args.Handled = focusedShortcuts.TryInvoke(WpfShortcutChordCapture.FromKey(key, Keyboard.Modifiers));
    }

    private void OnShortcutActionInvoked(object? sender, ShortcutAction action) =>
        ShortcutActionInvoked?.Invoke(this, action);

    private void OnClosed(object? sender, EventArgs args)
    {
        if (source is not null)
        {
            source.RemoveHook(WindowProcedure);
            source = null;
        }
        if (shortcutService is not null) shortcutService.ActionInvoked -= OnShortcutActionInvoked;
        if (focusedShortcuts is not null)
        {
            focusedShortcuts.ActionInvoked -= OnShortcutActionInvoked;
            focusedShortcuts.Dispose();
        }
    }
}
