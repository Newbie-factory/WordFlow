using System.ComponentModel;
using System.IO;
using WordFlow.App.Bootstrap;

namespace WordFlow.App.ViewModels;

public sealed class ThemeSettingsViewModel : INotifyPropertyChanged
{
    private readonly ImageThemeService service;
    private readonly SynchronizationContext? uiContext;
    private ImageTheme current = ImageTheme.Default;
    public ThemeSettingsViewModel(ImageThemeService service) { this.service = service ?? throw new ArgumentNullException(nameof(service)); uiContext = SynchronizationContext.Current; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ThemeChanged;
    public event Action<string>? Feedback;
    public ImageTheme Current => current;
    public double Opacity { get => Current.Opacity; set { var next = new ImageTheme(Current.ImagePath, ImageThemeService.ClampOpacity(value)); if (next != Current) { SetCurrent(next); _ = PersistAsync(next); } } }
    public bool IsCustom => !Current.IsDefault;
    public string DisplayName => IsCustom ? Path.GetFileName(Current.ImagePath) : "默认浅色背景";
    public async Task RestoreAsync(CancellationToken ct = default) => await SetCurrentAsync(await service.RestoreAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
    public async Task ImportAsync(string sourcePath, double opacity, CancellationToken ct = default) => await SetCurrentAsync(await service.ImportAsync(sourcePath, opacity, ct).ConfigureAwait(false)).ConfigureAwait(false);
    public async Task ResetAsync(CancellationToken ct = default) { await service.ResetAsync(ct).ConfigureAwait(false); await SetCurrentAsync(ImageTheme.Default).ConfigureAwait(false); }
    private void SetCurrent(ImageTheme value) { current = value; PropertyChanged?.Invoke(this, new(nameof(Current))); PropertyChanged?.Invoke(this, new(nameof(Opacity))); PropertyChanged?.Invoke(this, new(nameof(IsCustom))); PropertyChanged?.Invoke(this, new(nameof(DisplayName))); ThemeChanged?.Invoke(this, EventArgs.Empty); }
    private Task SetCurrentAsync(ImageTheme value) => RunOnUiAsync(() => SetCurrent(value));
    private async Task PersistAsync(ImageTheme theme) { try { await service.SaveAsync(theme).ConfigureAwait(false); } catch (Exception exception) { await RunOnUiAsync(() => Feedback?.Invoke($"主题设置未保存：{exception.Message}")).ConfigureAwait(false); } }
    private Task RunOnUiAsync(Action action)
    {
        if (uiContext is null || SynchronizationContext.Current == uiContext) { action(); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        uiContext.Post(_ => { try { action(); completion.SetResult(); } catch (Exception exception) { completion.SetException(exception); } }, null);
        return completion.Task;
    }
}
