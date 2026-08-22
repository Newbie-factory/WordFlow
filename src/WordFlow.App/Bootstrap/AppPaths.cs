using System.IO;

namespace WordFlow.App.Bootstrap;

public sealed class AppPaths
{
    internal const string VerificationModeEnvironmentVariable = "WORDFLOW_VERIFICATION_MODE";
    internal const string VerificationRootEnvironmentVariable = "WORDFLOW_VERIFICATION_LOCAL_APP_DATA";
    internal const string VerificationImportEnvironmentVariable = "WORDFLOW_VERIFICATION_IMPORT";

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
        ResolveLocalApplicationData(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable(VerificationRootEnvironmentVariable),
            Environment.GetEnvironmentVariable(VerificationModeEnvironmentVariable)),
        Path.Combine(AppContext.BaseDirectory, "Data", "ielts"));

    internal static string ResolveLocalApplicationData(
        string defaultDirectory,
        string? verificationDirectory,
        string? verificationMode)
    {
        if (string.IsNullOrWhiteSpace(verificationDirectory)) return Path.GetFullPath(defaultDirectory);
        if (!string.Equals(verificationMode, "isolated", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{VerificationRootEnvironmentVariable} requires {VerificationModeEnvironmentVariable}=isolated.");
        if (!Path.IsPathFullyQualified(verificationDirectory))
            throw new InvalidOperationException("The verification data root must be an absolute path.");

        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(verificationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!candidate.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The verification data root must be below the system temporary directory.");
        return candidate;
    }

    internal static string? ResolveVerificationImportPath(
        string? importPath,
        string? verificationMode,
        string? verificationDirectory)
    {
        if (string.IsNullOrWhiteSpace(importPath)) return null;
        if (!string.Equals(verificationMode, "isolated", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{VerificationImportEnvironmentVariable} requires {VerificationModeEnvironmentVariable}=isolated.");
        if (string.IsNullOrWhiteSpace(verificationDirectory))
            throw new InvalidOperationException(
                $"{VerificationImportEnvironmentVariable} requires {VerificationRootEnvironmentVariable}.");
        _ = ResolveLocalApplicationData(Path.GetTempPath(), verificationDirectory, verificationMode);
        if (!Path.IsPathFullyQualified(importPath))
            throw new InvalidOperationException("The verification import path must be absolute.");

        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(importPath);
        if (!candidate.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The verification import path must be below the system temporary directory.");
        return candidate;
    }

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
