using Microsoft.Data.Sqlite;
using WordFlow.Application.Learning;
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

        var result = await store.ApplyAsync(Command(Id(101), review), default);

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
    public async Task Queued_apply_and_undo_commit_event_snapshot_queue_and_session_atomically()
    {
        var factory = await CreateMigratedFactoryAsync(Database("queued.db"));
        var queue = new SqliteDailyQueueStore(factory);
        var learning = new SqliteLearningStore(factory);
        var day = DateOnly.FromDateTime(Now.UtcDateTime);
        var session = await queue.GetOrCreateAsync(new DailyQueueSeed(
            day, new DailyPlan(1, 0), [], [InitialCard().Id], Now), default);
        var item = Assert.Single(session.Items);
        var review = Review(Id(301), InitialCard(), Rating.Good);

        var applied = await learning.ApplyQueuedAsync(Command(Id(401), review), item.ItemId,
            DailyQueueItemStatus.Completed, default);
        await using (var completed = await factory.OpenUserAsync(default))
            Assert.Equal(1L, await ScalarAsync(completed, "SELECT COUNT(*) FROM daily_sessions WHERE completed_at_utc IS NOT NULL"));
        var undo = await learning.UndoQueuedAsync(
            new UndoLearningCommand(Id(402), Id(302), Now.AddMinutes(1)), review.EventId, default);

        Assert.True(applied.Applied);
        Assert.Equal(day, applied.QueueDay);
        Assert.True(undo.Applied);
        Assert.Equal(review.Before, undo.Card);
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
        Assert.Equal(1L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM daily_queue_items WHERE item_id='{item.ItemId:D}' AND status='Pending' AND completed_event_id IS NULL AND completed_at_utc IS NULL"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM daily_sessions WHERE completed_at_utc IS NULL"));
    }

    [Fact]
    public async Task Daily_statistics_use_the_durable_queue_local_day_instead_of_UTC_midnight()
    {
        var factory = await CreateMigratedFactoryAsync(Database("daily-local-day.db"));
        var queue = new SqliteDailyQueueStore(factory);
        var learning = new SqliteLearningStore(factory);
        var localDay = new DateOnly(2026, 8, 14);
        var occurredAt = new DateTimeOffset(2026, 8, 13, 16, 30, 0, TimeSpan.Zero);
        var card = new CardState(Id(901), null, occurredAt);
        var session = await queue.GetOrCreateAsync(new DailyQueueSeed(
            localDay, new DailyPlan(1, 0), [], [card.Id], occurredAt), default);
        var review = new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(occurredAt))
            .Review(Id(902), card, Rating.Good);

        await learning.ApplyQueuedAsync(
            new LearningCommand(Id(903), review, CardProjection.InitialRevision),
            Assert.Single(session.Items).ItemId,
            DailyQueueItemStatus.Completed,
            default);

        Assert.Equal(1, (await learning.GetDailyStatisticsAsync(localDay, default)).ReviewedToday);
        Assert.Equal(0, (await learning.GetDailyStatisticsAsync(localDay.AddDays(-1), default)).ReviewedToday);
    }

    [Fact]
    public async Task Daily_statistics_ignore_undone_queue_items_and_non_queue_restore_events()
    {
        var factory = await CreateMigratedFactoryAsync(Database("daily-effective.db"));
        var queue = new SqliteDailyQueueStore(factory);
        var learning = new SqliteLearningStore(factory);
        var day = new DateOnly(2026, 8, 13);
        var queueCard = InitialCard();
        var session = await queue.GetOrCreateAsync(new DailyQueueSeed(
            day, new DailyPlan(1, 0), [], [queueCard.Id], Now), default);
        var review = Review(Id(904), queueCard, Rating.Good);
        await learning.ApplyQueuedAsync(Command(Id(905), review), Assert.Single(session.Items).ItemId,
            DailyQueueItemStatus.Completed, default);
        await learning.UndoQueuedAsync(
            new UndoLearningCommand(Id(906), Id(907), Now.AddMinutes(1)), review.EventId, default);

        var restoredCard = new CardState(Id(908), null, Now);
        var actions = new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(Now.AddMinutes(2)));
        var slash = actions.Slash(Id(909), restoredCard);
        await learning.ApplyAsync(new LearningCommand(Id(910), slash, CardProjection.InitialRevision), default);
        var restore = actions.Restore(Id(911), slash.After, RestoreMode.Scheduled);
        await learning.ApplyAsync(new LearningCommand(Id(912), restore, slash.EventId), default);

        var statistics = await learning.GetDailyStatisticsAsync(day, default);

        Assert.Equal(0, statistics.ReviewedToday);
        Assert.Equal(0, statistics.SlashedTotal);
    }

    [Fact]
    public async Task Queued_undo_targets_the_committed_event_and_actual_queue_day_after_midnight()
    {
        var factory = await CreateMigratedFactoryAsync(Database("queued-midnight-undo.db"));
        var queue = new SqliteDailyQueueStore(factory);
        var learning = new SqliteLearningStore(factory);
        var dayOne = DateOnly.FromDateTime(Now.UtcDateTime);
        var dayTwo = dayOne.AddDays(1);
        var firstSession = await queue.GetOrCreateAsync(new DailyQueueSeed(
            dayOne, new DailyPlan(1, 0), [], [Id(1)], Now), default);
        var firstReview = Review(Id(311), InitialCard(), Rating.Good);
        await learning.ApplyQueuedAsync(Command(Id(411), firstReview), firstSession.Items[0].ItemId,
            DailyQueueItemStatus.Completed, default);
        var secondCard = new CardState(Id(2), null, Now.AddDays(1));
        var secondSession = await queue.GetOrCreateAsync(new DailyQueueSeed(
            dayTwo, new DailyPlan(1, 0), [], [Id(2)], Now.AddDays(1)), default);
        var secondReview = new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(Now.AddDays(1)))
            .Review(Id(312), secondCard, Rating.Good);
        await learning.ApplyQueuedAsync(new LearningCommand(Id(412), secondReview, CardProjection.InitialRevision),
            secondSession.Items[0].ItemId, DailyQueueItemStatus.Completed, default);

        var undo = await learning.UndoQueuedAsync(
            new UndoLearningCommand(Id(413), Id(313), Now.AddDays(1).AddMinutes(1)),
            firstReview.EventId,
            default);

        Assert.Equal(firstReview.Before, undo.Card);
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(connection,
            $"SELECT COUNT(*) FROM daily_queue_items WHERE completed_event_id IS NULL AND status='Pending' AND item_id='{firstSession.Items[0].ItemId:D}'"));
        Assert.Equal(1L, await ScalarAsync(connection,
            $"SELECT COUNT(*) FROM daily_queue_items WHERE completed_event_id='{secondReview.EventId:D}' AND status='Completed'"));
        Assert.Equal(1L, await ScalarAsync(connection,
            $"SELECT COUNT(*) FROM daily_sessions WHERE local_day='{dayOne:yyyy-MM-dd}' AND completed_at_utc IS NULL"));
        Assert.Equal(1L, await ScalarAsync(connection,
            $"SELECT COUNT(*) FROM daily_sessions WHERE local_day='{dayTwo:yyyy-MM-dd}' AND completed_at_utc IS NOT NULL"));
    }

    [Fact]
    public async Task Queued_apply_failure_after_event_write_rolls_back_queue_event_and_snapshot()
    {
        var factory = await CreateMigratedFactoryAsync(Database("queued-rollback.db"));
        var queue = new SqliteDailyQueueStore(factory);
        var session = await queue.GetOrCreateAsync(new DailyQueueSeed(
            DateOnly.FromDateTime(Now.UtcDateTime), new DailyPlan(1, 0), [], [InitialCard().Id], Now), default);
        var item = Assert.Single(session.Items);
        var learning = new SqliteLearningStore(factory, stage =>
        {
            if (stage == LearningCommitStage.EventWritten) throw new InjectedFailureException();
        });

        await Assert.ThrowsAsync<InjectedFailureException>(() => learning.ApplyQueuedAsync(
            Command(Id(403), Review(Id(303), InitialCard(), Rating.Good)), item.ItemId,
            DailyQueueItemStatus.Completed, default));

        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM card_state"));
        Assert.Equal(1L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM daily_queue_items WHERE item_id='{item.ItemId:D}' AND status='Pending'"));
    }

    [Theory]
    [InlineData(LearningCommitStage.CardStateProjected)]
    [InlineData(LearningCommitStage.QueueItemCompleted)]
    [InlineData(LearningCommitStage.SessionCompletionRecomputed)]
    public async Task Queued_apply_late_failure_rolls_back_event_card_queue_and_session(
        LearningCommitStage failureStage)
    {
        var factory = await CreateMigratedFactoryAsync(Database($"queued-late-{failureStage}.db"));
        var queue = new SqliteDailyQueueStore(factory);
        var session = await queue.GetOrCreateAsync(new DailyQueueSeed(
            DateOnly.FromDateTime(Now.UtcDateTime), new DailyPlan(1, 0), [], [InitialCard().Id], Now), default);
        var item = Assert.Single(session.Items);
        var learning = new SqliteLearningStore(factory, stage =>
        {
            if (stage == failureStage) throw new InjectedFailureException();
        });

        await Assert.ThrowsAsync<InjectedFailureException>(() => learning.ApplyQueuedAsync(
            Command(Id(404), Review(Id(304), InitialCard(), Rating.Good)), item.ItemId,
            DailyQueueItemStatus.Completed, default));

        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM card_state"));
        Assert.Equal(1L, await ScalarAsync(connection,
            $"SELECT COUNT(*) FROM daily_queue_items WHERE item_id='{item.ItemId:D}' AND status='Pending' AND completed_event_id IS NULL AND completed_at_utc IS NULL"));
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM daily_sessions WHERE completed_at_utc IS NULL AND updated_at_utc=created_at_utc"));
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
            () => store.ApplyAsync(Command(Id(102), review), default));

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

        var committed = await store.ApplyAsync(Command(Id(103), first), default);
        var retried = await store.ApplyAsync(Command(Id(103), conflictingRetry), default);

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

        await store.ApplyAsync(Command(Id(104), original), default);
        await store.ApplyAsync(Command(Id(105), undo, original.EventId), default);

        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
        Assert.Equal(Id(15).ToString("D"), await TextAsync(connection,
            "SELECT compensates_event_id FROM review_event WHERE action = 'Undo'"));
        Assert.Equal(original.Before, (await store.GetCardAsync(original.CardId, default))!);
    }

    [Fact]
    public async Task Application_reads_expose_idempotent_commit_card_pages_and_latest_undoable_event()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        var commandId = Id(140);
        var review = Review(Id(40), InitialCard(), Rating.Good);
        await store.ApplyAsync(Command(commandId, review), default);

        var commit = await store.GetCommitAsync(commandId, default);
        var cards = await store.GetCardsAsync(new PageRequest(0, 500), default);
        var latest = await store.GetLatestUndoableEventAsync(default);

        Assert.False(commit!.Applied);
        Assert.Equal(review.EventId, commit.EventId);
        Assert.Equal(review.After, Assert.Single(cards.Items));
        Assert.Equal(review, latest);

        var undo = new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(Now.AddMinutes(1))).Undo(Id(41), review);
        await store.ApplyAsync(Command(Id(141), undo, review.EventId), default);
        Assert.Null(await store.GetLatestUndoableEventAsync(default));
    }

    [Fact]
    public async Task Atomic_undo_selects_deterministic_latest_uncompensated_event_across_cards()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        var first = Review(Id(50), InitialCard(), Rating.Good);
        var secondCard = new CardState(Id(2), null, Now);
        var second = Review(Id(51), secondCard, Rating.Hard);
        await store.ApplyAsync(Command(Id(150), first), default);
        await store.ApplyAsync(Command(Id(151), second), default);

        var undoSecond = await store.UndoLatestAsync(new UndoLearningCommand(Id(152), Id(52), Now.AddMinutes(1)), default);
        var undoFirst = await store.UndoLatestAsync(new UndoLearningCommand(Id(153), Id(53), Now.AddMinutes(2)), default);

        Assert.Equal(second.Before, undoSecond.Card);
        Assert.Equal(first.Before, undoFirst.Card);
        await using var connection = await factory.OpenUserAsync(default);
        var compensated = await StringsAsync(connection, "SELECT compensates_event_id FROM review_event WHERE action='Undo' ORDER BY rowid");
        Assert.Equal(new[] { second.EventId.ToString("D"), first.EventId.ToString("D") }, compensated);
    }

    [Fact]
    public async Task Consecutive_same_card_undo_follows_compensation_lineage_append_only()
    {
        var factory = await CreateMigratedFactoryAsync(Database("same-card-undo.db"));
        var store = new SqliteLearningStore(factory);
        var first = Review(Id(54), InitialCard(), Rating.Good);
        var second = Review(Id(55), first.After, Rating.Hard) with { OccurredAt = first.OccurredAt };
        await store.ApplyAsync(new LearningCommand(Id(154), first, CardProjection.InitialRevision), default);
        await store.ApplyAsync(new LearningCommand(Id(155), second, first.EventId), default);

        var undoSecond = await store.UndoLatestAsync(new UndoLearningCommand(Id(156), Id(56), Now.AddMinutes(1)), default);
        var undoFirst = await store.UndoLatestAsync(new UndoLearningCommand(Id(157), Id(57), Now.AddMinutes(2)), default);

        Assert.Equal(first.After, undoSecond.Card);
        Assert.Equal(first.Before, undoFirst.Card);
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(4L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
        Assert.Equal(
            new[] { second.EventId.ToString("D"), first.EventId.ToString("D") },
            await StringsAsync(connection, "SELECT compensates_event_id FROM review_event WHERE action='Undo' ORDER BY rowid"));
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event WHERE action='Undo'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_displayed_rating_or_slash_conflicts_without_new_event(bool slash)
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        var displayed = InitialCard();
        var intervening = Review(Id(60), displayed, Rating.Good);
        await store.ApplyAsync(Command(Id(160), intervening), default);
        var staleEvent = slash
            ? new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(Now.AddMinutes(1))).Slash(Id(61), displayed)
            : Review(Id(61), displayed, Rating.Hard);

        await Assert.ThrowsAsync<LearningConcurrencyException>(() =>
            store.ApplyAsync(Command(Id(161), staleEvent), default));

        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
    }

    [Fact]
    public async Task Non_idempotent_event_identity_constraint_propagates_as_sqlite_error()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        var first = Review(Id(70), InitialCard(), Rating.Good);
        await store.ApplyAsync(Command(Id(170), first), default);
        var reusedIdentity = Review(first.EventId, first.After, Rating.Hard);

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            store.ApplyAsync(Command(Id(171), reusedIdentity, first.EventId), default));

        Assert.Equal(19, exception.SqliteErrorCode);
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
    }

    [Fact]
    public async Task Malformed_learning_schema_propagates_as_sqlite_error()
    {
        var database = Database("malformed.db");
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "CREATE TABLE review_event(unexpected TEXT)");
        }
        var store = new SqliteLearningStore(new SqliteConnectionFactory(database));

        var exception = await Assert.ThrowsAsync<SqliteException>(() => store.GetCommitAsync(Id(180), default));

        Assert.Equal(1, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task Corrupt_database_propagates_as_sqlite_error()
    {
        var database = Database("corrupt.db");
        await File.WriteAllBytesAsync(database, "not a sqlite database"u8.ToArray());
        var store = new SqliteLearningStore(new SqliteConnectionFactory(database));

        var exception = await Assert.ThrowsAsync<SqliteException>(() => store.GetCommitAsync(Id(181), default));

        Assert.DoesNotContain(exception.SqliteErrorCode, new[] { 5, 6, 10, 13, 14, 15 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABA_display_revision_rejects_stale_rating_and_slash(bool slash)
    {
        var factory = await CreateMigratedFactoryAsync(Database("aba.db"));
        var store = new SqliteLearningStore(factory);
        var displayed = new CardProjection(InitialCard(), CardProjection.InitialRevision);
        var learned = Review(Id(80), displayed.Card, Rating.Good);
        await store.ApplyAsync(new LearningCommand(Id(180), learned, displayed.Revision), default);
        await store.UndoLatestAsync(new UndoLearningCommand(Id(181), Id(81), Now.AddMinutes(1)), default);
        var stale = slash
            ? new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(Now.AddMinutes(2))).Slash(Id(82), displayed.Card)
            : Review(Id(82), displayed.Card, Rating.Hard);

        await Assert.ThrowsAsync<LearningConcurrencyException>(() =>
            store.ApplyAsync(new LearningCommand(Id(182), stale, displayed.Revision), default));

        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
    }

    [Fact]
    public async Task Locked_writer_is_translated_across_begin_stage_and_retry_remains_idempotent()
    {
        var database = Database("locked.db");
        var factory = await CreateMigratedFactoryAsync(database);
        var store = new SqliteLearningStore(new SqliteConnectionFactory(database, busyTimeoutMilliseconds: 1));
        var command = Command(Id(190), Review(Id(90), InitialCard(), Rating.Good));
        await using var blocker = await factory.OpenUserAsync(default);
        await using var transaction = blocker.BeginTransaction(deferred: false);
        await Assert.ThrowsAsync<TransientStorageException>(() => store.ApplyAsync(command, default));
        await transaction.RollbackAsync();
        var applied = await store.ApplyAsync(command, default);
        var retry = await store.ApplyAsync(command, default);

        Assert.True(applied.Applied);
        Assert.False(retry.Applied);
    }

    [Fact]
    public async Task Cannot_open_read_is_translated_but_cancellation_still_propagates()
    {
        var directoryPath = Path.Combine(directory, "not-a-database");
        Directory.CreateDirectory(directoryPath);
        var store = new SqliteLearningStore(new SqliteConnectionFactory(directoryPath));

        await Assert.ThrowsAsync<TransientStorageException>(() => store.GetCommitAsync(Id(191), default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetCommitAsync(Id(191), cancelled.Token));
    }

    [Fact]
    public async Task Concurrent_atomic_undo_retry_is_idempotent()
    {
        var factory = await CreateMigratedFactoryAsync(Database("undo-retry.db"));
        var store = new SqliteLearningStore(factory);
        await store.ApplyAsync(Command(Id(200), Review(Id(100), InitialCard(), Rating.Good)), default);
        var command = new UndoLearningCommand(Id(201), Id(101), Now.AddMinutes(1));

        var results = await Task.WhenAll(store.UndoLatestAsync(command, default), store.UndoLatestAsync(command, default));

        Assert.Single(results, x => x.Applied);
        Assert.Single(results, x => !x.Applied);
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
    }

    [Fact]
    public async Task Different_card_writer_at_undo_selection_serializes_and_becomes_next_latest()
    {
        var database = Database("undo-writer-barrier.db");
        var factory = await CreateMigratedFactoryAsync(database);
        var seed = new SqliteLearningStore(factory);
        var selected = Review(Id(102), InitialCard(), Rating.Good);
        await seed.ApplyAsync(Command(Id(202), selected), default);
        Task<CommitResult>? writer = null;
        using var attempted = new ManualResetEventSlim();
        var undoStore = new SqliteLearningStore(factory, stage =>
        {
            if (stage != LearningCommitStage.UndoEventSelected || writer is not null) return;
            var other = Review(Id(103), new CardState(Id(2), null, Now), Rating.Hard) with { OccurredAt = Now.AddMinutes(5) };
            writer = Task.Run(async () =>
            {
                attempted.Set();
                return await new SqliteLearningStore(factory).ApplyAsync(Command(Id(203), other), default);
            });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
        });

        var firstUndo = await undoStore.UndoLatestAsync(new UndoLearningCommand(Id(204), Id(104), Now.AddMinutes(1)), default);
        var writerResult = await writer!;
        var secondUndo = await seed.UndoLatestAsync(new UndoLearningCommand(Id(205), Id(105), Now.AddMinutes(6)), default);

        Assert.Equal(selected.Before, firstUndo.Card);
        Assert.True(writerResult.Applied);
        Assert.Equal(Id(2), secondUndo.Card.Id);
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(
            new[] { selected.EventId.ToString("D"), Id(103).ToString("D") },
            await StringsAsync(connection, "SELECT compensates_event_id FROM review_event WHERE action='Undo' ORDER BY rowid"));
    }

    [Fact]
    public async Task Card_page_rows_and_snapshot_token_share_one_read_transaction()
    {
        var factory = await CreateMigratedFactoryAsync(Database("coherent-read.db"));
        var seed = new SqliteLearningStore(factory);
        await seed.ApplyAsync(Command(Id(210), Review(Id(110), InitialCard(), Rating.Good)), default);
        var injected = false;
        var store = new SqliteLearningStore(factory, stage =>
        {
            if (stage != LearningCommitStage.CardPageRowsRead || injected) return;
            injected = true;
            var other = new CardState(Id(2), null, Now);
            var review = Review(Id(111), other, Rating.Good);
            new SqliteLearningStore(factory).ApplyAsync(Command(Id(211), review), default).GetAwaiter().GetResult();
        });

        var first = await store.GetCardsAsync(new PageRequest(0, 500), default);
        var second = await seed.GetCardsAsync(new PageRequest(0, 500), default);

        Assert.Single(first.Items);
        Assert.Equal("cards:1:1", first.SnapshotId);
        Assert.Equal(2, second.Items.Count);
        Assert.Equal("cards:2:2", second.SnapshotId);
    }

    [Fact]
    public async Task Stale_command_is_rejected_and_does_not_append_an_event()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        var first = Review(Id(17), InitialCard(), Rating.Good);
        var stale = Review(Id(18), InitialCard(), Rating.Hard);
        await store.ApplyAsync(Command(Id(106), first), default);

        await Assert.ThrowsAsync<LearningConcurrencyException>(
            () => store.ApplyAsync(Command(Id(107), stale), default));

        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event"));
    }

    [Fact]
    public async Task Event_rows_cannot_be_updated_or_deleted_even_by_direct_sql()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        await store.ApplyAsync(Command(Id(109), Review(Id(20), InitialCard(), Rating.Good)), default);
        await using var connection = await factory.OpenUserAsync(default);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE review_event SET action='Hard'"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM review_event"));
    }

    [Fact]
    public async Task Conflict_replace_cannot_bypass_event_or_projection_immutability()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        var slash = new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(Now)).Slash(Id(25), InitialCard());
        await store.ApplyAsync(Command(Id(125), slash), default);
        await using var connection = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(connection, "PRAGMA recursive_triggers"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "INSERT OR REPLACE INTO review_event SELECT event_id,command_id,card_id,occurred_at_utc,'Undo',compensates_event_id,before_json,after_json FROM review_event"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "REPLACE INTO slash_event SELECT event_id,card_id,'Undo',occurred_at_utc FROM slash_event"));

        Assert.Equal("Slash", await TextAsync(connection, "SELECT action FROM review_event"));
        Assert.Equal("Slash", await TextAsync(connection, "SELECT action FROM slash_event"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM review_event r JOIN slash_event s ON s.event_id=r.event_id AND s.action=r.action"));
    }

    [Fact]
    public async Task Concurrent_retries_of_one_command_converge_to_one_committed_event()
    {
        var factory = await CreateMigratedFactoryAsync(Database("user.db"));
        var store = new SqliteLearningStore(factory);
        var command = Command(Id(110), Review(Id(21), InitialCard(), Rating.Good));

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
            store.ApplyAsync(Command(Id(111), Review(Id(22), InitialCard(), Rating.Good)), cancellation.Token));

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
            "daily_sessions", "daily_queue_items",
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
            new SqliteMigration(5, "broken", "CREATE TABLE should_rollback(id INTEGER); INSERT INTO missing_table VALUES (1);")
        });

        await Assert.ThrowsAsync<SqliteException>(() => runner.MigrateAsync(default));

        await using var verify = await factory.OpenUserAsync(default);
        Assert.Equal("dark", await TextAsync(verify, "SELECT value FROM app_setting WHERE key='theme'"));
        Assert.Equal(0L, await ScalarAsync(verify,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='should_rollback'"));
        Assert.Equal(4L, await ScalarAsync(verify, "SELECT MAX(version) FROM schema_version"));
        Assert.Equal(4L, await ScalarAsync(verify, "SELECT COUNT(*) FROM schema_version"));
        Assert.Single(Directory.GetFiles(directory, "user.db.v4.*.backup"));
    }

    [Fact]
    public async Task Existing_version_one_database_is_hardened_by_version_two_migration()
    {
        var database = Database("upgrade.db");
        var factory = new SqliteConnectionFactory(database);
        var initialSql = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "src", "WordFlow.Infrastructure", "Data", "Migrations", "001_initial.sql"));
        await new MigrationRunner(factory, [new SqliteMigration(1, "initial", initialSql)]).MigrateAsync(default);
        await using (var before = await factory.OpenUserAsync(default))
            Assert.Equal(1L, await ScalarAsync(before, "SELECT MAX(version) FROM schema_version"));

        await new MigrationRunner(factory).MigrateAsync(default);

        await using var after = await factory.OpenUserAsync(default);
        Assert.Equal(4L, await ScalarAsync(after, "SELECT MAX(version) FROM schema_version"));
        Assert.Equal(6L, await ScalarAsync(after, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('fsrs_parameter_snapshot_insert_identity','fsrs_parameter_snapshot_update_guard','fsrs_parameter_snapshot_delete_guard','review_event_insert_identity','slash_event_insert_identity','fsrs_parameter_activation_insert_identity')"));
        Assert.Single(Directory.GetFiles(directory, "upgrade.db.v1.*.backup"));
    }

    [Fact]
    public async Task Version_two_upgrade_rejects_preexisting_snapshot_row_json_identity_mismatch()
    {
        var database = Database("corrupt-upgrade.db");
        var factory = new SqliteConnectionFactory(database);
        var initialSql = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "src", "WordFlow.Infrastructure", "Data", "Migrations", "001_initial.sql"));
        await new MigrationRunner(factory, [new SqliteMigration(1, "initial", initialSql)]).MigrateAsync(default);
        await using (var connection = await factory.OpenUserAsync(default))
        {
            await ExecuteAsync(connection, $"INSERT INTO fsrs_parameter_snapshot VALUES ('{Id(70):D}','PendingPreview','{{\"Id\":\"{Id(71):D}\",\"Status\":\"PendingPreview\",\"CreatedAt\":\"{Now.UtcDateTime:O}\"}}','{Now.UtcDateTime:O}')");
        }

        await Assert.ThrowsAsync<SqliteException>(() => new MigrationRunner(factory).MigrateAsync(default));

        await using var verify = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(verify, "SELECT MAX(version) FROM schema_version"));
        Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='fsrs_parameter_snapshot_insert_consistency'"));
        Assert.Single(Directory.GetFiles(directory, "corrupt-upgrade.db.v1.*.backup"));
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("Status")]
    [InlineData("CreatedAt")]
    public async Task Version_two_upgrade_rejects_preexisting_snapshot_missing_required_json_path(string path)
    {
        var database = Database($"missing-{path}.db");
        var factory = new SqliteConnectionFactory(database);
        var initialSql = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "src", "WordFlow.Infrastructure", "Data", "Migrations", "001_initial.sql"));
        await new MigrationRunner(factory, [new SqliteMigration(1, "initial", initialSql)]).MigrateAsync(default);
        var snapshotId = Id(72);
        var json = $"{{\"Id\":\"{snapshotId:D}\",\"Status\":\"PendingPreview\",\"CreatedAt\":\"{Now.UtcDateTime:O}\"}}";
        await using (var connection = await factory.OpenUserAsync(default))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO fsrs_parameter_snapshot VALUES ($id,'PendingPreview',json_remove($json,$path),$at)";
            command.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$path", $"$.{path}");
            command.Parameters.AddWithValue("$at", Now.UtcDateTime.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => new MigrationRunner(factory).MigrateAsync(default));

        await using var verify = await factory.OpenUserAsync(default);
        Assert.Equal(1L, await ScalarAsync(verify, "SELECT MAX(version) FROM schema_version"));
        Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='fsrs_parameter_snapshot_insert_consistency'"));
        Assert.Equal(1L, await ScalarAsync(verify, $"SELECT COUNT(*) FROM fsrs_parameter_snapshot WHERE snapshot_id='{snapshotId:D}' AND json_type(snapshot_json,'$.{path}') IS NULL"));
        Assert.Single(Directory.GetFiles(directory, $"missing-{path}.db.v1.*.backup"));
    }

    private async Task<SqliteConnectionFactory> CreateMigratedFactoryAsync(string userDatabase)
    {
        var factory = new SqliteConnectionFactory(userDatabase);
        await new MigrationRunner(factory).MigrateAsync(default);
        return factory;
    }

    private static ReviewEvent Review(Guid eventId, CardState before, Rating rating) =>
        new LearningActions(new Fsrs6Scheduler(), new FixedTimeProvider(Now)).Review(eventId, before, rating);

    private static LearningCommand Command(Guid commandId, ReviewEvent @event, Guid? revision = null) =>
        new(commandId, @event, revision ?? CardProjection.InitialRevision);

    private static CardState InitialCard() => new(Id(1), null, Now);

    private static Guid Id(int value) => new($"00000000-0000-0000-0000-{value:D12}");
    private string Database(string name) => Path.Combine(directory, name);

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "WordFlow.sln"))) current = Directory.GetParent(current)?.FullName;
        return current ?? throw new DirectoryNotFoundException();
    }

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

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InjectedFailureException : Exception;
}
