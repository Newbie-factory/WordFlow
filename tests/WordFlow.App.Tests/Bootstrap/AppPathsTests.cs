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

    public void Dispose()
    {
        if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
    }
}
