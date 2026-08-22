using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using WordFlow.Infrastructure.Data;

namespace WordFlow.App.Bootstrap;

public enum ThemeImageFormat
{
    Png,
    Jpeg,
}

public sealed record ImageTheme(string ImagePath, double Opacity)
{
    public bool IsDefault => string.IsNullOrWhiteSpace(ImagePath);
    public static ImageTheme Default => new(string.Empty, 1d);
}

public sealed class ImageThemeService
{
    public const string ImagePathKey = "theme.image_path";
    public const string LegacyImagePathKey = "theme.png_path";
    public const string OpacityKey = "theme.opacity";
    public const long MaxFileBytes = 20 * 1024 * 1024;
    public const int MaxImageDimension = 8192;
    public const long MaxImagePixels = 16L * 1024 * 1024;

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] JpegSignature = [255, 216, 255];
    private readonly AppPaths paths;
    private readonly SqliteAppSettingStore store;
    private readonly Action<string, string> copyFile;

    public ImageThemeService(AppPaths paths, SqliteAppSettingStore store)
        : this(paths, store, File.Copy)
    {
    }

    internal ImageThemeService(AppPaths paths, SqliteAppSettingStore store, Action<string, string> copyFile)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.copyFile = copyFile ?? throw new ArgumentNullException(nameof(copyFile));
    }

    public async Task<ImageTheme> RestoreAsync(CancellationToken ct = default)
    {
        try
        {
            var values = await store.GetManyAsync([ImagePathKey, LegacyImagePathKey, OpacityKey], ct).ConfigureAwait(false);
            bool restoringLegacy = values[ImagePathKey] is null && values[LegacyImagePathKey] is not null;
            string path = values[ImagePathKey] ?? values[LegacyImagePathKey] ?? string.Empty;
            double opacity = ParseOpacity(values[OpacityKey]);
            if (string.IsNullOrWhiteSpace(path)) return new ImageTheme(string.Empty, opacity);
            if (!IsSafeStoredPath(path) || !TryValidateImage(path, out _, out _)) return ImageTheme.Default;

            var theme = new ImageTheme(path, opacity);
            if (restoringLegacy) await SaveAsync(theme, ct).ConfigureAwait(false);
            return theme;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return ImageTheme.Default; }
    }

    public async Task<ImageTheme> ImportAsync(string sourcePath, double opacity, CancellationToken ct = default)
    {
        if (!TryValidateImage(sourcePath, out var format, out string error)) throw new ArgumentException(error, nameof(sourcePath));
        EnsureSafeSkinsDirectory();
        string destination = Path.Combine(paths.SkinsDirectory, $"theme-{Guid.NewGuid():N}{GetFileExtension(format)}");
        try
        {
            copyFile(sourcePath, destination);
            var theme = new ImageTheme(destination, ClampOpacity(opacity));
            await SaveAsync(theme, ct).ConfigureAwait(false);
            return theme;
        }
        catch
        {
            TryDeleteGeneratedDestination(destination);
            throw;
        }
    }

    public async Task SaveAsync(ImageTheme theme, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (!theme.IsDefault)
        {
            if (!IsSafeStoredPath(theme.ImagePath)) throw new ArgumentException("主题图片必须位于本地皮肤目录。", nameof(theme));
            if (!TryValidateImage(theme.ImagePath, out _, out string error)) throw new ArgumentException(error, nameof(theme));
        }
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [ImagePathKey] = theme.IsDefault ? string.Empty : Path.GetFullPath(theme.ImagePath),
            [OpacityKey] = ClampOpacity(theme.Opacity).ToString(CultureInfo.InvariantCulture),
        }, ct).ConfigureAwait(false);
    }

    public Task ResetAsync(CancellationToken ct = default) => SaveAsync(ImageTheme.Default, ct);

    public static double ClampOpacity(double opacity) => double.IsFinite(opacity) ? Math.Clamp(opacity, 0d, 1d) : 1d;

    public static bool TryValidateImage(string? path, out ThemeImageFormat format, out string error)
    {
        format = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !HasSupportedExtension(path))
        {
            error = "请选择 PNG 或 JPEG 图片。";
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { error = "图片不存在。"; return false; }
            if (info.Length is <= 8 or > MaxFileBytes) { error = "图片大小必须在 8 字节到 20 MB 之间。"; return false; }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            if (!TryReadFormat(stream, out format)) { error = "文件不是有效的 PNG 或 JPEG。"; return false; }
            stream.Position = 0;
            var headerDecoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            if (headerDecoder.Frames.Count == 0 || headerDecoder.Frames.Any(IsUnsafeImageSize))
            {
                error = "图片尺寸过大或不包含图像帧。";
                return false;
            }

            stream.Position = 0;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) { error = "图片不包含图像帧。"; return false; }
            return true;
        }
        catch (OutOfMemoryException)
        {
            error = "图片占用资源过多，无法读取。";
            return false;
        }
        catch (Exception)
        {
            error = "图片无法读取或已损坏。";
            return false;
        }
    }

    private bool IsSafeStoredPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        return IsManagedPathWithoutReparse(full, paths.RootDirectory, File.GetAttributes)
            && HasSupportedExtension(full);
    }

    internal static bool IsManagedPathWithoutReparse(string candidatePath, string trustedAppDataRoot, Func<string, FileAttributes> getAttributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedAppDataRoot);
        ArgumentNullException.ThrowIfNull(getAttributes);

        try
        {
            string root = Path.GetFullPath(trustedAppDataRoot).TrimEnd(Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(candidatePath);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;

            string relative = Path.GetRelativePath(root, full);
            string current = root;
            if (HasReparsePoint(current, getAttributes)) return false;
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (string.IsNullOrEmpty(segment) || segment == ".") continue;
                current = Path.Combine(current, segment);
                if (HasReparsePoint(current, getAttributes)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private void EnsureSafeSkinsDirectory()
    {
        if (!Directory.Exists(paths.RootDirectory) || !IsManagedPathWithoutReparse(paths.SkinsDirectory, paths.RootDirectory, File.GetAttributes))
            throw new InvalidOperationException("皮肤目录不安全，无法导入图片。");
        Directory.CreateDirectory(paths.SkinsDirectory);
        if (!IsManagedPathWithoutReparse(paths.SkinsDirectory, paths.RootDirectory, File.GetAttributes))
            throw new InvalidOperationException("皮肤目录不安全，无法导入图片。");
    }

    private void TryDeleteGeneratedDestination(string destination)
    {
        try
        {
            if (File.Exists(destination) && IsManagedPathWithoutReparse(destination, paths.RootDirectory, File.GetAttributes))
                File.Delete(destination);
        }
        catch { }
    }

    private static bool HasReparsePoint(string path, Func<string, FileAttributes> getAttributes)
    {
        try { return (getAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsUnsafeImageSize(BitmapFrame frame) =>
        frame.PixelWidth <= 0 || frame.PixelHeight <= 0
        || frame.PixelWidth > MaxImageDimension || frame.PixelHeight > MaxImageDimension
        || frame.PixelWidth > MaxImagePixels / frame.PixelHeight;

    private static bool TryReadFormat(Stream stream, out ThemeImageFormat format)
    {
        Span<byte> header = stackalloc byte[PngSignature.Length];
        int bytesRead = stream.Read(header);
        if (bytesRead >= PngSignature.Length && header.SequenceEqual(PngSignature))
        {
            format = ThemeImageFormat.Png;
            return true;
        }
        if (bytesRead >= JpegSignature.Length && header[..JpegSignature.Length].SequenceEqual(JpegSignature))
        {
            format = ThemeImageFormat.Jpeg;
            return true;
        }
        format = default;
        return false;
    }

    private static bool HasSupportedExtension(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFileExtension(ThemeImageFormat format) => format == ThemeImageFormat.Png ? ".png" : ".jpg";

    private static double ParseOpacity(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? ClampOpacity(parsed) : 1d;
}
