using System.Data.Common;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Application.Tests.Learning;

public sealed class LearningUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RatingShortcut.F1, Rating.Again)]
    [InlineData(RatingShortcut.F2, Rating.Hard)]
    [InlineData(RatingShortcut.F3, Rating.Good)]
    public async Task Rating_shortcuts_map_only_to_the_three_supported_FSRS_ratings(RatingShortcut shortcut, Rating expected)
    {
        var current = Card(1);
        var next = Card(2);
        var store = new FakeLearningStore([current, next]);
        var queue = Queue(store, [Word(1), Word(2)]);
        var handler = new SubmitRating(store, queue, new RecordingScheduler(), Clock());

        var result = await handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), current.Id, shortcut), default);

        var success = Assert.IsType<Success<LearningTransition>>(result);
        Assert.Equal(expected, Assert.Single(store.Applied).Event.Action switch
        {
            LearningAction.Again => Rating.Again,
            LearningAction.Hard => Rating.Hard,
            LearningAction.Good => Rating.Good,
            _ => throw new Xunit.Sdk.XunitException("Slash or Easy-like behavior entered rating mapping."),
        });
        Assert.Equal(next.Id, success.Value.NextCard!.Card.Id);
    }

    [Fact]
    public async Task Illegal_shortcut_value_is_rejected_without_writing_or_advancing()
    {
        var store = new FakeLearningStore([Card(1)]);
        var queue = Queue(store, [Word(1)]);
        var handler = new SubmitRating(store, queue, new RecordingScheduler(), Clock());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => handler.HandleAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), Id(1), (RatingShortcut)99), default));

        Assert.Empty(store.Applied);
        Assert.Equal(0, store.CardPageReads);
    }

    [Fact]
    public async Task Failed_commit_never_reads_or_advances_the_next_card()
    {
        var store = new FakeLearningStore([Card(1), Card(2)]) { ApplyFailure = new FakeDbException() };
        var queue = Queue(store, [Word(1), Word(2)]);
        var handler = new SubmitRating(store, queue, new RecordingScheduler(), Clock());

        var result = await handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), Id(1), RatingShortcut.F3), default);

        Assert.IsType<StorageFailure<LearningTransition>>(result);
        Assert.Equal(0, store.CardPageReads);
    }

    [Fact]
    public async Task Slash_is_a_separate_action_and_advances_only_after_commit()
    {
        var store = new FakeLearningStore([Card(1), Card(2)]);
        var handler = new SlashWord(store, Queue(store, [Word(1), Word(2)]), Clock());

        var result = await handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), Id(1)), default);

        var success = Assert.IsType<Success<LearningTransition>>(result);
        Assert.Equal(LearningAction.Slash, Assert.Single(store.Applied).Event.Action);
        Assert.True(store.Applied[0].Event.After.Slash.IsSlashed);
        Assert.Equal(Id(2), success.Value.NextCard!.Card.Id);
    }

    [Theory]
    [InlineData(RestoreMode.Scheduled)]
    [InlineData(RestoreMode.Immediate)]
    public async Task Restore_preserves_FSRS_and_applies_the_requested_due_mode(RestoreMode mode)
    {
        var memory = new MemoryState(4, 7, Now.AddDays(-1));
        var active = new CardState(Id(1), memory, Now.AddDays(3));
        var slashed = LearningActions.Slash(active, Now.AddDays(-2));
        var store = new FakeLearningStore([slashed]);
        var handler = new RestoreSlashedWords(store, Clock());

        var result = await handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), slashed.Id, mode), default);

        var restored = Assert.IsType<Success<CardState>>(result).Value;
        Assert.Equal(memory, restored.MemoryState);
        Assert.False(restored.Slash.IsSlashed);
        Assert.Equal(mode == RestoreMode.Immediate ? Now : active.DueAt, restored.DueAt);
    }

    [Fact]
    public async Task Undo_appends_a_compensating_event_and_duplicate_command_is_idempotent()
    {
        var before = Card(1);
        var original = new LearningActions(new RecordingScheduler(), Clock()).Slash(Guid.NewGuid(), before);
        var store = new FakeLearningStore([original.After]);
        store.Events.Add(original);
        var commandId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var handler = new UndoLastAction(store, Clock());

        var first = await handler.HandleAsync(new(commandId, eventId), default);
        var second = await handler.HandleAsync(new(commandId, Guid.NewGuid()), default);

        var restored = Assert.IsType<Success<CardState>>(first).Value;
        Assert.False(restored.Slash.IsSlashed);
        Assert.IsType<Success<CardState>>(second);
        Assert.Equal(2, store.Events.Count);
        Assert.Equal(original.EventId, store.Events[^1].CompensatesEventId);
        Assert.Equal(LearningAction.Undo, store.Events[^1].Action);
    }

    [Fact]
    public async Task Missing_cards_and_stale_writes_are_discriminated()
    {
        var store = new FakeLearningStore([]);
        var queue = Queue(store, []);
        var submit = new SubmitRating(store, queue, new RecordingScheduler(), Clock());

        Assert.IsType<NotFound<LearningTransition>>(await submit.HandleAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), Id(1), RatingShortcut.F1), default));

        store.Cards[Id(1)] = Card(1);
        store.ApplyFailure = new LearningConcurrencyException(Id(1));
        Assert.IsType<Conflict<LearningTransition>>(await submit.HandleAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), Id(1), RatingShortcut.F1), default));
    }

    [Fact]
    public async Task New_learning_headword_can_be_rated_before_a_snapshot_exists()
    {
        var store = new FakeLearningStore([]);
        var queue = Queue(store, [Word(1), Word(2)]);
        var handler = new SubmitRating(store, queue, new RecordingScheduler(), Clock());

        var result = await handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), Id(1), RatingShortcut.F3), default);

        var transition = Assert.IsType<Success<LearningTransition>>(result).Value;
        Assert.Equal(Id(1), transition.Card.Id);
        Assert.NotNull(transition.Card.MemoryState);
        Assert.Equal(Id(2), transition.NextCard!.Card.Id);
    }

    [Fact]
    public async Task Cancellation_and_corrupt_data_propagate_while_expected_storage_failures_are_results()
    {
        var store = new FakeLearningStore([Card(1)]);
        var handler = new RestoreSlashedWords(store, Clock());
        store.GetCardFailure = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.HandleAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), Id(1), RestoreMode.Immediate), default));

        store.GetCardFailure = new InvalidDataException("corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.HandleAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), Id(1), RestoreMode.Immediate), default));
    }

    [Fact]
    public async Task Queue_reads_every_page_and_never_admits_non_headword_spellings()
    {
        var store = new FakeLearningStore([]);
        var words = Enumerable.Range(1, 501).Select(Word).Append(
            new VocabularyWord(Id(999), "stimuate", null, false)).ToArray();
        var next = Queue(store, words);

        var result = await next.HandleAsync(new(new DailyPlan(600, 0)), default);

        var success = Assert.IsType<Success<NextCard?>>(result);
        Assert.Equal(Id(1), success.Value!.Card.Id);
        Assert.DoesNotContain(Id(999), next.LastBuiltQueue);
    }

    private static GetNextCard Queue(FakeLearningStore store, IEnumerable<VocabularyWord> words) =>
        new(store, new FakeVocabularyRepository(words), new QueuePolicy(new RecordingScheduler()), Clock());

    private static FakeTimeProvider Clock() => new(Now);
    private static CardState Card(int value) => new(Id(value), null, Now.AddMinutes(-value));
    private static VocabularyWord Word(int value) => new(Id(value), $"word-{value:000}", value, true);
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingScheduler : IFsrsScheduler
    {
        public ScheduleResult Review(MemoryState? previous, Rating rating, DateTimeOffset reviewedAt, double desiredRetention) =>
            new(new MemoryState(5, 5, reviewedAt), 0.5, TimeSpan.FromDays(1), reviewedAt.AddDays(1));
        public double Retrievability(MemoryState state, DateTimeOffset at) => 0.5;
    }

    private sealed class FakeDbException : DbException;

    private sealed class FakeLearningStore(IEnumerable<CardState> cards) : ILearningStore
    {
        public Dictionary<Guid, CardState> Cards { get; } = cards.ToDictionary(x => x.Id);
        public List<LearningCommand> Applied { get; } = [];
        public List<ReviewEvent> Events { get; } = [];
        public Exception? ApplyFailure { get; set; }
        public Exception? GetCardFailure { get; set; }
        public int CardPageReads { get; private set; }

        public Task<CommitResult> ApplyAsync(LearningCommand command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (ApplyFailure is not null) throw ApplyFailure;
            var duplicate = Applied.FirstOrDefault(x => x.CommandId == command.CommandId);
            if (duplicate is not null)
            {
                return Task.FromResult(new CommitResult(false, duplicate.Event.EventId, duplicate.Event.After));
            }
            Applied.Add(command);
            Events.Add(command.Event);
            Cards[command.Event.CardId] = command.Event.After;
            return Task.FromResult(new CommitResult(true, command.Event.EventId, command.Event.After));
        }

        public Task<CardState?> GetCardAsync(Guid cardId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (GetCardFailure is not null) throw GetCardFailure;
            return Task.FromResult(Cards.GetValueOrDefault(cardId));
        }

        public Task<CommitResult?> GetCommitAsync(Guid commandId, CancellationToken ct)
        {
            var command = Applied.SingleOrDefault(x => x.CommandId == commandId);
            return Task.FromResult(command is null ? null : new CommitResult(false, command.Event.EventId, command.Event.After));
        }

        public Task<Page<CardState>> GetCardsAsync(PageRequest page, CancellationToken ct)
        {
            CardPageReads++;
            var ordered = Cards.Values.OrderBy(x => x.Id).ToArray();
            return Task.FromResult(new Page<CardState>(ordered.Skip(page.Offset).Take(page.Limit).ToArray(), ordered.Length));
        }

        public Task<ReviewEvent?> GetLatestUndoableEventAsync(CancellationToken ct)
        {
            var compensated = Events.Where(x => x.CompensatesEventId.HasValue).Select(x => x.CompensatesEventId!.Value).ToHashSet();
            return Task.FromResult(Events.LastOrDefault(x => x.Action != LearningAction.Undo && !compensated.Contains(x.EventId)));
        }
    }

    private sealed class FakeVocabularyRepository(IEnumerable<VocabularyWord> words) : IVocabularyRepository
    {
        private readonly VocabularyWord[] all = words.OrderBy(x => x.WordId).ToArray();
        public Task<Page<VocabularyWord>> GetWordsAsync(PageRequest page, CancellationToken ct) =>
            Task.FromResult(new Page<VocabularyWord>(all.Skip(page.Offset).Take(page.Limit).ToArray(), all.Length));
        public Task<VocabularyWord?> GetWordAsync(Guid wordId, CancellationToken ct) => Task.FromResult(all.SingleOrDefault(x => x.WordId == wordId));
        public Task<Page<VocabularySense>> GetSensesAsync(Guid wordId, PageRequest page, CancellationToken ct) =>
            Task.FromResult(new Page<VocabularySense>([], 0));
    }
}
