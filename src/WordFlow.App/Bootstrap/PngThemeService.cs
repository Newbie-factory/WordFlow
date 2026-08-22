using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using WordFlow.Infrastructure.Data;

namespace WordFlow.App.Bootstrap;

public sealed record PngTheme(string ImagePath, double Opacity)
{
    public bool IsDefault => string.IsNullOrWhiteSpace(ImagePath);
    public static PngTheme Default => new(string.Empty, 1d);
}

public sealed class PngThemeService
{
    public const string ImagePathKey = "theme.png_path";
    public const string OpacityKey = "theme.opacity";
    public const long MaxFileBytes = 20 * 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly AppPaths paths;
    private readonly SqliteAppSettingStore store;

    public PngThemeService(AppPaths paths, SqliteAppSettingStore store)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<PngTheme> RestoreAsync(CancellationToken ct = default)
    {
        try
        {
            var values = await store.GetManyAsync([ImagePathKey, OpacityKey], ct).ConfigureAwait(false);
            string path = values[ImagePathKey] ?? string.Empty;
            double opacity = ParseOpacity(values[OpacityKey]);
            if (string.IsNullOrWhiteSpace(path)) return new PngTheme(string.Empty, opacity);
            if (!IsSafeStoredPath(path) || !TryValidatePng(path, out _)) return PngTheme.Default;
            return new(path, opacity);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return PngTheme.Default; }
    }

    public async Task<PngTheme> ImportAsync(string sourcePath, double opacity, CancellationToken ct = default)
    {
        if (!TryValidatePng(sourcePath, out string error)) throw new ArgumentException(error, nameof(sourcePath));
        Directory.CreateDirectory(paths.SkinsDirectory);
        string destination = Path.Combine(paths.SkinsDirectory, $"theme-{Guid.NewGuid():N}.png");
        File.Copy(sourcePath, destination, overwrite: false);
        try
        {
            var theme = new PngTheme(destination, ClampOpacity(opacity));
            await SaveAsync(theme, ct).ConfigureAwait(false);
            return theme;
        }
        catch
        {
            try { File.Delete(destination); } catch { }
            throw;
        }
    }

    public async Task SaveAsync(PngTheme theme, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (!theme.IsDefault)
        {
            if (!IsSafeStoredPath(theme.ImagePath)) throw new ArgumentException("主题图片必须位于本地皮肤目录。", nameof(theme));
            if (!TryValidatePng(theme.ImagePath, out string error)) throw new ArgumentException(error, nameof(theme));
        }
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [ImagePathKey] = theme.IsDefault ? string.Empty : Path.GetFullPath(theme.ImagePath),
            [OpacityKey] = ClampOpacity(theme.Opacity).ToString(CultureInfo.InvariantCulture),
        }, ct).ConfigureAwait(false);
    }

    public Task ResetAsync(CancellationToken ct = default) => SaveAsync(PngTheme.Default, ct);

    public static double ClampOpacity(double opacity) => double.IsFinite(opacity) ? Math.Clamp(opacity, .15d, 1d) : 1d;

    internal static bool TryValidatePng(string? path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
        { error = "请选择 PNG 图片。"; return false; }
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { error = "图片不存在。"; return false; }
            if (info.Length is <= 8 or > MaxFileBytes) { error = "图片大小必须在 8 字节到 20 MB 之间。"; return false; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            Span<byte> signature = stackalloc byte[8];
            if (stream.Read(signature) != 8 || !signature.SequenceEqual(PngSignature)) { error = "文件不是有效的 PNG。"; return false; }
            stream.Position = 0;
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0 || decoder.Frames.Any(frame => frame.PixelWidth > 8192 || frame.PixelHeight > 8192))
            { error = "PNG 尺寸过大或不包含图像帧。"; return false; }
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { error = "PNG 无法读取或已损坏。"; return false; }
    }

    private bool IsSafeStoredPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        string root = Path.GetFullPath(paths.SkinsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase);
    }

    private static double ParseOpacity(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? ClampOpacity(parsed) : 1d;
}
