using System.Security.Cryptography;
using System.Text.Json;
using WordFlow.App.Bootstrap;

namespace WordFlow.App.Tests.Bootstrap;

public sealed class CorpusIntegrityVerifierTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"WordFlow.Corpus.{Guid.NewGuid():N}");

    [Fact]
    public async Task VerifyAsync_accepts_bundled_databases_matching_both_manifests()
    {
        AppPaths paths = await CreateBundleAsync();

        await CorpusIntegrityVerifier.VerifyAsync(paths, CancellationToken.None);
    }

    [Fact]
    public async Task VerifyAsync_fails_closed_when_vocabulary_database_hash_differs()
    {
        AppPaths paths = await CreateBundleAsync();
        await File.AppendAllTextAsync(paths.VocabularyDatabasePath, "tampered");

        CorpusIntegrityException exception = await Assert.ThrowsAsync<CorpusIntegrityException>(
            () => CorpusIntegrityVerifier.VerifyAsync(paths, CancellationToken.None));

        Assert.Contains("vocabulary.sqlite3", exception.Message, StringComparison.Ordinal);
        Assert.Contains("repair", exception.RepairInstructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_rejects_manifest_path_substitution()
    {
        AppPaths paths = await CreateBundleAsync();
        string json = await File.ReadAllTextAsync(paths.VocabularyManifestPath);
        await File.WriteAllTextAsync(paths.VocabularyManifestPath,
            json.Replace("vocabulary.sqlite3", "../other.sqlite3", StringComparison.Ordinal));

        await Assert.ThrowsAsync<CorpusIntegrityException>(
            () => CorpusIntegrityVerifier.VerifyAsync(paths, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_honors_cancellation_during_hashing()
    {
        AppPaths paths = await CreateBundleAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CorpusIntegrityVerifier.VerifyAsync(paths, cancellation.Token));
    }

    private async Task<AppPaths> CreateBundleAsync()
    {
        string bundle = Path.Combine(root, "bundle");
        Directory.CreateDirectory(bundle);
        var paths = new AppPaths(Path.Combine(root, "local"), bundle);
        byte[] vocabulary = "trusted vocabulary"u8.ToArray();
        byte[] relations = "trusted relations"u8.ToArray();
        await File.WriteAllBytesAsync(paths.VocabularyDatabasePath, vocabulary);
        await File.WriteAllBytesAsync(paths.RelationDatabasePath, relations);
        await File.WriteAllTextAsync(paths.VocabularyManifestPath, JsonSerializer.Serialize(new
        {
            artifacts = new
            {
                sqlite = new { path = "vocabulary.sqlite3", bytes = vocabulary.Length, sha256 = Hash(vocabulary) },
            },
        }));
        await File.WriteAllTextAsync(paths.RelationManifestPath, JsonSerializer.Serialize(new
        {
            artifacts = new Dictionary<string, string>
            {
                ["relations.sqlite3"] = Hash(relations),
            },
        }));
        return paths;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
