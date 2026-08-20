using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WordFlow.App.Bootstrap;

public sealed class CorpusIntegrityException : Exception
{
    public CorpusIntegrityException(string message, Exception? innerException = null) : base(message, innerException) { }

    public string RepairInstructions =>
        "Repair WordFlow from its trusted installer or reinstall the application. Your learning data in LocalAppData is not removed.";
}

public static class CorpusIntegrityVerifier
{
    public static async Task VerifyAsync(AppPaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await VerifyVocabularyAsync(paths, cancellationToken).ConfigureAwait(false);
            await VerifyRelationsAsync(paths, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (CorpusIntegrityException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            throw new CorpusIntegrityException("The bundled WordFlow corpus could not be verified.", exception);
        }
    }

    private static async Task VerifyVocabularyAsync(AppPaths paths, CancellationToken cancellationToken)
    {
        using JsonDocument manifest = await ReadManifestAsync(paths.VocabularyManifestPath, cancellationToken).ConfigureAwait(false);
        JsonElement artifact = manifest.RootElement.GetProperty("artifacts").GetProperty("sqlite");
        string declaredPath = artifact.GetProperty("path").GetString() ?? "";
        long declaredBytes = artifact.GetProperty("bytes").GetInt64();
        string declaredHash = artifact.GetProperty("sha256").GetString() ?? "";
        if (!string.Equals(declaredPath, "vocabulary.sqlite3", StringComparison.Ordinal))
            throw new CorpusIntegrityException("The vocabulary manifest contains an invalid database path.");
        await VerifyFileAsync(paths.VocabularyDatabasePath, declaredBytes, declaredHash, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyRelationsAsync(AppPaths paths, CancellationToken cancellationToken)
    {
        using JsonDocument manifest = await ReadManifestAsync(paths.RelationManifestPath, cancellationToken).ConfigureAwait(false);
        string declaredHash = manifest.RootElement.GetProperty("artifacts").GetProperty("relations.sqlite3").GetString() ?? "";
        await VerifyFileAsync(paths.RelationDatabasePath, expectedLength: null, declaredHash, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyFileAsync(string path, long? expectedLength, string expectedHash, CancellationToken cancellationToken)
    {
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            throw new CorpusIntegrityException($"The trusted manifest hash for '{Path.GetFileName(path)}' is invalid.");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (expectedLength is not null && stream.Length != expectedLength)
            throw new CorpusIntegrityException($"The bundled '{Path.GetFileName(path)}' size does not match its trusted manifest.");
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expectedHash)))
            throw new CorpusIntegrityException($"The bundled '{Path.GetFileName(path)}' hash does not match its trusted manifest.");
    }
}
