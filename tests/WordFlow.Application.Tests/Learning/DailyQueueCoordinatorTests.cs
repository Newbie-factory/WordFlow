using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Application.Tests.Learning;

public sealed class DailyQueueCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reopening_after_thirteen_of_forty_returns_the_exact_persisted_fourteenth_card()
    {
        var clock = new MutableTimeProvider(Now);
        var queue = new DurableQueueStore();
        var learning = new DurableLearningStore(queue);
        var vocabulary = new FakeVocabularyRepository(Enumerable.Range(1, 40).Select(Word));
        var firstRun = UseCases(learning, queue, vocabulary, clock);
        var current = await firstRun.Coordinator.GetNextAsync(new DailyPlan(40, 0), default);

        for (var index = 0; index < 13; index++)
        {
            var result = await firstRun.Submit.HandleAsync(new(
                Guid.NewGuid(), Guid.NewGuid(), current!.Card, current.Revision,
                current.QueueItemId, RatingShortcut.F3, current.QueueDay, new DailyPlan(40, 0)), default);
            current = Assert.IsType<Success<LearningTransition>>(result).Value.NextCard;
        }

        var expected = current!;
        var reopened = UseCases(learning, queue, vocabulary, clock);
        var resumed = await reopened.Coordinator.GetNextAsync(new DailyPlan(40, 0), default);

        Assert.Equal(expected.Word.WordId, resumed!.Word.WordId);
        Assert.Equal(expected.QueueItemId, resumed.QueueItemId);
        Assert.Equal(DailyQueueItemKind.New, resumed.QueueKind);
    }

    [Fact]
    public async Task Persisted_session_resolves_its_current_item_without_enumerating_queue_candidates()
    {
        var clock = new MutableTimeProvider(Now);
        var queue = new DurableQueueStore();
        var learning = new DurableLearningStore(queue);
        var vocabulary = new FakeVocabularyRepository([Word(1), Word(2)]);
        var firstRun = UseCases(learning, queue, vocabulary, clock);
        var persisted = await firstRun.Coordinator.GetNextAsync(new DailyPlan(2, 0), default);
        var reopened = UseCases(learning, queue, new CandidateEnumerationFailureVocabulary(vocabulary), clock);

        var resumed = await reopened.Coordinator.GetNextAsync(new DailyPlan(99, 99), default);

        Assert.Equal(persisted!.QueueItemId, resumed!.QueueItemId);
        Assert.Equal(persisted.Word.WordId, resumed.Word.WordId);
    }

    [Fact]
    public async Task Failed_atomic_commit_keeps_the_captured_item_current_and_does_not_read_a_successor()
    {
        var clock = new MutableTimeProvider(Now);
        var queue = new DurableQueueStore();
        var learning = new DurableLearningStore(queue);
        var useCases = UseCases(learning, queue, new FakeVocabularyRepository([Word(1), Word(2)]), clock);
        var current = await useCases.Coordinator.GetNextAsync(new DailyPlan(2, 0), default);
        var readsBeforeCommit = queue.PendingReads;
        learning.ApplyQueuedFailure = new TransientStorageException("injected failure after event insertion", new IOException());

        var result = await useCases.Submit.HandleAsync(new(
            Guid.NewGuid(), Guid.NewGuid(), current!.Card, current.Revision,
            current.QueueItemId, RatingShortcut.F3, current.QueueDay, new DailyPlan(2, 0)), default);

        Assert.IsType<StorageFailure<LearningTransition>>(result);
        Assert.Equal(readsBeforeCommit, queue.PendingReads);
        Assert.Empty(learning.Events);
        var resumed = await useCases.Coordinator.GetNextAsync(new DailyPlan(2, 0), default);
        Assert.Equal(current.QueueItemId, resumed!.QueueItemId);
    }

    [Fact]
    public async Task Undo_restores_the_prior_queue_row_as_the_current_pending_card()
    {
        var clock = new MutableTimeProvider(Now);
        var queue = new DurableQueueStore();
        var learning = new DurableLearningStore(queue);
        var useCases = UseCases(learning, queue, new FakeVocabularyRepository([Word(1), Word(2)]), clock);
        var first = await useCases.Coordinator.GetNextAsync(new DailyPlan(2, 0), default);
        var eventId = Guid.NewGuid();
        var rated = await useCases.Submit.HandleAsync(new(
            Guid.NewGuid(), eventId, first!.Card, first.Revision,
            first.QueueItemId, RatingShortcut.F3, first.QueueDay, new DailyPlan(2, 0)), default);
        Assert.NotEqual(first.QueueItemId, Assert.IsType<Success<LearningTransition>>(rated).Value.NextCard!.QueueItemId);

        var undone = await useCases.Undo.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), eventId), default);
        var restored = await useCases.Coordinator.GetNextAsync(new DailyPlan(2, 0), default);

        Assert.IsType<Success<CardState>>(undone);
        Assert.Equal(first.Word.WordId, restored!.Word.WordId);
        Assert.Equal(first.QueueItemId, restored.QueueItemId);
    }

    [Fact]
    public async Task Immediate_undo_after_local_midnight_restores_the_event_on_its_actual_queue_day()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+08-undo", TimeSpan.FromHours(8), "UTC+08-undo", "UTC+08-undo");
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 13, 15, 59, 0, TimeSpan.Zero), zone);
        var queue = new DurableQueueStore();
        var learning = new DurableLearningStore(queue);
        var useCases = UseCases(learning, queue, new FakeVocabularyRepository([Word(1)]), clock);
        var displayed = Assert.IsType<NextCard>(
            await useCases.Coordinator.GetNextAsync(new DailyPlan(1, 0), default));
        var eventId = Guid.NewGuid();
        var rated = await useCases.Submit.HandleAsync(new(
            Guid.NewGuid(), eventId, displayed.Card, displayed.Revision,
            displayed.QueueItemId, RatingShortcut.F3, displayed.QueueDay, new DailyPlan(1, 0)), default);
        Assert.Equal(new DateOnly(2026, 8, 13),
            Assert.IsType<Success<LearningTransition>>(rated).Value.CommittedQueueDay);

        clock.UtcNow = new DateTimeOffset(2026, 8, 13, 16, 1, 0, TimeSpan.Zero);
        var undone = await useCases.Undo.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), eventId), default);
        var restored = await useCases.Coordinator.GetNextAsync(new DailyPlan(1, 0), default);

        Assert.IsType<Success<CardState>>(undone);
        Assert.Equal(displayed.Card.Id, restored!.Card.Id);
        Assert.Equal(new DateOnly(2026, 8, 14), restored.QueueDay);
        Assert.Equal(displayed.QueueItemId,
            queue.Sessions[new DateOnly(2026, 8, 14)].Items.Single().SourceItemId);
    }

    [Fact]
    public async Task Local_day_rollover_creates_then_restores_the_new_days_carried_session()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+08-test", TimeSpan.FromHours(8), "UTC+08-test", "UTC+08-test");
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 13, 15, 59, 0, TimeSpan.Zero), zone);
        var queue = new DurableQueueStore();
        var learning = new DurableLearningStore(queue);
        var vocabulary = new FakeVocabularyRepository([Word(1), Word(2), Word(3)]);
        var dayOne = UseCases(learning, queue, vocabulary, clock);
        var first = await dayOne.Coordinator.GetNextAsync(new DailyPlan(2, 0), default);
        var displayed = Assert.IsType<NextCard>(first);
        Assert.Equal(new DateOnly(2026, 8, 13), displayed.QueueDay);
        var rated = await dayOne.Submit.HandleAsync(new(
            Guid.NewGuid(), Guid.NewGuid(), displayed.Card, displayed.Revision,
            displayed.QueueItemId, RatingShortcut.F3, displayed.QueueDay, new DailyPlan(2, 0)), default);
        var carriedSource = Assert.IsType<Success<LearningTransition>>(rated).Value.NextCard!;

        clock.UtcNow = new DateTimeOffset(2026, 8, 13, 16, 1, 0, TimeSpan.Zero);
        var dayTwo = UseCases(learning, queue, vocabulary, clock);
        var rolled = await dayTwo.Coordinator.GetNextAsync(new DailyPlan(2, 0), default);
        var reopened = UseCases(learning, queue, vocabulary, clock);
        var restored = await reopened.Coordinator.GetNextAsync(new DailyPlan(99, 99), default);

        Assert.Equal(new DateOnly(2026, 8, 14), dayTwo.Coordinator.CurrentLocalDay);
        Assert.Equal(new DateOnly(2026, 8, 14), rolled!.QueueDay);
        Assert.Equal(carriedSource.Word.WordId, rolled!.Word.WordId);
        Assert.NotEqual(carriedSource.QueueItemId, rolled.QueueItemId);
        Assert.Equal(rolled.QueueItemId, restored!.QueueItemId);
        Assert.Equal(new DailyPlan(2, 0), queue.Sessions[new DateOnly(2026, 8, 14)].ConfiguredPlan);
    }

    [Fact]
    public async Task Midnight_click_does_not_commit_yesterdays_card_when_todays_actual_next_card_differs()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+08-midnight", TimeSpan.FromHours(8), "UTC+08-midnight", "UTC+08-midnight");
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 13, 15, 59, 0, TimeSpan.Zero), zone);
        var queue = new DurableQueueStore();
        var learning = new DurableLearningStore(queue);
        learning.Seed(new CardState(Id(2), null, new DateTimeOffset(2026, 8, 13, 16, 0, 0, TimeSpan.Zero)));
        var useCases = UseCases(learning, queue, new FakeVocabularyRepository([Word(1), Word(2)]), clock);
        var displayed = Assert.IsType<NextCard>(
            await useCases.Coordinator.GetNextAsync(new DailyPlan(1, 1), default));
        Assert.Equal(Id(1), displayed.Card.Id);

        clock.UtcNow = new DateTimeOffset(2026, 8, 13, 16, 1, 0, TimeSpan.Zero);
        var result = await useCases.Submit.HandleAsync(new(
            Guid.NewGuid(), Guid.NewGuid(), displayed.Card, displayed.Revision,
            displayed.QueueItemId, RatingShortcut.F3, displayed.QueueDay, new DailyPlan(0, 1)), default);

        var transition = Assert.IsType<Success<LearningTransition>>(result).Value;
        Assert.Equal(LearningTransitionStatus.QueueDayRolledOver, transition.Status);
        Assert.Equal(Id(2), transition.NextCard!.Card.Id);
        Assert.Equal(new DateOnly(2026, 8, 14), transition.NextCard.QueueDay);
        Assert.Empty(learning.Events);
        Assert.Equal(DailyQueueItemStatus.Pending,
            queue.Sessions[new DateOnly(2026, 8, 13)].Items.Single().Status);
    }

    [Fact]
    public async Task Midnight_click_maps_the_same_carried_card_to_todays_item_before_committing()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+08-carried-click", TimeSpan.FromHours(8), "UTC+08-carried-click", "UTC+08-carried-click");
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 13, 15, 59, 0, TimeSpan.Zero), zone);
        var queue = new DurableQueueStore();
        var learning = new DurableLearningStore(queue);
        var useCases = UseCases(learning, queue, new FakeVocabularyRepository([Word(1)]), clock);
        var displayed = Assert.IsType<NextCard>(
            await useCases.Coordinator.GetNextAsync(new DailyPlan(1, 0), default));

        clock.UtcNow = new DateTimeOffset(2026, 8, 13, 16, 1, 0, TimeSpan.Zero);
        var result = await useCases.Submit.HandleAsync(new(
            Guid.NewGuid(), Guid.NewGuid(), displayed.Card, displayed.Revision,
            displayed.QueueItemId, RatingShortcut.F3, displayed.QueueDay, new DailyPlan(1, 0)), default);

        var transition = Assert.IsType<Success<LearningTransition>>(result).Value;
        var today = new DateOnly(2026, 8, 14);
        Assert.Equal(LearningTransitionStatus.Committed, transition.Status);
        Assert.Equal(today, transition.CommittedQueueDay);
        Assert.Single(learning.Events);
        Assert.Equal(DailyQueueItemStatus.CarriedForward,
            queue.Sessions[new DateOnly(2026, 8, 13)].Items.Single().Status);
        Assert.Equal(DailyQueueItemStatus.Completed, queue.Sessions[today].Items.Single().Status);
    }

    private static UseCaseSet UseCases(
        DurableLearningStore learning,
        DurableQueueStore queue,
        IVocabularyRepository vocabulary,
        TimeProvider clock)
    {
        var coordinator = new DailyQueueCoordinator(
            learning, vocabulary, queue, new QueuePolicy(new RecordingScheduler()), clock);
        var next = new GetNextCard(coordinator);
        return new(coordinator,
            new SubmitRating(learning, next, new RecordingScheduler(), clock),
            new UndoLastAction(learning, clock));
    }

    private static VocabularyWord Word(int value) => new(Id(value), $"word-{value:000}", value, true);
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private sealed record UseCaseSet(DailyQueueCoordinator Coordinator, SubmitRating Submit, UndoLastAction Undo);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow, TimeZoneInfo? zone = null) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;
    }

    private sealed class RecordingScheduler : IFsrsScheduler
    {
        public ScheduleResult Review(MemoryState? previous, Rating rating, DateTimeOffset reviewedAt, double desiredRetention) =>
            new(new MemoryState(5, 5, reviewedAt), 0.5, TimeSpan.FromDays(1), reviewedAt.AddDays(1));
        public double Retrievability(MemoryState state, DateTimeOffset at) => 0.5;
    }

    private sealed class DurableLearningStore(DurableQueueStore queue) : ILearningStore
    {
        private readonly Dictionary<Guid, CardState> cards = [];
        private readonly Dictionary<Guid, CommitResult> commits = [];
        private readonly List<QueuedEvent> queuedEvents = [];
        public Exception? ApplyQueuedFailure { get; set; }
        public IReadOnlyList<ReviewEvent> Events => queuedEvents.Select(x => x.Event).ToArray();

        public void Seed(CardState card) => cards[card.Id] = card;

        public Task<CommitResult> ApplyAsync(LearningCommand command, CancellationToken ct) =>
            ApplyCoreAsync(command, null, null, ct);

        public Task<CommitResult> ApplyQueuedAsync(
            LearningCommand command, Guid queueItemId, DailyQueueItemStatus terminalStatus, CancellationToken ct) =>
            ApplyCoreAsync(command, queueItemId, terminalStatus, ct);

        private Task<CommitResult> ApplyCoreAsync(
            LearningCommand command, Guid? queueItemId, DailyQueueItemStatus? terminalStatus, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (commits.TryGetValue(command.CommandId, out var duplicate)) return Task.FromResult(duplicate with { Applied = false });
            if (ApplyQueuedFailure is not null) throw ApplyQueuedFailure;
            var actualRevision = queuedEvents.LastOrDefault(x => x.Event.CardId == command.Event.CardId)?.Event.EventId
                ?? CardProjection.InitialRevision;
            if (actualRevision != command.ExpectedRevision) throw new LearningConcurrencyException(command.Event.CardId);
            if (queueItemId is { } itemId) queue.Complete(itemId, command.Event.CardId, terminalStatus!.Value, command.Event.EventId, command.Event.OccurredAt);
            cards[command.Event.CardId] = command.Event.After;
            queuedEvents.Add(new(command.Event, queueItemId));
            var result = new CommitResult(true, command.Event.EventId, command.Event.After);
            commits.Add(command.CommandId, result);
            return Task.FromResult(result);
        }

        public Task<CommitResult?> GetCommitAsync(Guid commandId, CancellationToken ct) =>
            Task.FromResult(commits.GetValueOrDefault(commandId));
        public Task<CardState?> GetCardAsync(Guid cardId, CancellationToken ct) => Task.FromResult(cards.GetValueOrDefault(cardId));
        public Task<CardProjection?> GetCardProjectionAsync(Guid cardId, CancellationToken ct) => Task.FromResult(
            cards.TryGetValue(cardId, out var card)
                ? new CardProjection(card, queuedEvents.LastOrDefault(x => x.Event.CardId == cardId)?.Event.EventId
                    ?? CardProjection.InitialRevision)
                : null);
        public Task<Page<CardState>> GetCardsAsync(PageRequest page, CancellationToken ct)
        {
            var all = cards.Values.OrderBy(x => x.Id).ToArray();
            var items = all.Skip(page.Offset).Take(page.Limit).ToArray();
            return Task.FromResult(new Page<CardState>(items, all.Length, page.Offset + items.Length < all.Length, "cards:test"));
        }
        public Task<ExhaustionProbe> ProbeCardsEndAsync(int offset, string snapshotId, CancellationToken ct) =>
            Task.FromResult(new ExhaustionProbe(offset >= cards.Count, "cards:test"));
        public Task<ReviewEvent?> GetLatestUndoableEventAsync(CancellationToken ct) =>
            Task.FromResult(queuedEvents.LastOrDefault()?.Event);
        public Task<CommitResult> UndoLatestAsync(UndoLearningCommand command, CancellationToken ct) =>
            UndoQueuedAsync(command, queuedEvents.Last(x => x.Event.Action != LearningAction.Undo).Event.EventId, ct);
        public Task<CommitResult> UndoQueuedAsync(UndoLearningCommand command, Guid completedEventId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (commits.TryGetValue(command.CommandId, out var duplicate)) return Task.FromResult(duplicate with { Applied = false });
            var original = queuedEvents.SingleOrDefault(x => x.Event.EventId == completedEventId
                && x.QueueItemId.HasValue && queue.IsCompleted(x.QueueItemId.Value))
                ?? throw new LearningNotFoundException("The queued action is not undoable.");
            var undo = new ReviewEvent(command.EventId, original.Event.CardId, command.OccurredAt,
                LearningAction.Undo, original.Event.After, original.Event.Before, original.Event.EventId);
            queue.Restore(original.QueueItemId!.Value, original.Event.EventId);
            cards[undo.CardId] = undo.After;
            queuedEvents.Add(new(undo, null));
            var result = new CommitResult(true, undo.EventId, undo.After);
            commits.Add(command.CommandId, result);
            return Task.FromResult(result);
        }

        private sealed record QueuedEvent(ReviewEvent Event, Guid? QueueItemId);
    }

    private sealed class DurableQueueStore : IDailyQueueStore
    {
        private readonly Dictionary<DateOnly, Session> sessions = [];
        public IReadOnlyDictionary<DateOnly, DailySessionSnapshot> Sessions => sessions.ToDictionary(x => x.Key, x => x.Value.Snapshot());
        public int PendingReads { get; private set; }

        public Task<DailySessionSnapshot?> GetAsync(DateOnly localDay, CancellationToken ct) =>
            Task.FromResult(sessions.TryGetValue(localDay, out var session) ? session.Snapshot() : null);

        public Task<DailySessionSnapshot> GetOrCreateAsync(DailyQueueSeed seed, CancellationToken ct)
        {
            if (sessions.TryGetValue(seed.LocalDay, out var existing)) return Task.FromResult(existing.Snapshot());
            var items = new List<DailyQueueItem>();
            var included = new HashSet<Guid>();
            var ordinal = 0;
            var newCount = 0;
            var reviewCount = 0;
            foreach (var prior in sessions.Values.OrderBy(x => x.Day).SelectMany(x => x.Items)
                         .Where(x => x.Status == DailyQueueItemStatus.Pending).ToArray())
            {
                var allowed = prior.Kind switch
                {
                    DailyQueueItemKind.New => newCount < seed.ConfiguredPlan.NewLimit,
                    DailyQueueItemKind.Review => reviewCount < seed.ConfiguredPlan.SoftReviewLimit,
                    _ => false,
                };
                if (!allowed || !included.Add(prior.CardId)) continue;
                Replace(prior.ItemId, prior with { Status = DailyQueueItemStatus.CarriedForward });
                items.Add(new(Guid.NewGuid(), seed.LocalDay, ordinal++, prior.CardId, prior.Kind,
                    prior.OriginDay, prior.ItemId, DailyQueueItemStatus.Pending, null, null));
                if (prior.Kind == DailyQueueItemKind.New) newCount++; else reviewCount++;
            }
            foreach (var cardId in seed.OrderedReviewCandidates.Where(included.Add).Take(seed.ConfiguredPlan.SoftReviewLimit - reviewCount))
                items.Add(new(Guid.NewGuid(), seed.LocalDay, ordinal++, cardId, DailyQueueItemKind.Review,
                    seed.LocalDay, null, DailyQueueItemStatus.Pending, null, null));
            foreach (var cardId in seed.OrderedNewCandidates.Where(included.Add).Take(seed.ConfiguredPlan.NewLimit - newCount))
                items.Add(new(Guid.NewGuid(), seed.LocalDay, ordinal++, cardId, DailyQueueItemKind.New,
                    seed.LocalDay, null, DailyQueueItemStatus.Pending, null, null));
            var session = new Session(seed.LocalDay, seed.ConfiguredPlan, items);
            sessions.Add(seed.LocalDay, session);
            return Task.FromResult(session.Snapshot());
        }

        public Task<DailyQueueItem?> GetNextPendingAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct)
        {
            PendingReads++;
            var item = sessions.GetValueOrDefault(localDay)?.Items
                .Where(x => x.Status == DailyQueueItemStatus.Pending)
                .OrderBy(x => x.Kind == DailyQueueItemKind.Relearning ? 0 : x.Kind == DailyQueueItemKind.Review ? 1 : 2)
                .ThenBy(x => x.Ordinal).FirstOrDefault();
            return Task.FromResult(item);
        }

        public Task EnsureDueRelearningAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
        public Task<DateTimeOffset?> GetNextRelearningDueAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(null);
        public Task<IReadOnlyList<DailyHistoryEntry>> GetHistoryAsync(DateOnly throughDay, int dayCount, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DailyHistoryEntry>>([]);
        public Task<GoalProgress> GetGoalProgressAsync(CancellationToken ct) => Task.FromResult(new GoalProgress(0, 0));

        public void Complete(Guid itemId, Guid cardId, DailyQueueItemStatus status, Guid eventId, DateTimeOffset at)
        {
            var item = Find(itemId);
            if (item.CardId != cardId || item.Status != DailyQueueItemStatus.Pending)
                throw new LearningConcurrencyException(cardId);
            Replace(itemId, item with { Status = status, CompletedEventId = eventId, CompletedAt = at });
        }

        public bool IsCompleted(Guid itemId)
        {
            var item = Find(itemId);
            return item.Status is DailyQueueItemStatus.Completed or DailyQueueItemStatus.Slashed;
        }

        public void Restore(Guid itemId, Guid eventId)
        {
            var item = Find(itemId);
            if (item.CompletedEventId != eventId) throw new LearningConcurrencyException(item.CardId);
            Replace(itemId, item with { Status = DailyQueueItemStatus.Pending, CompletedEventId = null, CompletedAt = null });
        }

        private DailyQueueItem Find(Guid itemId) => sessions.Values.SelectMany(x => x.Items).Single(x => x.ItemId == itemId);
        private void Replace(Guid itemId, DailyQueueItem replacement)
        {
            var session = sessions.Values.Single(x => x.Items.Any(item => item.ItemId == itemId));
            session.Items[session.Items.FindIndex(x => x.ItemId == itemId)] = replacement;
        }

        private sealed record Session(DateOnly Day, DailyPlan Plan, List<DailyQueueItem> Items)
        {
            public DailySessionSnapshot Snapshot() => new(Day, Plan, Plan, null, Items.ToArray());
        }
    }

    private sealed class FakeVocabularyRepository(IEnumerable<VocabularyWord> words) : IVocabularyRepository
    {
        private readonly VocabularyWord[] all = words.OrderBy(x => x.WordId).ToArray();
        public Task<Page<VocabularyWord>> GetWordsAsync(PageRequest page, CancellationToken ct)
        {
            var items = all.Skip(page.Offset).Take(page.Limit).ToArray();
            return Task.FromResult(new Page<VocabularyWord>(items, all.Length, page.Offset + items.Length < all.Length, "words:test"));
        }
        public Task<VocabularyWord?> GetWordAsync(Guid wordId, CancellationToken ct) => Task.FromResult(all.SingleOrDefault(x => x.WordId == wordId));
        public Task<ExhaustionProbe> ProbeWordsEndAsync(int offset, string snapshotId, CancellationToken ct) =>
            Task.FromResult(new ExhaustionProbe(offset >= all.Length, "words:test"));
        public Task<Page<VocabularySense>> GetSensesAsync(Guid wordId, PageRequest page, CancellationToken ct) =>
            Task.FromResult(new Page<VocabularySense>([], 0, false, "senses:test"));
        public Task<ExhaustionProbe> ProbeSensesEndAsync(Guid wordId, int offset, string snapshotId, CancellationToken ct) =>
            Task.FromResult(new ExhaustionProbe(true, "senses:test"));
    }

    private sealed class CandidateEnumerationFailureVocabulary(IVocabularyRepository inner) : IVocabularyRepository
    {
        public Task<Page<VocabularyWord>> GetWordsAsync(PageRequest page, CancellationToken ct) =>
            throw new IOException("Candidate enumeration must not run for a persisted session.");
        public Task<ExhaustionProbe> ProbeWordsEndAsync(int offset, string snapshotId, CancellationToken ct) =>
            throw new IOException("Candidate enumeration must not run for a persisted session.");
        public Task<VocabularyWord?> GetWordAsync(Guid wordId, CancellationToken ct) => inner.GetWordAsync(wordId, ct);
        public Task<Page<VocabularySense>> GetSensesAsync(Guid wordId, PageRequest page, CancellationToken ct) =>
            inner.GetSensesAsync(wordId, page, ct);
        public Task<ExhaustionProbe> ProbeSensesEndAsync(Guid wordId, int offset, string snapshotId, CancellationToken ct) =>
            inner.ProbeSensesEndAsync(wordId, offset, snapshotId, ct);
    }
}
