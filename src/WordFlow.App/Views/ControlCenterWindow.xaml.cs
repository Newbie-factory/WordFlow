using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WordFlow.App.ViewModels;
using WordFlow.Application.Shortcuts;

namespace WordFlow.App.Views;

public partial class ControlCenterWindow : Window
{
    private readonly ControlCenterViewModel viewModel;

    public ControlCenterWindow(ControlCenterViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Theme.Feedback += ThemeFeedback;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
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
    private void RestoreShortcut_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is ShortcutBindingItemViewModel item) item.RestoreDefault(); }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PNG 图片|*.png", Title = "选择 PNG 背景" };
        if (dialog.ShowDialog(this) != true) return;
        try { await viewModel.Theme.ImportAsync(dialog.FileName, viewModel.Theme.Opacity); ThemeStatus.Text = $"已使用：{viewModel.Theme.DisplayName}"; }
        catch (Exception ex) { ThemeStatus.Text = $"PNG 无法使用：{ex.Message}"; }
    }
    private async void Reset_Click(object sender, RoutedEventArgs e) { await viewModel.Theme.ResetAsync(); ThemeStatus.Text = "已恢复默认背景"; }
    private void ThemeFeedback(string message) => Dispatcher.Invoke(() => ThemeStatus.Text = message);
    protected override void OnClosed(EventArgs e) { viewModel.Theme.Feedback -= ThemeFeedback; viewModel.Dispose(); base.OnClosed(e); }
}
