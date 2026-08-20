using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WordFlow.App.Bootstrap;

namespace WordFlow.App.Tests.Bootstrap;

public sealed class CorpusIntegrityVerifierTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"WordFlow.Corpus.{Guid.NewGuid():N}");

    [Fact]
    public async Task VerifyAsync_accepts_bundled_databases_matching_both_manifests()
    {
        (AppPaths paths, CorpusHashPins pins) = await CreateBundleAsync();

        await CorpusIntegrityVerifier.VerifyAsync(paths, pins, CancellationToken.None);
    }

    [Fact]
    public async Task VerifyAsync_fails_closed_when_vocabulary_database_hash_differs()
    {
        (AppPaths paths, CorpusHashPins pins) = await CreateBundleAsync();
        await File.AppendAllTextAsync(paths.VocabularyDatabasePath, "tampered");

        CorpusIntegrityException exception = await Assert.ThrowsAsync<CorpusIntegrityException>(
            () => CorpusIntegrityVerifier.VerifyAsync(paths, pins, CancellationToken.None));

        Assert.Contains("vocabulary.sqlite3", exception.Message, StringComparison.Ordinal);
        Assert.Contains("repair", exception.RepairInstructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_rejects_manifest_path_substitution()
    {
        (AppPaths paths, CorpusHashPins pins) = await CreateBundleAsync();
        string json = await File.ReadAllTextAsync(paths.VocabularyManifestPath);
        await File.WriteAllTextAsync(paths.VocabularyManifestPath,
            json.Replace("vocabulary.sqlite3", "../other.sqlite3", StringComparison.Ordinal));

        await Assert.ThrowsAsync<CorpusIntegrityException>(
            () => CorpusIntegrityVerifier.VerifyAsync(paths, pins, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_honors_cancellation_during_hashing()
    {
        (AppPaths paths, CorpusHashPins pins) = await CreateBundleAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CorpusIntegrityVerifier.VerifyAsync(paths, pins, cancellation.Token));
    }

    [Theory]
    [InlineData("vocabulary", "")]
    [InlineData("vocabulary", "ABCDEF")]
    [InlineData("vocabulary", "GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    [InlineData("relations", "")]
    [InlineData("relations", "ABCDEF")]
    [InlineData("relations", "GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public async Task VerifyAsync_classifies_invalid_application_hash_pins_as_fatal_configuration(
        string artifact,
        string invalidPin)
    {
        (AppPaths paths, CorpusHashPins validPins) = await CreateBundleAsync();
        var pins = artifact == "vocabulary"
            ? validPins with { VocabularySha256 = invalidPin }
            : validPins with { RelationsSha256 = invalidPin };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CorpusIntegrityVerifier.VerifyAsync(paths, pins, CancellationToken.None));

        Assert.Contains("application corpus hash pin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Trusted_hash_pins_match_the_actual_packaged_databases()
    {
        string data = Path.Combine(FindRepositoryRoot(), "data", "ielts");

        Assert.Equal(TrustedCorpusHashPins.VocabularySha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(data, "vocabulary.sqlite3")))));
        Assert.Equal(TrustedCorpusHashPins.RelationsSha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(data, "relations.sqlite3")))));
    }

    [Theory]
    [InlineData("vocabulary")]
    [InlineData("relations")]
    public async Task VerifyAsync_rejects_database_and_adjacent_manifest_tampered_together(string artifact)
    {
        (AppPaths paths, _) = await CreateBundleAsync();
        byte[] replacement = Encoding.UTF8.GetBytes($"attacker replacement {artifact}");
        if (artifact == "vocabulary")
        {
            await File.WriteAllBytesAsync(paths.VocabularyDatabasePath, replacement);
            await WriteVocabularyManifestAsync(paths, replacement);
        }
        else
        {
            await File.WriteAllBytesAsync(paths.RelationDatabasePath, replacement);
            await WriteRelationsManifestAsync(paths, replacement);
        }

        await Assert.ThrowsAsync<CorpusIntegrityException>(
            () => CorpusIntegrityVerifier.VerifyAsync(paths, CancellationToken.None));
    }

    [Theory]
    [InlineData("vocabulary", "{}")]
    [InlineData("vocabulary", "{\"schema_version\":1,\"artifacts\":[]}")]
    [InlineData("vocabulary", "{\"schema_version\":1,\"artifacts\":{\"sqlite\":{\"path\":1,\"bytes\":\"large\",\"sha256\":false}}}")]
    [InlineData("relations", "{}")]
    [InlineData("relations", "{\"schema_version\":2,\"artifacts\":[]}")]
    [InlineData("relations", "{\"schema_version\":2,\"artifacts\":{\"relations.sqlite3\":42}}")]
    public async Task VerifyAsync_translates_manifest_schema_and_type_errors_to_corpus_failure(string artifact, string json)
    {
        (AppPaths paths, CorpusHashPins pins) = await CreateBundleAsync();
        await File.WriteAllTextAsync(
            artifact == "vocabulary" ? paths.VocabularyManifestPath : paths.RelationManifestPath, json);

        CorpusIntegrityException exception = await Assert.ThrowsAsync<CorpusIntegrityException>(
            () => CorpusIntegrityVerifier.VerifyAsync(paths, pins, CancellationToken.None));

        Assert.Contains("manifest", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repair", exception.RepairInstructions, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(AppPaths Paths, CorpusHashPins Pins)> CreateBundleAsync()
    {
        string bundle = Path.Combine(root, "bundle");
        Directory.CreateDirectory(bundle);
        var paths = new AppPaths(Path.Combine(root, "local"), bundle);
        byte[] vocabulary = "trusted vocabulary"u8.ToArray();
        byte[] relations = "trusted relations"u8.ToArray();
        await File.WriteAllBytesAsync(paths.VocabularyDatabasePath, vocabulary);
        await File.WriteAllBytesAsync(paths.RelationDatabasePath, relations);
        await WriteVocabularyManifestAsync(paths, vocabulary);
        await WriteRelationsManifestAsync(paths, relations);
        return (paths, new CorpusHashPins(Hash(vocabulary), Hash(relations)));
    }

    private static Task WriteVocabularyManifestAsync(AppPaths paths, byte[] bytes) =>
        File.WriteAllTextAsync(paths.VocabularyManifestPath, JsonSerializer.Serialize(new
        {
            schema_version = 1,
            artifacts = new
            {
                sqlite = new { path = "vocabulary.sqlite3", bytes = bytes.Length, sha256 = Hash(bytes) },
            },
        }));

    private static Task WriteRelationsManifestAsync(AppPaths paths, byte[] bytes) =>
        File.WriteAllTextAsync(paths.RelationManifestPath, JsonSerializer.Serialize(new
        {
            schema_version = 2,
            artifacts = new Dictionary<string, string> { ["relations.sqlite3"] = Hash(bytes) },
        }));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WordFlow.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("WordFlow repository root not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
