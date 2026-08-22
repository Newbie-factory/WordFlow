using System.Windows;
using System.Windows.Controls;
using System.IO;
using WordFlow.App.ViewModels;
using Microsoft.Win32;

namespace WordFlow.App.Views;

public partial class ControlCenterWindow : Window
{
    private readonly ThemeSettingsViewModel themeSettings;
    private bool updating;

    public ControlCenterWindow(ThemeSettingsViewModel themeSettings)
    {
        this.themeSettings = themeSettings ?? throw new ArgumentNullException(nameof(themeSettings));
        InitializeComponent();
        themeSettings.ThemeChanged += ThemeChanged;
        themeSettings.Feedback += ThemeFeedback;
        Closed += (_, _) => themeSettings.ThemeChanged -= ThemeChanged;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        updating = true;
        OpacitySlider.Value = themeSettings.Opacity;
        updating = false;
        ThemeName.Text = themeSettings.DisplayName;
    }

    private void ThemeChanged(object? sender, EventArgs e) => Dispatcher.Invoke(ApplyTheme);
    private void ThemeFeedback(string message) => Dispatcher.Invoke(() => Status.Text = message);
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PNG 图片|*.png", Title = "选择 PNG 背景" };
        if (dialog.ShowDialog(this) != true) return;
        try { await themeSettings.ImportAsync(dialog.FileName, OpacitySlider.Value); Status.Text = $"已使用：{themeSettings.DisplayName}"; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { Status.Text = $"PNG 无法使用：{ex.Message}"; }
    }
    private async void Reset_Click(object sender, RoutedEventArgs e) { await themeSettings.ResetAsync(); Status.Text = "已恢复默认背景"; }
    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!updating) themeSettings.Opacity = e.NewValue; }
    protected override void OnClosed(EventArgs e) { themeSettings.ThemeChanged -= ThemeChanged; themeSettings.Feedback -= ThemeFeedback; base.OnClosed(e); }
}
