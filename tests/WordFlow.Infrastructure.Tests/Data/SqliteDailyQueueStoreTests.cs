using Microsoft.Data.Sqlite;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;
using WordFlow.Infrastructure.Data;

namespace WordFlow.Infrastructure.Tests.Data;

public sealed class SqliteDailyQueueStoreTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 8, 13);
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wordflow-daily-{Guid.NewGuid():N}");

    public SqliteDailyQueueStoreTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Migration_three_creates_strict_daily_schema_with_guarded_values()
    {
        var factory = await CreateMigratedFactoryAsync(Database("schema.db"));
        await using var connection = await factory.OpenUserAsync(default);

        Assert.Equal(3L, await ScalarAsync(connection, "SELECT MAX(version) FROM schema_version"));
        var tables = await StringsAsync(connection,
            "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('daily_sessions','daily_queue_items')");
        Assert.Equal(new[] { "daily_queue_items", "daily_sessions" }, tables.Order());
        Assert.All(await StringsAsync(connection,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name IN ('daily_sessions','daily_queue_items')"),
            sql => Assert.EndsWith("STRICT", sql, StringComparison.OrdinalIgnoreCase));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "INSERT INTO daily_sessions VALUES ('2026-8-13',1,1,1,1,NULL,'x','x')"));
        await ExecuteAsync(connection,
            "INSERT INTO daily_sessions VALUES ('2026-08-13',1,1,1,1,NULL,'2026-08-13T08:00:00.0000000Z','2026-08-13T08:00:00.0000000Z')");
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            $"INSERT INTO daily_queue_items VALUES ('{Id(1):D}','2026-08-13',0,'not-a-guid','New','2026-08-13',NULL,'Pending',NULL,'2026-08-13T08:00:00.0000000Z',NULL)"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            $"INSERT INTO daily_queue_items VALUES ('{Id(1):D}','2026-08-13',0,'{Id(2):D}','Unknown','2026-08-13',NULL,'Pending',NULL,'2026-08-13T08:00:00.0000000Z',NULL)"));
    }

    [Fact]
    public async Task Same_day_creation_is_idempotent_and_restores_exact_item_ids_ordinals_and_statuses()
    {
        var factory = await CreateMigratedFactoryAsync(Database("resume.db"));
        var store = new SqliteDailyQueueStore(factory);
        var first = await store.GetOrCreateAsync(Seed(Today, 2, 2, reviews: Range(1, 2), news: Range(11, 2)), default);
        await using (var connection = await factory.OpenUserAsync(default))
            await ExecuteAsync(connection, "UPDATE daily_queue_items SET status='Completed' WHERE local_day='2026-08-13' AND ordinal=0");

        var restored = await store.GetOrCreateAsync(Seed(Today, 99, 99, reviews: Range(100, 2), news: Range(200, 2)), default);

        Assert.Equal(first.ConfiguredPlan, restored.ConfiguredPlan);
        Assert.Equal(first.EffectivePlan, restored.EffectivePlan);
        Assert.Equal(first.Items.Select(x => (x.ItemId, x.Ordinal, x.CardId)), restored.Items.Select(x => (x.ItemId, x.Ordinal, x.CardId)));
        Assert.Equal(DailyQueueItemStatus.Completed, restored.Items[0].Status);
    }

    [Fact]
    public async Task New_day_carries_27_then_adds_13_to_a_40_item_new_target()
    {
        var factory = await CreateMigratedFactoryAsync(Database("carry.db"));
        var store = new SqliteDailyQueueStore(factory);
        var yesterday = Today.AddDays(-1);
        await store.GetOrCreateAsync(Seed(yesterday, 40, 0, news: Range(1, 40)), default);
        await using (var connection = await factory.OpenUserAsync(default))
            await ExecuteAsync(connection, "UPDATE daily_queue_items SET status='Completed' WHERE local_day='2026-08-12' AND ordinal < 13");

        var today = await store.GetOrCreateAsync(Seed(Today, 40, 0, news: Range(101, 100)), default);

        Assert.Equal(40, today.EffectivePlan.NewLimit);
        Assert.Equal(27, today.Items.Count(x => x.Kind == DailyQueueItemKind.New && x.SourceItemId.HasValue));
        Assert.Equal(13, today.Items.Count(x => x.Kind == DailyQueueItemKind.New && x.SourceItemId is null));
        await using var verify = await factory.OpenUserAsync(default);
        Assert.Equal(27L, await ScalarAsync(verify, "SELECT COUNT(*) FROM daily_queue_items WHERE local_day='2026-08-12' AND status='CarriedForward'"));
    }

    [Fact]
    public async Task Carryover_is_capped_within_target_and_preserves_source_lineage_across_days()
    {
        var factory = await CreateMigratedFactoryAsync(Database("lineage.db"));
        var store = new SqliteDailyQueueStore(factory);
        var dayOne = Today.AddDays(-2);
        var first = await store.GetOrCreateAsync(Seed(dayOne, 3, 0, news: Range(1, 3)), default);

        var second = await store.GetOrCreateAsync(Seed(dayOne.AddDays(1), 1, 0, news: Range(50, 5)), default);
        var third = await store.GetOrCreateAsync(Seed(Today, 1, 0, news: Range(60, 5)), default);

        var dayTwoItem = Assert.Single(second.Items);
        var dayThreeItem = Assert.Single(third.Items);
        Assert.Equal(first.Items[0].ItemId, dayTwoItem.SourceItemId);
        Assert.Equal(first.Items[1].ItemId, dayThreeItem.SourceItemId);
        Assert.Equal(dayOne, dayTwoItem.OriginDay);
        Assert.Equal(dayOne, dayThreeItem.OriginDay);
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM daily_queue_items WHERE local_day='2026-08-11' AND status='Pending'"));
    }

    [Fact]
    public async Task Next_pending_prioritizes_relearning_then_review_then_new()
    {
        var factory = await CreateMigratedFactoryAsync(Database("priority.db"));
        var store = new SqliteDailyQueueStore(factory);
        var snapshot = await store.GetOrCreateAsync(Seed(Today, 1, 1, reviews: [Id(1)], news: [Id(2)]), default);
        await using (var connection = await factory.OpenUserAsync(default))
            await ExecuteAsync(connection, $"INSERT INTO daily_queue_items VALUES ('{Id(900):D}','2026-08-13',99,'{Id(3):D}','Relearning','2026-08-13',NULL,'Pending',NULL,'2026-08-13T08:00:00.0000000Z',NULL)");

        var relearning = await store.GetNextPendingAsync(Today, Now, default);
        await using (var connection = await factory.OpenUserAsync(default))
            await ExecuteAsync(connection, $"UPDATE daily_queue_items SET status='Completed' WHERE item_id='{relearning!.ItemId:D}'");
        var review = await store.GetNextPendingAsync(Today, Now, default);
        await using (var connection = await factory.OpenUserAsync(default))
            await ExecuteAsync(connection, $"UPDATE daily_queue_items SET status='Completed' WHERE item_id='{review!.ItemId:D}'");
        var @new = await store.GetNextPendingAsync(Today, Now, default);

        Assert.Equal(DailyQueueItemKind.Relearning, relearning.Kind);
        Assert.Equal(DailyQueueItemKind.Review, review.Kind);
        Assert.Equal(DailyQueueItemKind.New, @new!.Kind);
        Assert.Contains(@new.ItemId, snapshot.Items.Select(x => x.ItemId));
    }

    [Fact]
    public async Task Due_same_day_again_creates_one_relearning_item_and_reopens_session()
    {
        var factory = await CreateMigratedFactoryAsync(Database("relearning.db"));
        var queue = new SqliteDailyQueueStore(factory);
        var learning = new SqliteLearningStore(factory);
        var snapshot = await queue.GetOrCreateAsync(Seed(Today, 0, 1, reviews: [Id(1)]), default);
        var card = new CardState(Id(1), null, Now);
        var again = new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(Now)).Review(Id(501), card, Rating.Again);
        await learning.ApplyQueuedAsync(new LearningCommand(Id(601), again, CardProjection.InitialRevision),
            snapshot.Items[0].ItemId, DailyQueueItemStatus.Completed, default);

        await queue.EnsureDueRelearningAsync(Today, Now.AddMinutes(9), default);
        Assert.Null(await queue.GetNextPendingAsync(Today, Now.AddMinutes(9), default));
        await queue.EnsureDueRelearningAsync(Today, Now.AddMinutes(11), default);
        await queue.EnsureDueRelearningAsync(Today, Now.AddMinutes(11), default);

        var restored = await queue.GetOrCreateAsync(Seed(Today, 99, 99), default);
        Assert.Null(restored.CompletedAt);
        Assert.Single(restored.Items, x => x.Kind == DailyQueueItemKind.Relearning && x.Status == DailyQueueItemStatus.Pending);
    }

    [Fact]
    public async Task History_returns_every_one_of_183_dates_and_fills_absent_days()
    {
        var factory = await CreateMigratedFactoryAsync(Database("history.db"));
        var store = new SqliteDailyQueueStore(factory);
        await store.GetOrCreateAsync(Seed(Today.AddDays(-182), 1, 0, news: [Id(1)]), default);
        await store.GetOrCreateAsync(Seed(Today, 2, 1, reviews: [Id(2)], news: [Id(3), Id(4)]), default);

        var history = await store.GetHistoryAsync(Today, 183, default);

        Assert.Equal(183, history.Count);
        Assert.Equal(Today.AddDays(-182), history[0].LocalDay);
        Assert.Equal(Today, history[^1].LocalDay);
        Assert.True(history[0].WasStarted);
        Assert.False(history[1].WasStarted);
        Assert.Equal(2, history[^1].NewTarget);
        Assert.Equal(1, history[^1].ReviewTarget);
    }

    [Fact]
    public async Task Goal_progress_counts_current_distinct_slashes_and_all_promoted_vocabulary_rows()
    {
        var vocabulary = Database("vocabulary.db");
        await CreateVocabularyAsync(vocabulary, 5);
        var factory = await CreateMigratedFactoryAsync(Database("progress.db"), vocabulary);
        var learning = new SqliteLearningStore(factory);
        foreach (var value in new[] { 1, 2 })
        {
            var card = new CardState(Id(value), null, Now);
            var slash = new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(Now)).Slash(Id(700 + value), card);
            await learning.ApplyAsync(new LearningCommand(Id(800 + value), slash, CardProjection.InitialRevision), default);
        }

        var progress = await new SqliteDailyQueueStore(factory).GetGoalProgressAsync(default);

        Assert.Equal(new GoalProgress(2, 5), progress);
    }

    private async Task<SqliteConnectionFactory> CreateMigratedFactoryAsync(string userDatabase, string? vocabulary = null)
    {
        var factory = new SqliteConnectionFactory(userDatabase, vocabulary);
        await new MigrationRunner(factory).MigrateAsync(default);
        return factory;
    }

    private static DailyQueueSeed Seed(
        DateOnly day,
        int newTarget,
        int reviewTarget,
        IReadOnlyList<Guid>? reviews = null,
        IReadOnlyList<Guid>? news = null) =>
        new(day, new DailyPlan(newTarget, reviewTarget), reviews ?? [], news ?? [],
            new DateTimeOffset(day.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc)));

    private static Guid[] Range(int start, int count) => Enumerable.Range(start, count).Select(Id).ToArray();
    private static Guid Id(int value) => new($"00000000-0000-0000-0000-{value:D12}");
    private string Database(string name) => Path.Combine(directory, name);

    private static async Task CreateVocabularyAsync(string path, int count)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE vocabulary(stable_id TEXT PRIMARY KEY, word TEXT NOT NULL)");
        for (var index = 1; index <= count; index++)
            await ExecuteAsync(connection, $"INSERT INTO vocabulary VALUES ('{Id(9000 + index):D}','word-{index}')");
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
