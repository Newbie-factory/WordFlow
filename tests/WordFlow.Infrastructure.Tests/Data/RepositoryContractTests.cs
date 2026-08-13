using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Data;

namespace WordFlow.Infrastructure.Tests.Data;

public sealed class RepositoryContractTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wordflow-repository-{Guid.NewGuid():N}");

    public RepositoryContractTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Corpus_connection_is_truly_read_only_and_rejects_writes()
    {
        var corpus = Path.Combine(directory, "corpus.db");
        await CreateCorpusAsync(corpus);
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "user.db"), corpus);

        await using var connection = await factory.OpenCorpusAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO word VALUES ('00000000-0000-0000-0000-000000000099', 'write', 99)";

        var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(8, error.SqliteErrorCode);
    }

    [Fact]
    public async Task Word_and_sense_pages_are_stable_and_validate_page_arguments()
    {
        var corpus = Path.Combine(directory, "corpus.db");
        await CreateCorpusAsync(corpus);
        var repository = new SqliteVocabularyRepository(
            new SqliteConnectionFactory(Path.Combine(directory, "user.db"), corpus));

        var words = await repository.GetWordsAsync(new PageRequest(0, 1), default);
        var senses = await repository.GetSensesAsync(Id(1), new PageRequest(0, 10), default);

        Assert.Equal(2, words.TotalCount);
        Assert.Equal("alpha", Assert.Single(words.Items).Lemma);
        Assert.Equal("first", Assert.Single(senses.Items).Definition);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(0, 0));
    }

    [Fact]
    public async Task User_relation_overrides_live_only_in_user_database_and_overlay_corpus_relations()
    {
        var corpus = Path.Combine(directory, "corpus.db");
        var user = Path.Combine(directory, "user.db");
        await CreateCorpusAsync(corpus);
        var factory = new SqliteConnectionFactory(user, corpus);
        await new MigrationRunner(factory).MigrateAsync(default);
        var repository = new SqliteRelationRepository(factory);

        await repository.SetOverrideAsync(new UserRelationOverride(Id(1), Id(2), "synonym", false), default);
        Assert.Empty((await repository.GetRelationsAsync(Id(1), new PageRequest(0, 10), default)).Items);
        await repository.SetOverrideAsync(new UserRelationOverride(Id(1), Id(2), "antonym", true), default);
        var relations = await repository.GetRelationsAsync(Id(1), new PageRequest(0, 10), default);

        Assert.Equal("antonym", Assert.Single(relations.Items).RelationType);
        await using var corpusConnection = await factory.OpenCorpusAsync(default);
        Assert.Equal(0L, await ScalarAsync(corpusConnection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='user_word_relation'"));
        await using var userConnection = await factory.OpenUserAsync(default);
        Assert.Equal(2L, await ScalarAsync(userConnection, "SELECT COUNT(*) FROM user_word_relation"));
    }

    private static async Task CreateCorpusAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE word(word_id TEXT PRIMARY KEY, lemma TEXT NOT NULL, frequency_rank INTEGER NOT NULL); CREATE TABLE sense(sense_id TEXT PRIMARY KEY, word_id TEXT NOT NULL, definition TEXT NOT NULL); CREATE TABLE word_relation(source_word_id TEXT NOT NULL, target_word_id TEXT NOT NULL, relation_type TEXT NOT NULL, PRIMARY KEY(source_word_id,target_word_id,relation_type));");
        await ExecuteAsync(connection, $"INSERT INTO word VALUES ('{Id(1):D}', 'alpha', 1), ('{Id(2):D}', 'beta', 2); INSERT INTO sense VALUES ('{Id(11):D}', '{Id(1):D}', 'first'); INSERT INTO word_relation VALUES ('{Id(1):D}', '{Id(2):D}', 'synonym');");
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

    private static Guid Id(int value) => new($"00000000-0000-0000-0000-{value:D12}");

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
