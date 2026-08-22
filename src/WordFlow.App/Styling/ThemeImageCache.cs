using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WordFlow.App.Styling;

public sealed class ThemeImageCache
{
    private readonly object sync = new();
    private readonly Func<string, BitmapSource> decode;
    private string? cachedPath;
    private ImageBrush? cachedBrush;

    public ThemeImageCache() : this(DecodeOnLoad) { }

    internal ThemeImageCache(Func<string, BitmapSource> decode) =>
        this.decode = decode ?? throw new ArgumentNullException(nameof(decode));

    public ImageBrush Get(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = Path.GetFullPath(path);
        lock (sync)
        {
            if (cachedBrush is not null && string.Equals(cachedPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                return cachedBrush;

            BitmapSource bitmap = decode(normalizedPath);
            if (!bitmap.IsFrozen) bitmap.Freeze();
            var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
            brush.Freeze();
            cachedPath = normalizedPath;
            cachedBrush = brush;
            return brush;
        }
    }

    private static BitmapSource DecodeOnLoad(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
