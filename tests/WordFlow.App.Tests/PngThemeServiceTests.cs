using System.Windows.Media;
using System.Windows.Media.Imaging;
using WordFlow.App.Bootstrap;
using WordFlow.Infrastructure.Data;

namespace WordFlow.App.Tests;

public sealed class PngThemeServiceTests
{
    [Fact]
    public void ClampOpacity_StaysWithinReadableRange()
    {
        Assert.Equal(.15d, PngThemeService.ClampOpacity(-1));
        Assert.Equal(1d, PngThemeService.ClampOpacity(2));
        Assert.Equal(1d, PngThemeService.ClampOpacity(double.NaN));
    }

    [Fact]
    public void Validator_RejectsNonPngAndMalformedFiles()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            string text = Path.Combine(root, "image.png");
            File.WriteAllText(text, "not a png");
            Assert.False(PngThemeService.TryValidatePng(text, out _));
            Assert.False(PngThemeService.TryValidatePng(Path.Combine(root, "missing.png"), out _));
            string jpg = Path.Combine(root, "image.jpg");
            File.Copy(text, jpg);
            Assert.False(PngThemeService.TryValidatePng(jpg, out _));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Validator_AcceptsSmallPng()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            string path = Path.Combine(root, "image.PNG");
            using (var stream = File.Create(path))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null)));
                encoder.Save(stream);
            }
            Assert.True(PngThemeService.TryValidatePng(path, out var error), error);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Import_persists_a_copy_and_restores_after_new_service_instance()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "bundled"));
            paths.Initialize();
            var factory = new SqliteConnectionFactory(paths.UserDatabasePath);
            await new MigrationRunner(factory).MigrateAsync(default);
            string source = Path.Combine(root, "source.png");
            WritePng(source);

            var imported = await new PngThemeService(paths, new SqliteAppSettingStore(factory)).ImportAsync(source, 0.05d);
            var restored = await new PngThemeService(paths, new SqliteAppSettingStore(factory)).RestoreAsync();

            Assert.False(imported.IsDefault);
            Assert.Equal(0.15d, imported.Opacity);
            Assert.True(File.Exists(imported.ImagePath));
            Assert.Equal(imported.ImagePath, restored.ImagePath);
            Assert.Equal(0.15d, restored.Opacity);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Reset_clears_the_active_image_and_invalid_import_does_not_replace_it()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "bundled"));
            paths.Initialize();
            var factory = new SqliteConnectionFactory(paths.UserDatabasePath);
            await new MigrationRunner(factory).MigrateAsync(default);
            var service = new PngThemeService(paths, new SqliteAppSettingStore(factory));
            string source = Path.Combine(root, "source.png");
            WritePng(source);
            var active = await service.ImportAsync(source, 0.7d);
            string invalid = Path.Combine(root, "bad.png");
            File.WriteAllText(invalid, "not png");

            await Assert.ThrowsAsync<ArgumentException>(() => service.ImportAsync(invalid, 0.7d));
            var unchanged = await service.RestoreAsync();
            await service.ResetAsync();
            var reset = await service.RestoreAsync();

            Assert.Equal(active.ImagePath, unchanged.ImagePath);
            Assert.True(reset.IsDefault);
            Assert.Equal(1d, reset.Opacity);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WritePng(string path)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null)));
        encoder.Save(stream);
        stream.Flush();
    }
}
