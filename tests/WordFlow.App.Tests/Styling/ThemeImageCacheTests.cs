using System.Windows.Media;
using System.Windows.Media.Imaging;
using WordFlow.App.Styling;

namespace WordFlow.App.Tests.Styling;

public sealed class ThemeImageCacheTests
{
    [Fact]
    public void Get_normalizes_path_decodes_once_and_returns_one_frozen_brush()
    {
        int decodes = 0;
        var cache = new ThemeImageCache(_ =>
        {
            decodes++;
            var bitmap = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
            bitmap.Freeze();
            return bitmap;
        });
        string path = Path.Combine(Path.GetTempPath(), "theme-cache", "skin.png");

        var first = cache.Get(path);
        var second = cache.Get(Path.Combine(Path.GetDirectoryName(path)!, ".", "skin.png"));

        Assert.Same(first, second);
        Assert.True(first.IsFrozen);
        Assert.True(((BitmapSource)first.ImageSource).IsFrozen);
        Assert.Equal(Stretch.Fill, first.Stretch);
        Assert.Equal(1, decodes);
    }

    [Fact]
    public void Get_replaces_the_cached_brush_when_normalized_path_changes()
    {
        int decodes = 0;
        var cache = new ThemeImageCache(_ =>
        {
            decodes++;
            var bitmap = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
            bitmap.Freeze();
            return bitmap;
        });

        var first = cache.Get(Path.Combine(Path.GetTempPath(), "one.png"));
        var second = cache.Get(Path.Combine(Path.GetTempPath(), "two.png"));

        Assert.NotSame(first, second);
        Assert.Equal(2, decodes);
    }
}
