using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WordFlow.Infrastructure.Data;

public static class LegacyVocabularyIdMapper
{
    private static readonly Guid WordNamespace = new("85e2bd5f-cb11-5f2e-a068-51ccf3ec6123");

    public static async Task<IReadOnlyDictionary<long, Guid>> ReadAsync(
        string legacyVocabularyPath,
        string promotedVocabularyPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyVocabularyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(promotedVocabularyPath);
        await using var legacy = await OpenReadOnlyAsync(legacyVocabularyPath, ct).ConfigureAwait(false);
        await using var promoted = await OpenReadOnlyAsync(promotedVocabularyPath, ct).ConfigureAwait(false);
        var promotedByWord = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using (var command = promoted.CreateCommand())
        {
            command.CommandText = "SELECT stable_id,word FROM vocabulary";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var normalized = Normalize(reader.GetString(1));
                if (!Guid.TryParseExact(reader.GetString(0), "D", out var id) || id == Guid.Empty)
                    throw new InvalidDataException("Promoted vocabulary contains a malformed stable ID.");
                if (!promotedByWord.TryAdd(normalized, id))
                    throw new InvalidDataException($"Promoted vocabulary has duplicate normalized spelling '{normalized}'.");
            }
        }

        var result = new Dictionary<long, Guid>();
        await using (var command = legacy.CreateCommand())
        {
            command.CommandText = "SELECT id,word FROM vocabulary ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var legacyId = reader.GetInt64(0);
                var normalized = Normalize(reader.GetString(1));
                var derived = Uuid5(WordNamespace, normalized);
                if (!promotedByWord.TryGetValue(normalized, out var promotedId))
                    throw new InvalidDataException($"Legacy word {legacyId} ('{normalized}') is missing from the promoted vocabulary.");
                if (derived != promotedId)
                    throw new InvalidDataException($"Promoted stable ID for legacy word {legacyId} does not match the UUIDv5 spelling contract.");
                result.Add(legacyId, derived);
            }
        }
        return result;
    }

    internal static string Normalize(string word)
    {
        var builder = new StringBuilder();
        foreach (var rune in word.Trim().Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (Rune.TryCreate(rune.Value, out var scalar)) builder.Append(Rune.ToLowerInvariant(scalar));
        }
        return builder.ToString();
    }

    internal static Guid Uuid5(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        SwapGuidByteOrder(namespaceBytes);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, namespaceBytes.Length);
        var hash = SHA1.HashData(input);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        SwapGuidByteOrder(bytes);
        return new Guid(bytes);
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=5000";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void SwapGuidByteOrder(byte[] bytes)
    {
        (bytes[0], bytes[3]) = (bytes[3], bytes[0]);
        (bytes[1], bytes[2]) = (bytes[2], bytes[1]);
        (bytes[4], bytes[5]) = (bytes[5], bytes[4]);
        (bytes[6], bytes[7]) = (bytes[7], bytes[6]);
    }
}
