using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;
using WordFlow.Infrastructure.Data;

namespace WordFlow.Infrastructure.Tests.Data;

public sealed class SqliteLearningStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wordflow-data-{Guid.NewGuid():N}");

    public SqliteLearningStoreTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Apply_inserts_immutable_event_and_updates_snapshot_in_one_transaction()
    {
        var database = Database("user.db");
        var factory = await CreateMigratedFactoryAsync(database);
        var store = new SqliteLearningStore(factory);
        var review = Review(Id(11), InitialCard(), Rating.Good);

        var result = await store.ApplyAsync(new LearningCommand(Id(101), review), default);

        Assert.True(result.Applied);
        Assert.Equal(review.After, result.Card);
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM card_state"));
        Assert.Equal("2026-08-13T08:00:00.0000000Z", await TextAsync(connection, "SELECT occurred_at_utc FROM review_event"));
        var snapshots = await TextAsync(connection, "SELECT before_json || after_json FROM review_event");
        Assert.Contains("Z", snapshots, StringComparison.Ordinal);
        Assert.DoesNotContain("+00:00", snapshots, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_between_event_and_snapshot_rolls_back_both_writes()
    {
        var database = Database("user.db");
        var factory = await CreateMigratedFactoryAsync(database);
        var store = new SqliteLearningStore(factory, stage =>
        {
            if (stage == LearningCommitStage.EventWritten)
            {
                throw new InjectedFailureException();
            }
        });
        var review = Review(Id(12), InitialCard(), Rating.Good);

        await Assert.ThrowsAsync<InjectedFailureException>(
            () => store.ApplyAsync(new LearningCommand(Id(102), review), default));

        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM card_state"));
    }

    [Fact]
    public async Task Repeated_command_id_is_idempotent_even_with_a_different_event_payload()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        var first = Review(Id(13), InitialCard(), Rating.Good);
        var conflictingRetry = first with { EventId = Id(14), Action = LearningAction.Hard };

        var committed = await store.ApplyAsync(new LearningCommand(Id(103), first), default);
        var retried = await store.ApplyAsync(new LearningCommand(Id(103), conflictingRetry), default);

        Assert.True(committed.Applied);
        Assert.False(retried.Applied);
        Assert.Equal(first.EventId, retried.EventId);
        Assert.Equal(first.After, retried.Card);
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
    }

    [Fact]
    public async Task Undo_is_persisted_as_a_compensating_event_without_mutating_history()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        var original = Review(Id(15), InitialCard(), Rating.Good);
        var undo = new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(Now.AddMinutes(1)))
            .Undo(Id(16), original);

        await store.ApplyAsync(new LearningCommand(Id(104), original), default);
        await store.ApplyAsync(new LearningCommand(Id(105), undo), default);

        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
        Assert.Equal(Id(15).ToString("D"), await TextAsync(connection,
            "SELECT compensates_event_id FROM review_event WHERE action = 'Undo'"));
        Assert.Equal(original.Before, (await store.GetCardAsync(original.CardId, default))!);
    }

    [Fact]
    public async Task Stale_command_is_rejected_and_does_not_append_an_event()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        var first = Review(Id(17), InitialCard(), Rating.Good);
        var stale = Review(Id(18), InitialCard(), Rating.Hard);
        await store.ApplyAsync(new LearningCommand(Id(106), first), default);

        await Assert.ThrowsAsync<LearningConcurrencyException>(
            () => store.ApplyAsync(new LearningCommand(Id(107), stale), default));

        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
    }

    [Fact]
    public async Task Event_rows_cannot_be_updated_or_deleted_even_by_direct_sql()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        await store.ApplyAsync(new LearningCommand(Id(109), Review(Id(20), InitialCard(), Rating.Good)), default);
        await using var connection = await factory.OpenUserAsync(default);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE review_event SET action='Hard'"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM review_event"));
    }

    [Fact]
    public async Task Concurrent_retries_of_one_command_converge_to_one_committed_event()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        var command = new LearningCommand(Id(110), Review(Id(21), InitialCard(), Rating.Good));

        var results = await Task.WhenAll(
            store.ApplyAsync(command, default),
            store.ApplyAsync(command, default));

        Assert.Single(results, result => result.Applied);
        Assert.Single(results, result => !result.Applied);
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
    }

    [Fact]
    public async Task Precancelled_command_does_not_write_event_or_snapshot()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ApplyAsync(new LearningCommand(Id(111), Review(Id(22), InitialCard(), Rating.Good)), cancellation.Token));

        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM card_state"));
    }

    [Fact]
    public async Task Schema_contains_every_required_table_and_enables_safety_pragmas()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        await using var connection = await factory.OpenUserAsync(default);
        var tables = await StringsAsync(connection, "SELECT name FROM sqlite_master WHERE type='table'");

        Assert.Subset(tables.ToHashSet(), new HashSet<string>
        {
            "card_state", "review_event", "slash_event", "daily_plan", "app_setting",
            "shortcut_binding", "user_word_relation", "skin_preset", "backup_record", "schema_version",
        });
        Assert.Equal(1L, await ScalarAsync(connection, "PRAGMA foreign_keys"));
        Assert.Equal(5000L, await ScalarAsync(connection, "PRAGMA busy_timeout"));
        Assert.Equal("wal", (await TextAsync(connection, "PRAGMA journal_mode")).ToLowerInvariant());
    }

    [Fact]
    public async Task Migration_failure_rolls_back_schema_and_data_and_creates_backup()
    {
        var database = Database("user.db");
        var factory = await CreateMigratedFactoryAsync(database);
        await using (var connection = await factory.OpenUserAsync(default))
        {
            await ExecuteAsync(connection, "INSERT INTO app_setting(key, value) VALUES ('theme', 'dark')");
        }
        var runner = new MigrationRunner(factory, new[]
        {
            new SqliteMigration(2, "broken", "CREATE TABLE should_rollback(id INTEGER); INSERT INTO missing_table VALUES (1);")
        });

        await Assert.ThrowsAsync<SqliteException>(() => runner.MigrateAsync(default));

        await using var verify = await factory.OpenUserAsync(default);
        Assert.Equal("dark", await TextAsync(verify, "SELECT value FROM app_setting WHERE key='theme'"));
        Assert.Equal(0L, await ScalarAsync(verify,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='should_rollback'"));
        Assert.Single(Directory.GetFiles(directory, "user.db.v1.*.backup"));
    }

    [Fact]
    public async Task Stable_word_id_retains_review_and_slash_history_after_corpus_promotion()
    {
        var userDatabase = Database("user.db");
        var factory = await CreateMigratedFactoryAsync(userDatabase);
        var store = new SqliteLearningStore(factory);
        var slash = new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(Now))
            .Slash(Id(19), InitialCard());
        await store.ApplyAsync(new LearningCommand(Id(108), slash), default);
        var oldCorpus = Database("corpus-8000.db");
        var promotedCorpus = Database("corpus-promoted.db");
        await CreateCorpusAsync(oldCorpus, (InitialCard().Id, "retain", 7999));
        await CreateCorpusAsync(promotedCorpus, (InitialCard().Id, "retain", 12001));

        var oldWord = Assert.Single((await new SqliteVocabularyRepository(
            factory.WithCorpus(oldCorpus)).GetWordsAsync(new PageRequest(0, 20), default)).Items);
        var promotedWord = Assert.Single((await new SqliteVocabularyRepository(
            factory.WithCorpus(promotedCorpus)).GetWordsAsync(new PageRequest(0, 20), default)).Items);

        Assert.Equal(oldWord.WordId, promotedWord.WordId);
        Assert.True((await store.GetCardAsync(promotedWord.WordId, default))!.Slash.IsSlashed);
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM slash_event"));
    }

    private async Task<SqliteConnectionFactory> CreateMigratedFactoryAsync(string userDatabase)
    {
        var factory = new SqliteConnectionFactory(userDatabase);
        await new MigrationRunner(factory).MigrateAsync(default);
        return factory;
    }

    private static ReviewEvent Review(Guid eventId, CardState before, Rating rating) =>
        new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(Now)).Review(eventId, before, rating);

    private static CardState InitialCard() => new(Id(1), null, Now);

    private static Guid Id(int value) => new($"00000000-0000-0000-0000-{value:D12}");
    private string Database(string name) => Path.Combine(directory, name);

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> TextAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<HashSet<string>> StringsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new HashSet<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateCorpusAsync(string path, params (Guid Id, string Lemma, int Rank)[] words)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE word(word_id TEXT PRIMARY KEY, lemma TEXT NOT NULL, frequency_rank INTEGER NOT NULL); CREATE TABLE sense(sense_id TEXT PRIMARY KEY, word_id TEXT NOT NULL, definition TEXT NOT NULL); CREATE TABLE word_relation(source_word_id TEXT NOT NULL, target_word_id TEXT NOT NULL, relation_type TEXT NOT NULL, PRIMARY KEY(source_word_id,target_word_id,relation_type));");
        foreach (var word in words)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO word VALUES ($id, $lemma, $rank)";
            command.Parameters.AddWithValue("$id", word.Id.ToString("D"));
            command.Parameters.AddWithValue("$lemma", word.Lemma);
            command.Parameters.AddWithValue("$rank", word.Rank);
            await command.ExecuteNonQueryAsync();
        }
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InjectedFailureException : Exception;
}
