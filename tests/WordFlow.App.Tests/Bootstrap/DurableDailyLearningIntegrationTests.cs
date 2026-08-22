using Microsoft.Data.Sqlite;
using WordFlow.Application;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;
using WordFlow.Infrastructure.Data;

namespace WordFlow.App.Tests.Bootstrap;

public sealed class DurableDailyLearningIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"WordFlow.DurableDaily.{Guid.NewGuid():N}");

    public DurableDailyLearningIntegrationTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Fresh_runtime_against_the_same_database_resumes_the_exact_fourteenth_item()
    {
        var paths = await CreateDatabasesAsync(40);
        var clock = new FixedTimeProvider(Now);
        var firstRun = Runtime(paths, clock);
        var current = await firstRun.Coordinator.GetNextAsync(new DailyPlan(40, 0), default);
        for (var index = 0; index < 13; index++)
        {
            var result = await firstRun.Submit.HandleAsync(new(
                Guid.NewGuid(), Guid.NewGuid(), current!.Card, current.Revision,
                current.QueueItemId, RatingShortcut.F3, current.QueueDay, new DailyPlan(40, 0)), default);
            current = Assert.IsType<Success<LearningTransition>>(result).Value.NextCard;
        }

        var expected = current!;
        var reopened = Runtime(paths, clock);
        var resumed = await reopened.Coordinator.GetNextAsync(new DailyPlan(40, 0), default);

        Assert.Equal(expected.Word.WordId, resumed!.Word.WordId);
        Assert.Equal(expected.QueueItemId, resumed.QueueItemId);
    }

    [Fact]
    public async Task Failure_after_event_insertion_rolls_back_event_projection_and_queue_before_reporting_failure()
    {
        var paths = await CreateDatabasesAsync(2);
        var clock = new FixedTimeProvider(Now);
        var runtime = Runtime(paths, clock);
        var current = await runtime.Coordinator.GetNextAsync(new DailyPlan(2, 0), default);
        var failing = Runtime(paths, clock, stage =>
        {
            if (stage == LearningCommitStage.EventWritten)
                throw new TransientStorageException("injected failure after event insertion", new IOException());
        });

        var result = await failing.Submit.HandleAsync(new(
            Guid.NewGuid(), Guid.NewGuid(), current!.Card, current.Revision,
            current.QueueItemId, RatingShortcut.F3, current.QueueDay, new DailyPlan(2, 0)), default);
        var reopened = Runtime(paths, clock);
        var resumed = await reopened.Coordinator.GetNextAsync(new DailyPlan(2, 0), default);

        Assert.IsType<StorageFailure<LearningTransition>>(result);
        Assert.Null(await reopened.Learning.GetLatestUndoableEventAsync(default));
        Assert.Null(await reopened.Learning.GetCardAsync(current.Card.Id, default));
        Assert.Equal(current.QueueItemId, resumed!.QueueItemId);
    }

    private RuntimeSet Runtime(DatabasePaths paths, TimeProvider clock, Action<LearningCommitStage>? fault = null)
    {
        var factory = new SqliteConnectionFactory(paths.User, paths.Corpus, paths.Corpus);
        var learning = new SqliteLearningStore(factory, fault);
        var coordinator = new DailyQueueCoordinator(
            learning,
            new SqliteVocabularyRepository(factory),
            new SqliteDailyQueueStore(factory),
            new QueuePolicy(new Fsrs6Scheduler()),
            clock);
        var next = new GetNextCard(coordinator);
        return new(coordinator, learning, new SubmitRating(learning, next, new Fsrs6Scheduler(), clock));
    }

    private async Task<DatabasePaths> CreateDatabasesAsync(int wordCount)
    {
        var user = Path.Combine(directory, "user.sqlite3");
        var corpus = Path.Combine(directory, "corpus.sqlite3");
        await using (var connection = new SqliteConnection($"Data Source={corpus};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE vocabulary(
                    stable_id TEXT PRIMARY KEY,
                    word TEXT NOT NULL,
                    frequency_rank INTEGER,
                    phonetic TEXT NOT NULL,
                    translation_zh_cn TEXT NOT NULL,
                    definition_en TEXT,
                    pos TEXT);
                CREATE TABLE lexical_sense(
                    sense_id TEXT PRIMARY KEY,
                    entry_id TEXT NOT NULL,
                    definition_en TEXT NOT NULL,
                    pos TEXT NOT NULL);
                """;
            await schema.ExecuteNonQueryAsync();
            for (var index = 1; index <= wordCount; index++)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO vocabulary VALUES ($id,$word,$rank,'','','','')";
                insert.Parameters.AddWithValue("$id", Id(index).ToString("D"));
                insert.Parameters.AddWithValue("$word", $"word-{index:000}");
                insert.Parameters.AddWithValue("$rank", index);
                await insert.ExecuteNonQueryAsync();
            }
        }
        await new MigrationRunner(new SqliteConnectionFactory(user, corpus, corpus)).MigrateAsync(default);
        return new(user, corpus);
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed record DatabasePaths(string User, string Corpus);
    private sealed record RuntimeSet(DailyQueueCoordinator Coordinator, SqliteLearningStore Learning, SubmitRating Submit);
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
