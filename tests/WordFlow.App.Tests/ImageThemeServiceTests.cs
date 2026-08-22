using System.Windows.Media;
using System.Windows.Media.Imaging;
using WordFlow.App.Bootstrap;
using WordFlow.Infrastructure.Data;

namespace WordFlow.App.Tests;

public sealed class ImageThemeServiceTests
{
    [Theory]
    [InlineData("skin.jpg")]
    [InlineData("skin.JPEG")]
    public void Validator_accepts_real_jpeg_case_insensitively(string name)
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            string path = WriteJpeg(root, name);

            Assert.True(ImageThemeService.TryValidateImage(path, out var format, out var error), error);
            Assert.Equal(ThemeImageFormat.Jpeg, format);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Validator_rejects_renamed_and_corrupt_images()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            string renamedFake = Path.Combine(root, "renamed.jpg");
            File.WriteAllText(renamedFake, "not an image");
            string corruptJpeg = Path.Combine(root, "corrupt.jpeg");
            File.WriteAllBytes(corruptJpeg, [0xFF, 0xD8, 0xFF, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);

            Assert.False(ImageThemeService.TryValidateImage(renamedFake, out _, out _));
            Assert.False(ImageThemeService.TryValidateImage(corruptJpeg, out _, out _));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Validator_detects_png_by_its_content_and_does_not_lock_the_source_file()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            string source = WritePng(root, "skin.jpg");

            Assert.True(ImageThemeService.TryValidateImage(source, out var format, out var error), error);
            Assert.Equal(ThemeImageFormat.Png, format);
            File.Move(source, Path.Combine(root, "moved.png"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ClampOpacity_preserves_zero_and_one()
    {
        Assert.Equal(0d, ImageThemeService.ClampOpacity(0d));
        Assert.Equal(1d, ImageThemeService.ClampOpacity(1d));
        Assert.Equal(0d, ImageThemeService.ClampOpacity(-1d));
        Assert.Equal(1d, ImageThemeService.ClampOpacity(2d));
        Assert.Equal(1d, ImageThemeService.ClampOpacity(double.NaN));
    }

    [Fact]
    public void Managed_path_guard_rejects_reparse_points_on_the_skins_root_and_its_ancestors()
    {
        string appRoot = Path.Combine(Path.GetTempPath(), "wordflow-app-root");
        string skinsRoot = Path.Combine(appRoot, "Skins");
        string imagePath = Path.Combine(skinsRoot, "theme.png");
        var normal = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase)
        {
            [appRoot] = FileAttributes.Directory,
            [skinsRoot] = FileAttributes.Directory,
            [imagePath] = FileAttributes.Normal,
        };

        Assert.True(ImageThemeService.IsManagedPathWithoutReparse(imagePath, appRoot, normal.GetValueOrDefault));
        Assert.False(ImageThemeService.IsManagedPathWithoutReparse(imagePath, appRoot,
            path => path == skinsRoot ? FileAttributes.Directory | FileAttributes.ReparsePoint : normal.GetValueOrDefault(path)));
        Assert.False(ImageThemeService.IsManagedPathWithoutReparse(imagePath, appRoot,
            path => path == appRoot ? FileAttributes.Directory | FileAttributes.ReparsePoint : normal.GetValueOrDefault(path)));
        Assert.False(ImageThemeService.IsManagedPathWithoutReparse(Path.Combine(Path.GetTempPath(), "outside.png"), appRoot, normal.GetValueOrDefault));
    }

    [Fact]
    public void Stored_image_guard_requires_the_skins_directory_boundary()
    {
        string appRoot = Path.Combine(Path.GetTempPath(), "wordflow-app-root");
        string skinsRoot = Path.Combine(appRoot, "Skins");
        string skinsImage = Path.Combine(skinsRoot, "theme.png");
        var normal = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase)
        {
            [appRoot] = FileAttributes.Directory,
            [skinsRoot] = FileAttributes.Directory,
            [skinsImage] = FileAttributes.Normal,
        };

        Assert.True(ImageThemeService.IsStoredImagePathSafe(skinsImage, appRoot, skinsRoot, normal.GetValueOrDefault));
        Assert.False(ImageThemeService.IsStoredImagePathSafe(Path.Combine(appRoot, "Data", "theme.png"), appRoot, skinsRoot, normal.GetValueOrDefault));
        Assert.False(ImageThemeService.IsStoredImagePathSafe(Path.Combine(appRoot, "Cache", "theme.png"), appRoot, skinsRoot, normal.GetValueOrDefault));
    }

    [Fact]
    public async Task Import_uses_detected_format_for_managed_copy_and_preserves_exact_opacity()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            var (paths, store) = await CreateStoreAsync(root);
            string source = WriteJpeg(root, "source.png");
            var service = new ImageThemeService(paths, store);

            var imported = await service.ImportAsync(source, 0d);
            var restored = await new ImageThemeService(paths, store).RestoreAsync();

            Assert.EndsWith(".jpg", imported.ImagePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(imported.ImagePath));
            Assert.Equal(0d, imported.Opacity);
            Assert.Equal(imported, restored);

            await service.SaveAsync(new ImageTheme(imported.ImagePath, 1d));
            Assert.Equal(1d, (await service.RestoreAsync()).Opacity);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Import_removes_its_generated_destination_after_a_partial_copy_failure()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            var (paths, store) = await CreateStoreAsync(root);
            string source = WritePng(root, "source.png");
            var service = new ImageThemeService(paths, store, (_, destination) =>
            {
                File.WriteAllText(destination, "partial copy");
                throw new IOException("simulated copy failure");
            });

            await Assert.ThrowsAsync<IOException>(() => service.ImportAsync(source, .7d));

            Assert.Empty(Directory.GetFiles(paths.SkinsDirectory));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Restore_migrates_valid_legacy_path_to_the_new_key_without_removing_legacy_data()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            var (paths, store) = await CreateStoreAsync(root);
            string legacyPath = WritePng(paths.SkinsDirectory, "theme-legacy.png");
            await store.SetManyAsync(new Dictionary<string, string>
            {
                [ImageThemeService.LegacyImagePathKey] = legacyPath,
                [ImageThemeService.OpacityKey] = "0",
            }, default);

            var restored = await new ImageThemeService(paths, store).RestoreAsync();
            var values = await store.GetManyAsync(
                [ImageThemeService.ImagePathKey, ImageThemeService.LegacyImagePathKey, ImageThemeService.OpacityKey], default);

            Assert.Equal(legacyPath, restored.ImagePath);
            Assert.Equal(0d, restored.Opacity);
            Assert.Equal(legacyPath, values[ImageThemeService.ImagePathKey]);
            Assert.Equal(legacyPath, values[ImageThemeService.LegacyImagePathKey]);
            Assert.Equal("0", values[ImageThemeService.OpacityKey]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Restore_prefers_the_new_path_key_over_the_legacy_path_key()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            var (paths, store) = await CreateStoreAsync(root);
            string newPath = WritePng(paths.SkinsDirectory, "theme-new.png");
            string legacyPath = WritePng(paths.SkinsDirectory, "theme-legacy.png");
            await store.SetManyAsync(new Dictionary<string, string>
            {
                [ImageThemeService.ImagePathKey] = newPath,
                [ImageThemeService.LegacyImagePathKey] = legacyPath,
            }, default);

            var restored = await new ImageThemeService(paths, store).RestoreAsync();

            Assert.Equal(newPath, restored.ImagePath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Data")]
    [InlineData("Cache")]
    public async Task Restore_rejects_a_valid_image_outside_the_skins_directory(string siblingDirectory)
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            var (paths, store) = await CreateStoreAsync(root);
            string imagePath = WritePng(Path.Combine(paths.RootDirectory, siblingDirectory), "theme.png");
            await store.SetManyAsync(new Dictionary<string, string>
            {
                [ImageThemeService.ImagePathKey] = imagePath,
            }, default);

            var restored = await new ImageThemeService(paths, store).RestoreAsync();

            Assert.True(restored.IsDefault);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Restore_rejects_a_reparse_point_that_targets_an_image_outside_the_skins_directory()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            var (paths, store) = await CreateStoreAsync(root);
            string outsideImage = WritePng(root, "outside.png");
            string linkedImage = Path.Combine(paths.SkinsDirectory, "theme-link.png");
            try { File.CreateSymbolicLink(linkedImage, outsideImage); }
            catch (IOException) { return; }
            await store.SetManyAsync(new Dictionary<string, string>
            {
                [ImageThemeService.ImagePathKey] = linkedImage,
            }, default);

            var restored = await new ImageThemeService(paths, store).RestoreAsync();

            Assert.True(restored.IsDefault);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Import_rejects_a_reparse_point_replacing_the_skins_directory_before_copying()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            var (paths, store) = await CreateStoreAsync(root);
            string source = WritePng(root, "source.png");
            string outsideDirectory = Path.Combine(root, "outside-skins");
            Directory.CreateDirectory(outsideDirectory);
            Directory.Delete(paths.SkinsDirectory);
            try { Directory.CreateSymbolicLink(paths.SkinsDirectory, outsideDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }

            await Assert.ThrowsAsync<InvalidOperationException>(() => new ImageThemeService(paths, store).ImportAsync(source, .7d));

            Assert.Empty(Directory.GetFiles(outsideDirectory));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Reset_clears_the_active_image_and_invalid_import_does_not_replace_it()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-").FullName;
        try
        {
            var (paths, store) = await CreateStoreAsync(root);
            var service = new ImageThemeService(paths, store);
            string source = WritePng(root, "source.png");
            var active = await service.ImportAsync(source, .7d);
            string invalid = Path.Combine(root, "bad.png");
            File.WriteAllText(invalid, "not png");

            await Assert.ThrowsAsync<ArgumentException>(() => service.ImportAsync(invalid, .7d));
            var unchanged = await service.RestoreAsync();
            await service.ResetAsync();
            var reset = await service.RestoreAsync();

            Assert.Equal(active.ImagePath, unchanged.ImagePath);
            Assert.True(reset.IsDefault);
            Assert.Equal(1d, reset.Opacity);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<(AppPaths Paths, SqliteAppSettingStore Store)> CreateStoreAsync(string root)
    {
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "bundled"));
        paths.Initialize();
        var factory = new SqliteConnectionFactory(paths.UserDatabasePath);
        await new MigrationRunner(factory).MigrateAsync(default);
        return (paths, new SqliteAppSettingStore(factory));
    }

    private static string WritePng(string root, string name)
    {
        string path = Path.Combine(root, name);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null)));
        encoder.Save(stream);
        return path;
    }

    private static string WriteJpeg(string root, string name)
    {
        string path = Path.Combine(root, name);
        using var stream = File.Create(path);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgr32, null)));
        encoder.Save(stream);
        return path;
    }
}
