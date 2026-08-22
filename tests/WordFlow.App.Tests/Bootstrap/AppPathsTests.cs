using WordFlow.App.Bootstrap;

namespace WordFlow.App.Tests.Bootstrap;

public sealed class AppPathsTests : IDisposable
{
    private readonly string temporaryRoot = Path.Combine(Path.GetTempPath(), $"WordFlow.AppPaths.{Guid.NewGuid():N}");

    [Fact]
    public void Initialize_creates_exact_user_scoped_directory_layout()
    {
        var paths = new AppPaths(temporaryRoot, Path.Combine(temporaryRoot, "bundle"));

        paths.Initialize();

        Assert.Equal(Path.GetFullPath(Path.Combine(temporaryRoot, "WordFlow")), paths.RootDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Backups"), paths.BackupsDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Skins"), paths.SkinsDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Cache"), paths.CacheDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Logs"), paths.LogsDirectory);
        Assert.All(new[] { paths.RootDirectory, paths.DataDirectory, paths.BackupsDirectory,
            paths.SkinsDirectory, paths.CacheDirectory, paths.LogsDirectory },
            directory => Assert.True(Directory.Exists(directory), directory));
    }

    [Fact]
    public void Default_paths_are_below_local_application_data()
    {
        AppPaths paths = AppPaths.ForCurrentUser();

        string expected = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WordFlow"));
        Assert.Equal(expected, paths.RootDirectory);
        Assert.StartsWith(expected + Path.DirectorySeparatorChar, paths.UserDatabasePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verification_override_requires_guard_token_and_returns_the_isolated_root()
    {
        string defaultRoot = Path.Combine(temporaryRoot, "default");
        string isolatedRoot = Path.Combine(temporaryRoot, "isolated");

        Assert.Equal(
            Path.GetFullPath(defaultRoot),
            AppPaths.ResolveLocalApplicationData(defaultRoot, null, null));
        Assert.Throws<InvalidOperationException>(() =>
            AppPaths.ResolveLocalApplicationData(defaultRoot, isolatedRoot, null));
        Assert.Equal(
            Path.GetFullPath(isolatedRoot),
            AppPaths.ResolveLocalApplicationData(defaultRoot, isolatedRoot, "isolated"));
    }

    [Fact]
    public void Verification_import_requires_the_guard_token_and_a_file_below_temp()
    {
        Directory.CreateDirectory(temporaryRoot);
        string image = Path.Combine(temporaryRoot, "skin.png");
        File.WriteAllBytes(image, [1]);

        Assert.Null(AppPaths.ResolveVerificationImportPath(null, null, null));
        Assert.Throws<InvalidOperationException>(() =>
            AppPaths.ResolveVerificationImportPath(image, null, temporaryRoot));
        Assert.Throws<InvalidOperationException>(() =>
            AppPaths.ResolveVerificationImportPath(image, "isolated", null));
        Assert.Equal(
            Path.GetFullPath(image),
            AppPaths.ResolveVerificationImportPath(image, "isolated", temporaryRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
    }
}
