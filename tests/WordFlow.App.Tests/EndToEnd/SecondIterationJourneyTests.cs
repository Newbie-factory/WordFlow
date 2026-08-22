using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WordFlow.App.ViewModels;
using WordFlow.Application;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;
using WordFlow.Infrastructure.Data;

namespace WordFlow.App.Tests.EndToEnd;

public sealed class SecondIterationJourneyTests : IDisposable
{
    private static readonly DateTimeOffset DayOne = new(2026, 8, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly DailyPlan Plan = new(40, 120);
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"WordFlow.SecondIterationJourney.{Guid.NewGuid():N}");

    public SecondIterationJourneyTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Critical_journey_resumes_carries_within_target_and_updates_goal_and_history()
    {
        DatabasePaths paths = await CreateDatabasesAsync(wordCount: 80);
        var clock = new MutableTimeProvider(DayOne, TimeZoneInfo.Utc);
        Guid expectedFourteenthWordId;
        Guid expectedFourteenthQueueItemId;

        await using (ServiceProvider firstRun = CreateRuntime(paths, clock))
        {
            var coordinator = firstRun.GetRequiredService<DailyQueueCoordinator>();
            var submit = firstRun.GetRequiredService<SubmitRating>();
            NextCard current = Assert.IsType<NextCard>(await coordinator.GetNextAsync(Plan, default));

            DailySessionSnapshot created = await firstRun.GetRequiredService<IDailyQueueStore>()
                .GetOrCreateAsync(new DailyQueueSeed(
                    new DateOnly(2026, 8, 22), Plan, [], [], clock.GetUtcNow()), default);
            Assert.Equal(Plan, created.ConfiguredPlan);
            Assert.Equal(new DailyPlan(40, 0), created.EffectivePlan);
            Assert.Equal(40, created.Items.Count(item => item.Kind == DailyQueueItemKind.New));
            Assert.DoesNotContain(created.Items, item => item.Kind == DailyQueueItemKind.Review);

            for (int completed = 0; completed < 13; completed++)
            {
                var result = await submit.HandleAsync(new SubmitRatingRequest(
                    Guid.NewGuid(), Guid.NewGuid(), current.Card, current.Revision,
                    current.QueueItemId, RatingShortcut.F3, current.QueueDay, Plan), default);
                current = Assert.IsType<NextCard>(Assert.IsType<Success<LearningTransition>>(result).Value.NextCard);
            }

            expectedFourteenthWordId = current.Word.WordId;
            expectedFourteenthQueueItemId = current.QueueItemId;
        }

        await using (ServiceProvider reopened = CreateRuntime(paths, clock))
        {
            var coordinator = reopened.GetRequiredService<DailyQueueCoordinator>();
            NextCard resumed = Assert.IsType<NextCard>(await coordinator.GetNextAsync(Plan, default));
            Assert.Equal(expectedFourteenthWordId, resumed.Word.WordId);
            Assert.Equal(expectedFourteenthQueueItemId, resumed.QueueItemId);

            clock.Advance(TimeSpan.FromDays(1));
            NextCard dayTwoCard = Assert.IsType<NextCard>(await coordinator.GetNextAsync(Plan, default));
            DateOnly dayTwo = new(2026, 8, 23);
            DailySessionSnapshot dayTwoQueue = await reopened.GetRequiredService<IDailyQueueStore>()
                .GetOrCreateAsync(new DailyQueueSeed(dayTwo, Plan, [], [], clock.GetUtcNow()), default);

            DailyQueueItem[] dayTwoNewItems = dayTwoQueue.Items
                .Where(item => item.Kind == DailyQueueItemKind.New)
                .ToArray();
            Assert.Equal(40, dayTwoNewItems.Length);
            Assert.Equal(27, dayTwoNewItems.Count(item => item.SourceItemId is not null));
            Assert.Equal(13, dayTwoNewItems.Count(item => item.SourceItemId is null));
            Assert.Equal(expectedFourteenthWordId, dayTwoCard.Word.WordId);

            var queueStore = reopened.GetRequiredService<IDailyQueueStore>();
            GoalProgress beforeSlash = await queueStore.GetGoalProgressAsync(default);
            var slashResult = await reopened.GetRequiredService<SlashWord>().HandleAsync(new SlashWordRequest(
                Guid.NewGuid(), Guid.NewGuid(), dayTwoCard.Card, dayTwoCard.Revision,
                dayTwoCard.QueueItemId, dayTwoCard.QueueDay, Plan), default);
            CardState slashedCard = Assert.IsType<Success<LearningTransition>>(slashResult).Value.Card;
            GoalProgress afterSlash = await queueStore.GetGoalProgressAsync(default);
            Assert.Equal(beforeSlash.SlashedWords + 1, afterSlash.SlashedWords);

            CardProjection projection = Assert.IsType<CardProjection>(
                await reopened.GetRequiredService<ILearningStore>()
                    .GetCardProjectionAsync(slashedCard.Id, default));
            var restoreResult = await reopened.GetRequiredService<RestoreSlashedWords>().HandleAsync(
                new RestoreSlashedWordRequest(
                    Guid.NewGuid(), Guid.NewGuid(), projection.Card.Id, projection.Revision, RestoreMode.Scheduled),
                default);
            Assert.IsType<Success<CardState>>(restoreResult);
            GoalProgress afterRestore = await queueStore.GetGoalProgressAsync(default);
            Assert.Equal(beforeSlash.SlashedWords, afterRestore.SlashedWords);

            var history = new LearningHistoryViewModel(queueStore, clock);
            await history.LoadAsync(default);
            Assert.Equal(183, history.Days.Count);
            Assert.Equal(dayTwo.AddDays(-182), history.Days[0].Day);
            Assert.Equal(dayTwo, history.Days[^1].Day);
        }
    }

    private static ServiceProvider CreateRuntime(DatabasePaths paths, TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SqliteConnectionFactory(paths.User, paths.Corpus, paths.Corpus));
        services.AddSingleton<SqliteLearningStore>();
        services.AddSingleton<ILearningStore>(provider => provider.GetRequiredService<SqliteLearningStore>());
        services.AddSingleton<SqliteDailyQueueStore>();
        services.AddSingleton<IDailyQueueStore>(provider => provider.GetRequiredService<SqliteDailyQueueStore>());
        services.AddSingleton<IVocabularyRepository, SqliteVocabularyRepository>();
        services.AddSingleton(clock);
        services.AddSingleton<IFsrsScheduler, Fsrs6Scheduler>();
        services.AddSingleton<QueuePolicy>();
        services.AddSingleton<DailyQueueCoordinator>();
        services.AddSingleton<GetNextCard>();
        services.AddSingleton<SubmitRating>();
        services.AddSingleton<SlashWord>();
        services.AddSingleton<RestoreSlashedWords>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task<DatabasePaths> CreateDatabasesAsync(int wordCount)
    {
        string user = Path.Combine(directory, "user.sqlite3");
        string corpus = Path.Combine(directory, "corpus.sqlite3");
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
            for (int index = 1; index <= wordCount; index++)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO vocabulary VALUES ($id,$word,$rank,'','','','')";
                insert.Parameters.AddWithValue("$id", Id(index).ToString("D"));
                insert.Parameters.AddWithValue("$word", $"word-{index:000}");
                insert.Parameters.AddWithValue("$rank", index);
                await insert.ExecuteNonQueryAsync();
            }
        }

        var factory = new SqliteConnectionFactory(user, corpus, corpus);
        await new MigrationRunner(factory).MigrateAsync(default);
        return new DatabasePaths(user, corpus);
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed record DatabasePaths(string User, string Corpus);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => localTimeZone;
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan amount) => utcNow = utcNow.Add(amount);
    }
}
