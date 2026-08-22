using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using WordFlow.App.Bootstrap;

namespace WordFlow.App.ViewModels;

public sealed class ThemeSettingsViewModel(PngThemeService service) : INotifyPropertyChanged
{
    private PngTheme current = PngTheme.Default;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ThemeChanged;
    public PngTheme Current { get => current; private set { current = value; PropertyChanged?.Invoke(this, new(nameof(Current))); ThemeChanged?.Invoke(this, EventArgs.Empty); } }
    public double Opacity { get => Current.Opacity; set { var next = new PngTheme(Current.ImagePath, PngThemeService.ClampOpacity(value)); if (next != Current) Current = next; } }
    public bool IsCustom => !Current.IsDefault;
    public string DisplayName => IsCustom ? Path.GetFileName(Current.ImagePath) : "默认浅色背景";

    public async Task RestoreAsync(CancellationToken ct = default) => Current = await service.RestoreAsync(ct).ConfigureAwait(false);
    public async Task ImportAsync(string sourcePath, double opacity, CancellationToken ct = default) => Current = await service.ImportAsync(sourcePath, opacity, ct).ConfigureAwait(false);
    public async Task ResetAsync(CancellationToken ct = default) { await service.ResetAsync(ct).ConfigureAwait(false); Current = PngTheme.Default; }
}
