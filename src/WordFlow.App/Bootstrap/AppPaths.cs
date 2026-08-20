using System.IO;

namespace WordFlow.App.Bootstrap;

public sealed class AppPaths
{
    public AppPaths(string localApplicationDataDirectory, string bundledDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(localApplicationDataDirectory))
            throw new ArgumentException("A local application-data directory is required.", nameof(localApplicationDataDirectory));
        if (string.IsNullOrWhiteSpace(bundledDataDirectory))
            throw new ArgumentException("A bundled-data directory is required.", nameof(bundledDataDirectory));

        RootDirectory = Path.GetFullPath(Path.Combine(localApplicationDataDirectory, "WordFlow"));
        BundledDataDirectory = Path.GetFullPath(bundledDataDirectory);
        DataDirectory = Path.Combine(RootDirectory, "Data");
        BackupsDirectory = Path.Combine(RootDirectory, "Backups");
        SkinsDirectory = Path.Combine(RootDirectory, "Skins");
        CacheDirectory = Path.Combine(RootDirectory, "Cache");
        LogsDirectory = Path.Combine(RootDirectory, "Logs");
        UserDatabasePath = Path.Combine(DataDirectory, "wordflow.sqlite3");
        VocabularyDatabasePath = Path.Combine(BundledDataDirectory, "vocabulary.sqlite3");
        RelationDatabasePath = Path.Combine(BundledDataDirectory, "relations.sqlite3");
        VocabularyManifestPath = Path.Combine(BundledDataDirectory, "manifest.json");
        RelationManifestPath = Path.Combine(BundledDataDirectory, "relations-manifest.json");
    }

    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string BackupsDirectory { get; }
    public string SkinsDirectory { get; }
    public string CacheDirectory { get; }
    public string LogsDirectory { get; }
    public string UserDatabasePath { get; }
    public string BundledDataDirectory { get; }
    public string VocabularyDatabasePath { get; }
    public string RelationDatabasePath { get; }
    public string VocabularyManifestPath { get; }
    public string RelationManifestPath { get; }

    public static AppPaths ForCurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.Combine(AppContext.BaseDirectory, "Data", "ielts"));

    public void Initialize()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(SkinsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
