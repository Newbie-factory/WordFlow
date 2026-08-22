using System.ComponentModel;
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
    public bool PermitClose { get; set; }

    public ControlCenterWindow(ControlCenterViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Theme.Feedback += ThemeFeedback;
    }

    public void OpenDashboard()
    {
        SelectPage("Dashboard");
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
        base.OnClosing(e);
    }

    private void ShowPageError(Exception exception) =>
        MessageBox.Show(this, exception.Message, "控制中心操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
}
