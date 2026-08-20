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

public static class TrustedCorpusHashPins
{
    public const string VocabularySha256 = "7B0D2ED0161EC0BC1F4B238A84DFD9D20AAB0A413C412A64501EAD4E52FA1BB9";
    public const string RelationsSha256 = "A6ACD3A73A2354929B083D81C06EB5B5B39FA28F4CD428FA1F3F9840B25851D3";
}

internal sealed record CorpusHashPins(string VocabularySha256, string RelationsSha256);

public static class CorpusIntegrityVerifier
{
    private static readonly CorpusHashPins TrustedPins = new(
        TrustedCorpusHashPins.VocabularySha256, TrustedCorpusHashPins.RelationsSha256);

    public static Task VerifyAsync(AppPaths paths, CancellationToken cancellationToken) =>
        VerifyAsync(paths, TrustedPins, cancellationToken);

    internal static async Task VerifyAsync(
        AppPaths paths,
        CorpusHashPins pins,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(pins);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateApplicationHashPin(pins.VocabularySha256, "vocabulary");
        ValidateApplicationHashPin(pins.RelationsSha256, "relations");
        try
        {
            await VerifyVocabularyAsync(paths, pins.VocabularySha256, cancellationToken).ConfigureAwait(false);
            await VerifyRelationsAsync(paths, pins.RelationsSha256, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (CorpusIntegrityException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            throw new CorpusIntegrityException("The bundled WordFlow corpus could not be verified.", exception);
        }
    }

    private static async Task VerifyVocabularyAsync(AppPaths paths, string trustedHash, CancellationToken cancellationToken)
    {
        using JsonDocument manifest = await ReadManifestAsync(paths.VocabularyManifestPath, cancellationToken).ConfigureAwait(false);
        JsonElement root = RequireObject(manifest.RootElement, "The vocabulary manifest root must be an object.");
        RequirePositiveInteger(root, "schema_version", "vocabulary");
        JsonElement artifacts = RequireObjectProperty(root, "artifacts", "vocabulary");
        JsonElement sqlite = RequireObjectProperty(artifacts, "sqlite", "vocabulary");
        string declaredPath = RequireString(sqlite, "path", "vocabulary");
        long declaredBytes = RequirePositiveInteger(sqlite, "bytes", "vocabulary");
        string declaredHash = RequireHash(sqlite, "sha256", "vocabulary");
        if (!string.Equals(declaredPath, "vocabulary.sqlite3", StringComparison.Ordinal))
            throw ManifestError("The vocabulary manifest contains an invalid database path.");
        RequirePinnedHash(declaredHash, trustedHash, "vocabulary");
        await VerifyFileAsync(paths.VocabularyDatabasePath, declaredBytes, trustedHash, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyRelationsAsync(AppPaths paths, string trustedHash, CancellationToken cancellationToken)
    {
        using JsonDocument manifest = await ReadManifestAsync(paths.RelationManifestPath, cancellationToken).ConfigureAwait(false);
        JsonElement root = RequireObject(manifest.RootElement, "The relations manifest root must be an object.");
        RequirePositiveInteger(root, "schema_version", "relations");
        JsonElement artifacts = RequireObjectProperty(root, "artifacts", "relations");
        string declaredHash = RequireHash(artifacts, "relations.sqlite3", "relations");
        RequirePinnedHash(declaredHash, trustedHash, "relations");
        await VerifyFileAsync(paths.RelationDatabasePath, expectedLength: null, trustedHash, cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement RequireObject(JsonElement value, string error)
    {
        if (value.ValueKind != JsonValueKind.Object) throw ManifestError(error);
        return value;
    }

    private static JsonElement RequireObjectProperty(JsonElement parent, string property, string manifest)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            throw ManifestError($"The {manifest} manifest property '{property}' must be an object.");
        return value;
    }

    private static string RequireString(JsonElement parent, string property, string manifest)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw ManifestError($"The {manifest} manifest property '{property}' must be a string.");
        string? result = value.GetString();
        if (string.IsNullOrWhiteSpace(result))
            throw ManifestError($"The {manifest} manifest property '{property}' cannot be empty.");
        return result;
    }

    private static long RequirePositiveInteger(JsonElement parent, string property, string manifest)
    {
        if (!parent.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long result)
            || result <= 0)
        {
            throw ManifestError($"The {manifest} manifest property '{property}' must be a positive integer.");
        }
        return result;
    }

    private static string RequireHash(JsonElement parent, string property, string manifest)
    {
        string hash = RequireString(parent, property, manifest);
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw ManifestError($"The {manifest} manifest property '{property}' must be a SHA-256 hash.");
        return hash;
    }

    private static void RequirePinnedHash(string declaredHash, string trustedHash, string artifact)
    {
        if (!string.Equals(declaredHash, trustedHash, StringComparison.OrdinalIgnoreCase))
            throw ManifestError($"The {artifact} manifest does not match the trusted application hash pin.");
    }

    private static void ValidateApplicationHashPin(string hash, string artifact)
    {
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidOperationException($"The application corpus hash pin for {artifact} is invalid.");
    }

    private static CorpusIntegrityException ManifestError(string message) => new(message);

    private static async Task<JsonDocument> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyFileAsync(string path, long? expectedLength, string expectedHash, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (expectedLength is not null && stream.Length != expectedLength)
            throw new CorpusIntegrityException($"The bundled '{Path.GetFileName(path)}' size does not match its trusted manifest.");
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expectedHash)))
            throw new CorpusIntegrityException($"The bundled '{Path.GetFileName(path)}' hash does not match the trusted application pin.");
    }
}
