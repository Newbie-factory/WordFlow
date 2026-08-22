using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using WordFlow.App.ViewModels;
using WordFlow.Application.Shortcuts;

namespace WordFlow.App.Views;

public partial class ControlCenterWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private readonly ControlCenterViewModel viewModel;
    private HwndSource? windowSource;
    public bool PermitClose { get; set; }

    public ControlCenterWindow(ControlCenterViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Theme.Feedback += ThemeFeedback;
        ApplyWindowStateVisuals();
    }

    public void OpenDashboard()
    {
        SelectPage("Dashboard");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource?.AddHook(WindowMessageHook);
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || DashboardPage is null) return;
        if (sender is RadioButton { Tag: string page }) SelectPage(page);
    }

    private async void SelectPage(string page)
    {
        DashboardPage.Visibility = page == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        VocabularyPage.Visibility = page == "Vocabulary" ? Visibility.Visible : Visibility.Collapsed;
        ConfusablesPage.Visibility = page == "Confusables" ? Visibility.Visible : Visibility.Collapsed;
        SlashedPage.Visibility = page == "Slashed" ? Visibility.Visible : Visibility.Collapsed;
        StatisticsPage.Visibility = page == "Statistics" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "Vocabulary" => ("词汇库", "搜索、筛选并查看本机 IELTS 词条"),
            "Confusables" => ("易混词", "按真实关系类型辨析形近、音近与同主题词"),
            "Slashed" => ("已斩词汇", "每页 100 条，保留历史并支持取消斩"),
            "Statistics" => ("学习统计", "从不可变学习事件读取真实进展"),
            "Settings" => ("设置", "学习、快捷键、悬浮卡、发音、数据与 FSRS"),
            _ => ("今日学习", "今天学多少、复习多少、进展如何"),
        };
        try
        {
            if (page == "Slashed") await viewModel.LoadSlashedPageAsync();
            else if (page is "Dashboard" or "Statistics") await viewModel.RefreshProgressAsync();
        }
        catch (Exception ex) { ShowPageError(ex); }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        if (WindowState == WindowState.Maximized) RestoreForDrag(e);
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void ToggleMaximizeRestore() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void RestoreForDrag(MouseButtonEventArgs e)
    {
        Point pointer = e.GetPosition(this);
        double horizontalRatio = ActualWidth <= 0 ? 0.5 : Math.Clamp(pointer.X / ActualWidth, 0.0, 1.0);
        Point screenPixels = PointToScreen(pointer);
        var source = PresentationSource.FromVisual(this);
        Point screen = source?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(screenPixels)
            : screenPixels;
        double restoredWidth = RestoreBounds.Width > 0 ? RestoreBounds.Width : Width;

        WindowState = WindowState.Normal;
        Left = screen.X - (restoredWidth * horizontalRatio);
        Top = Math.Max(SystemParameters.WorkArea.Top, screen.Y - 24);
    }

    private void Window_StateChanged(object sender, EventArgs e) => ApplyWindowStateVisuals();

    private void ApplyWindowStateVisuals()
    {
        if (RootCard is null || MaximizeGlyph is null || MaximizeRestoreButton is null) return;
        bool maximized = WindowState == WindowState.Maximized;
        RootCard.Margin = maximized ? new Thickness(0) : new Thickness(10);
        RootCard.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(18);
        MaximizeGlyph.Data = Geometry.Parse(maximized
            ? "M 3,1 L 10,1 L 10,8 L 8,8 M 1,3 L 8,3 L 8,10 L 1,10 Z"
            : "M 1,1 L 10,1 L 10,10 L 1,10 Z");
        string actionName = maximized ? "还原控制中心" : "最大化控制中心";
        AutomationProperties.SetName(MaximizeRestoreButton, actionName);
        ToolTipService.SetToolTip(MaximizeRestoreButton, maximized ? "还原" : "最大化");
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmNcHitTest || WindowState == WindowState.Maximized) return IntPtr.Zero;
        if (!GetWindowRect(hwnd, out NativeRect bounds)) return IntPtr.Zero;

        int screenX = unchecked((short)(long)lParam);
        int screenY = unchecked((short)((long)lParam >> 16));
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double borderPixels = 8 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
        int result = WindowResizeHitTest.Resolve(
            screenX - bounds.Left,
            screenY - bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            borderPixels);
        if (result == WindowResizeHitTest.Client) return IntPtr.Zero;

        handled = true;
        return new IntPtr(result);
    }

    private async void SaveDaily_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.SaveDailyPlanAsync(); DailyStatus.Text = "每日目标已保存，下一张学习卡将使用新目标。"; }
        catch (Exception ex) { DailyStatus.Text = $"保存失败：{ex.Message}"; }
    }

    private async void RefreshProgress_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.RefreshProgressAsync(); DailyStatus.Text = "今日进度已刷新。"; }
        catch (Exception ex) { DailyStatus.Text = $"读取失败：{ex.Message}"; }
    }

    private async void VocabularySearch_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.SearchVocabularyAsync(); }
        catch (Exception ex) { ShowPageError(ex); }
    }

    private async void ConfusableSearch_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.SearchConfusablesAsync(); }
        catch (Exception ex) { ShowPageError(ex); }
    }

    private async void LoadSlashed_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.LoadSlashedPageAsync(); }
        catch (Exception ex) { ShowPageError(ex); }
    }

    private async void SlashedPrevious_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.ChangeSlashedPageAsync(-1); }
        catch (Exception ex) { ShowPageError(ex); }
    }

    private async void SlashedNext_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.ChangeSlashedPageAsync(1); }
        catch (Exception ex) { ShowPageError(ex); }
    }

    private async void RestoreSlashed_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.RestoreSelectedSlashedAsync(); }
        catch (Exception ex) { ShowPageError(ex); }
    }

    private async void SavePronunciation_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.Pronunciation.SaveAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "发音设置保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Shortcut_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not ShortcutBindingItemViewModel item) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        item.Record(WpfShortcutChordCapture.FromKey(key, Keyboard.Modifiers));
        e.Handled = true;
    }

    private void RestoreShortcut_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ShortcutBindingItemViewModel item) item.RestoreDefault();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PNG 图片|*.png", Title = "选择 PNG 背景" };
        if (dialog.ShowDialog(this) != true) return;
        try { await viewModel.Theme.ImportAsync(dialog.FileName, viewModel.Theme.Opacity); ThemeStatus.Text = $"已使用：{viewModel.Theme.DisplayName}"; }
        catch (Exception ex) { ThemeStatus.Text = $"PNG 无法使用：{ex.Message}"; }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.Theme.ResetAsync(); ThemeStatus.Text = "已恢复默认背景"; }
        catch (Exception ex) { ThemeStatus.Text = $"恢复失败：{ex.Message}"; }
    }

    private void ThemeFeedback(string message) => Dispatcher.InvokeAsync(() => ThemeStatus.Text = message);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!PermitClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        viewModel.Theme.Feedback -= ThemeFeedback;
        if (windowSource is not null)
        {
            windowSource.RemoveHook(WindowMessageHook);
            windowSource = null;
        }
        base.OnClosing(e);
    }

    private void ShowPageError(Exception exception) =>
        MessageBox.Show(this, exception.Message, "控制中心操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal static class WindowResizeHitTest
{
    internal const int Client = 1;
    private const int Left = 10;
    private const int Right = 11;
    private const int Top = 12;
    private const int TopLeft = 13;
    private const int TopRight = 14;
    private const int Bottom = 15;
    private const int BottomLeft = 16;
    private const int BottomRight = 17;

    internal static int Resolve(double x, double y, double width, double height, double borderThickness)
    {
        bool left = x >= 0 && x < borderThickness;
        bool right = x <= width && x > width - borderThickness;
        bool top = y >= 0 && y < borderThickness;
        bool bottom = y <= height && y > height - borderThickness;

        if (top && left) return TopLeft;
        if (top && right) return TopRight;
        if (bottom && left) return BottomLeft;
        if (bottom && right) return BottomRight;
        if (left) return Left;
        if (right) return Right;
        if (top) return Top;
        if (bottom) return Bottom;
        return Client;
    }
}
