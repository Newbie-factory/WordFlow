using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Data;

namespace WordFlow.Infrastructure.Tests.Data;

public sealed class RepositoryContractTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wordflow-repository-{Guid.NewGuid():N}");

    public RepositoryContractTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Checked_in_promoted_artifacts_support_word_sense_and_nullable_rank_pages()
    {
        var vocabulary = CopyArtifact("vocabulary.sqlite3");
        var relations = CopyArtifact("relations.sqlite3");
        var repository = new SqliteVocabularyRepository(Factory(vocabulary, relations));

        var words = await repository.GetWordsAsync(new PageRequest(0, 500), default);
        var nullRank = words.Items.FirstOrDefault(word => word.FrequencyRank is null)
            ?? (await FindNullRankPageAsync(repository));
        var senses = await repository.GetSensesAsync(nullRank.WordId, new PageRequest(0, 500), default);
        var exact = await repository.GetWordAsync(nullRank.WordId, default);

        Assert.True(words.TotalCount >= 10_000);
        Assert.Null(nullRank.FrequencyRank);
        Assert.Equal(nullRank, exact);
        Assert.All(senses.Items, sense => Assert.Equal(nullRank.WordId, sense.WordId));
        Assert.All(senses.Items, sense => Assert.Contains(sense.PartOfSpeech, new[] { "n", "v", "a", "r", "s" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(0, 0));
    }

    [Fact]
    public async Task Relation_direction_is_respected_and_user_overrides_are_user_only()
    {
        var vocabulary = CopyArtifact("vocabulary.sqlite3");
        var relations = CopyArtifact("relations.sqlite3");
        var factory = Factory(vocabulary, relations);
        await new MigrationRunner(factory).MigrateAsync(default);
        var repository = new SqliteRelationRepository(factory);
        var (forwardSource, forwardTarget, forwardKind) = await RelationRowAsync(relations, "forward");
        var (biSource, biTarget, biKind) = await RelationRowAsync(relations, "bidirectional");

        Assert.Contains((await repository.GetRelationsAsync(forwardSource, new PageRequest(0, 500), default)).Items,
            relation => relation.TargetWordId == forwardTarget && relation.RelationType == forwardKind && relation.Direction == RelationDirection.Forward);
        Assert.DoesNotContain((await repository.GetRelationsAsync(forwardTarget, new PageRequest(0, 500), default)).Items,
            relation => relation.TargetWordId == forwardSource && relation.RelationType == forwardKind && relation.Direction == RelationDirection.Bidirectional);
        Assert.Contains((await repository.GetRelationsAsync(biTarget, new PageRequest(0, 500), default)).Items,
            relation => relation.TargetWordId == biSource && relation.RelationType == biKind && relation.Direction == RelationDirection.Bidirectional);

        var synonym = await SemanticRelationRowAsync(relations);
        var synonymResult = (await repository.GetRelationsAsync(synonym.Source, new PageRequest(0, 500), default)).Items
            .Single(relation => relation.TargetWordId == synonym.Target && relation.RelationType == RelationKinds.Synonym
                && relation.SourceSenseId == synonym.SourceSense && relation.TargetSenseId == synonym.TargetSense);
        Assert.Equal(synonym.Pos, synonymResult.PartOfSpeech);
        var reverse = (await repository.GetRelationsAsync(synonym.Target, new PageRequest(0, 500), default)).Items
            .Single(relation => relation.TargetWordId == synonym.Source && relation.RelationType == RelationKinds.Synonym
                && relation.SourceSenseId == synonym.TargetSense && relation.TargetSenseId == synonym.SourceSense);
        Assert.Equal(synonym.Pos, reverse.PartOfSpeech);

        var correction = await MisspellingRowAsync(relations);
        Assert.Contains((await repository.GetMisspellingsAsync(correction.Target, new PageRequest(0, 500), default)).Items,
            item => item.Spelling == correction.Spelling);

        await repository.SetOverrideAsync(new UserRelationOverride(biTarget, biSource, biKind, false), default);
        Assert.DoesNotContain((await repository.GetRelationsAsync(biTarget, new PageRequest(0, 500), default)).Items,
            relation => relation.TargetWordId == biSource && relation.RelationType == biKind);
        await repository.SetOverrideAsync(new UserRelationOverride(biTarget, biSource, "personal_confusable", true), default);
        Assert.Contains((await repository.GetRelationsAsync(biTarget, new PageRequest(0, 500), default)).Items,
            relation => relation.TargetWordId == biSource && relation.RelationType == "personal_confusable");

        await using var relationConnection = await factory.OpenRelationsAsync(default);
        Assert.Equal(0L, await ScalarAsync(relationConnection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='user_word_relation'"));
        await using var userConnection = await factory.OpenUserAsync(default);
        Assert.Equal(2L, await ScalarAsync(userConnection, "SELECT COUNT(*) FROM user_word_relation"));
    }

    [Fact]
    public async Task Both_checked_in_corpus_copies_are_truly_read_only()
    {
        var vocabulary = CopyArtifact("vocabulary.sqlite3");
        var relations = CopyArtifact("relations.sqlite3");
        var factory = Factory(vocabulary, relations);

        await using var vocabularyConnection = await factory.OpenVocabularyAsync(default);
        var vocabularyError = await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(vocabularyConnection, "DELETE FROM vocabulary"));
        await using var relationConnection = await factory.OpenRelationsAsync(default);
        var relationError = await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(relationConnection, "DELETE FROM published_relation_base"));

        Assert.Equal(8, vocabularyError.SqliteErrorCode);
        Assert.Equal(8, relationError.SqliteErrorCode);
    }

    [Fact]
    public async Task Cannot_open_corpus_reads_are_translated_and_cancellation_propagates()
    {
        var missing = Path.Combine(directory, "missing.sqlite3");
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "user.db"), missing, missing);
        var vocabulary = new SqliteVocabularyRepository(factory);
        var relations = new SqliteRelationRepository(factory);

        await Assert.ThrowsAsync<TransientStorageException>(() => vocabulary.GetWordsAsync(new PageRequest(0, 1), default));
        await Assert.ThrowsAsync<TransientStorageException>(() => relations.GetRelationsAsync(Guid.NewGuid(), new PageRequest(0, 1), default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vocabulary.GetWordsAsync(new PageRequest(0, 1), cancelled.Token));
    }

    [Fact]
    public async Task Locked_override_write_is_translated()
    {
        var vocabulary = CopyArtifact("vocabulary.sqlite3");
        var relations = CopyArtifact("relations.sqlite3");
        var database = Path.Combine(directory, "locked-user.db");
        var migrationFactory = new SqliteConnectionFactory(database, vocabulary, relations);
        await new MigrationRunner(migrationFactory).MigrateAsync(default);
        var factory = new SqliteConnectionFactory(database, vocabulary, relations, busyTimeoutMilliseconds: 1);
        var repository = new SqliteRelationRepository(factory);
        await using var blocker = await migrationFactory.OpenUserAsync(default);
        await using var transaction = blocker.BeginTransaction(deferred: false);

        await Assert.ThrowsAsync<TransientStorageException>(() => repository.SetOverrideAsync(
            new UserRelationOverride(Guid.NewGuid(), Guid.NewGuid(), RelationKinds.PersonalConfusable, true), default));
    }

    [Fact]
    public async Task Corpus_schema_corruption_and_programmer_errors_are_not_translated()
    {
        var malformed = Path.Combine(directory, "malformed.sqlite3");
        await using (var connection = new SqliteConnection($"Data Source={malformed};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "CREATE TABLE unexpected(value TEXT)");
        }
        var malformedRepository = new SqliteVocabularyRepository(
            new SqliteConnectionFactory(Path.Combine(directory, "user.db"), malformed, malformed));
        var schemaError = await Assert.ThrowsAsync<SqliteException>(() =>
            malformedRepository.GetWordsAsync(new PageRequest(0, 1), default));
        Assert.Equal(1, schemaError.SqliteErrorCode);

        var corrupt = Path.Combine(directory, "corrupt.sqlite3");
        await File.WriteAllBytesAsync(corrupt, "not sqlite"u8.ToArray());
        var corruptRepository = new SqliteRelationRepository(
            new SqliteConnectionFactory(Path.Combine(directory, "user.db"), corrupt, corrupt));
        await Assert.ThrowsAsync<SqliteException>(() =>
            corruptRepository.GetMisspellingsAsync(Guid.NewGuid(), new PageRequest(0, 1), default));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            malformedRepository.GetWordAsync(Guid.Empty, default));
    }

    [Fact]
    public async Task Override_constraint_error_is_not_translated()
    {
        var vocabulary = CopyArtifact("vocabulary.sqlite3");
        var relations = CopyArtifact("relations.sqlite3");
        var factory = Factory(vocabulary, relations);
        await new MigrationRunner(factory).MigrateAsync(default);
        await using (var connection = await factory.OpenUserAsync(default))
            await ExecuteAsync(connection, "CREATE TRIGGER reject_override BEFORE INSERT ON user_word_relation BEGIN SELECT RAISE(ABORT,'reject'); END");
        var repository = new SqliteRelationRepository(factory);

        var exception = await Assert.ThrowsAsync<SqliteException>(() => repository.SetOverrideAsync(
            new UserRelationOverride(Guid.NewGuid(), Guid.NewGuid(), RelationKinds.PersonalConfusable, true), default));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    private SqliteConnectionFactory Factory(string vocabulary, string relations) =>
        new(Path.Combine(directory, "user.db"), vocabulary, relations);

    private string CopyArtifact(string name)
    {
        var source = Path.Combine(FindRepositoryRoot(), "data", "ielts", name);
        var target = Path.Combine(directory, name);
        File.Copy(source, target);
        return target;
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "WordFlow.sln")))
            current = Directory.GetParent(current)?.FullName;
        return current ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static async Task<VocabularyWord> FindNullRankPageAsync(SqliteVocabularyRepository repository)
    {
        for (var offset = 500; ; offset += 500)
        {
            var page = await repository.GetWordsAsync(new PageRequest(offset, 500), default);
            var found = page.Items.FirstOrDefault(word => word.FrequencyRank is null);
            if (found is not null) return found;
            if (offset + page.Items.Count >= page.TotalCount) throw new Xunit.Sdk.XunitException("Promoted artifact has no null frequency rank.");
        }
    }

    private static async Task<(Guid Source, Guid Target, string Kind)> RelationRowAsync(string path, string direction)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_entry_id,target_entry_id,kind FROM published_word_relation WHERE direction=$direction AND source_entry_id IS NOT NULL LIMIT 1";
        command.Parameters.AddWithValue("$direction", direction);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2));
    }

    private static async Task<(Guid Source, Guid Target, string SourceSense, string TargetSense, string Pos)> SemanticRelationRowAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_entry_id,target_entry_id,source_sense_id,target_sense_id,pos FROM published_word_relation WHERE kind='synonym' LIMIT 1";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    }

    private static async Task<(string Spelling, Guid Target)> MisspellingRowAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_spelling,target_entry_id FROM published_word_relation WHERE kind='misspelling' LIMIT 1";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), Guid.Parse(reader.GetString(1)));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
