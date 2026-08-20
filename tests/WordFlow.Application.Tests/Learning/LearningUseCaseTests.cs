using System.Data.Common;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Application.Tests.Learning;

public sealed class LearningUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Interactive_learning_command_requires_an_explicit_non_nullable_revision()
    {
        var constructors = typeof(LearningCommand).GetConstructors();

        Assert.All(constructors, constructor => Assert.Equal(3, constructor.GetParameters().Length));
        Assert.Equal(typeof(Guid), typeof(LearningCommand).GetProperty(nameof(LearningCommand.ExpectedRevision))!.PropertyType);
    }

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

        var result = await handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), current, CardProjection.InitialRevision, shortcut), default);

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
            new(Guid.NewGuid(), Guid.NewGuid(), Card(1), CardProjection.InitialRevision, (RatingShortcut)99), default));

        Assert.Empty(store.Applied);
        Assert.Equal(0, store.CardPageReads);
    }

    [Fact]
    public async Task Failed_commit_never_reads_or_advances_the_next_card()
    {
        var store = new FakeLearningStore([Card(1), Card(2)]) { ApplyFailure = new TransientStorageException("busy", new IOException()) };
        var queue = Queue(store, [Word(1), Word(2)]);
        var handler = new SubmitRating(store, queue, new RecordingScheduler(), Clock());

        var result = await handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), Card(1), CardProjection.InitialRevision, RatingShortcut.F3), default);

        Assert.IsType<StorageFailure<LearningTransition>>(result);
        Assert.Equal(0, store.CardPageReads);
    }

    [Fact]
    public async Task Slash_is_a_separate_action_and_advances_only_after_commit()
    {
        var store = new FakeLearningStore([Card(1), Card(2)]);
        var handler = new SlashWord(store, Queue(store, [Word(1), Word(2)]), Clock());

        var result = await handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), Card(1), CardProjection.InitialRevision), default);

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

        var result = await handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), slashed.Id, CardProjection.InitialRevision, mode), default);

        var restored = Assert.IsType<Success<CardState>>(result).Value;
        Assert.Equal(memory, restored.MemoryState);
        Assert.False(restored.Slash.IsSlashed);
        Assert.Equal(mode == RestoreMode.Immediate ? Now : active.DueAt, restored.DueAt);
    }

    [Fact]
    public async Task Stale_restore_returns_conflict_without_appending()
    {
        var slashed = LearningActions.Slash(Card(1), Now.AddDays(-2));
        var store = new FakeLearningStore([slashed]);
        store.Events.Add(new ReviewEvent(Id(92), slashed.Id, Now, LearningAction.Slash, slashed, slashed));

        var result = await new RestoreSlashedWords(store, Clock()).HandleAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), slashed.Id, CardProjection.InitialRevision, RestoreMode.Immediate), default);

        Assert.IsType<Conflict<CardState>>(result);
        Assert.Empty(store.Applied);
        Assert.Single(store.Events);
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
            new(Guid.NewGuid(), Guid.NewGuid(), Card(1), CardProjection.InitialRevision, RatingShortcut.F1), default));

        store.Cards[Id(1)] = Card(1);
        store.ApplyFailure = new LearningConcurrencyException(Id(1));
        Assert.IsType<Conflict<LearningTransition>>(await submit.HandleAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), Card(1), CardProjection.InitialRevision, RatingShortcut.F1), default));
    }

    [Fact]
    public async Task New_learning_headword_can_be_rated_before_a_snapshot_exists()
    {
        var store = new FakeLearningStore([]);
        var queue = Queue(store, [Word(1), Word(2)]);
        var handler = new SubmitRating(store, queue, new RecordingScheduler(), Clock());

        var result = await handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), new CardState(Id(1), null, Now), CardProjection.InitialRevision, RatingShortcut.F3), default);

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
            new(Guid.NewGuid(), Guid.NewGuid(), Id(1), CardProjection.InitialRevision, RestoreMode.Immediate), default));

        store.GetCardFailure = new InvalidDataException("corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.HandleAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), Id(1), CardProjection.InitialRevision, RestoreMode.Immediate), default));
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

    [Fact]
    public async Task Next_card_projects_the_quality_gated_primary_definition_to_its_matching_sense()
    {
        var store = new FakeLearningStore([]);
        var word = new VocabularyWord(Id(1), "abate", 1, true, "/əˈbeɪt/", "减轻", "to become less intense", "v");
        var senses = new[]
        {
            new VocabularySense("sense-z", Id(1), "a legal reduction", "n"),
            new VocabularySense("sense-a", Id(1), "to become less intense", "v"),
        };
        var queue = new GetNextCard(store, new FakeVocabularyRepository([word], senses), new QueuePolicy(new RecordingScheduler()), Clock());

        var result = await queue.HandleAsync(new(DailyPlan.Default), default);

        var primary = Assert.IsType<Success<NextCard?>>(result).Value!.PrimarySense!;
        Assert.Equal("sense-a", primary.SenseId);
        Assert.Equal("to become less intense", primary.Definition);
        Assert.False(primary.IsDeterministicFallback);
    }

    [Fact]
    public async Task Next_card_marks_a_deterministic_sense_fallback_when_actual_definition_has_no_match()
    {
        var store = new FakeLearningStore([]);
        var word = new VocabularyWord(Id(1), "abate", 1, true, "", "减轻", "curated primary", "v");
        var senses = new[]
        {
            new VocabularySense("sense-z", Id(1), "z", "v"),
            new VocabularySense("sense-a", Id(1), "a", "n"),
        };
        var queue = new GetNextCard(store, new FakeVocabularyRepository([word], senses), new QueuePolicy(new RecordingScheduler()), Clock());

        var primary = Assert.IsType<Success<NextCard?>>(await queue.HandleAsync(new(DailyPlan.Default), default)).Value!.PrimarySense!;

        Assert.Equal("sense-a", primary.SenseId);
        Assert.Equal("curated primary", primary.Definition);
        Assert.True(primary.IsDeterministicFallback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABA_stale_application_mutation_returns_conflict_without_queue_reads(bool slash)
    {
        var original = Card(1);
        var store = new FakeLearningStore([original]);
        store.Events.Add(new ReviewEvent(Id(91), original.Id, Now, LearningAction.Good, original, original));
        var queue = Queue(store, [Word(1), Word(2)]);
        UseCaseResult<LearningTransition> result = slash
            ? await new SlashWord(store, queue, Clock()).HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), original, CardProjection.InitialRevision), default)
            : await new SubmitRating(store, queue, new RecordingScheduler(), Clock()).HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), original, CardProjection.InitialRevision, RatingShortcut.F3), default);

        Assert.IsType<Conflict<LearningTransition>>(result);
        Assert.Equal(0, store.CardPageReads);
        Assert.Single(store.Events);
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
            var actualRevision = Events.LastOrDefault(x => x.CardId == command.Event.CardId)?.EventId ?? CardProjection.InitialRevision;
            if (command.ExpectedRevision is { } expectedRevision && expectedRevision != actualRevision)
                throw new LearningConcurrencyException(command.Event.CardId);
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
        public Task<CardProjection?> GetCardProjectionAsync(Guid cardId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (GetCardFailure is not null) throw GetCardFailure;
            return Task.FromResult(Cards.TryGetValue(cardId, out var card)
                ? new CardProjection(card, Events.LastOrDefault(x => x.CardId == cardId)?.EventId ?? CardProjection.InitialRevision)
                : null);
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
            var items = ordered.Skip(page.Offset).Take(page.Limit).ToArray();
            return Task.FromResult(new Page<CardState>(items, ordered.Length, page.Offset + items.Length < ordered.Length, "cards:v1"));
        }
        public Task<ExhaustionProbe> ProbeCardsEndAsync(int offset, string snapshotId, CancellationToken ct) =>
            Task.FromResult(new ExhaustionProbe(offset >= Cards.Count, "cards:v1"));

        public Task<ReviewEvent?> GetLatestUndoableEventAsync(CancellationToken ct)
        {
            var compensated = Events.Where(x => x.CompensatesEventId.HasValue).Select(x => x.CompensatesEventId!.Value).ToHashSet();
            return Task.FromResult(Events.LastOrDefault(x => x.Action != LearningAction.Undo && !compensated.Contains(x.EventId)));
        }

        public Task<CommitResult> UndoLatestAsync(UndoLearningCommand command, CancellationToken ct)
        {
            var duplicate = Applied.FirstOrDefault(x => x.CommandId == command.CommandId);
            if (duplicate is not null) return Task.FromResult(new CommitResult(false, duplicate.Event.EventId, duplicate.Event.After));
            var original = Events.LastOrDefault(x => x.Action != LearningAction.Undo && !Events.Any(u => u.CompensatesEventId == x.EventId))
                ?? throw new LearningNotFoundException("There is no action to undo.");
            var undo = new LearningActions(new RecordingScheduler(), new FakeTimeProvider(command.OccurredAt)).Undo(command.EventId, original);
            var revision = Events.LastOrDefault(x => x.CardId == original.CardId)?.EventId ?? CardProjection.InitialRevision;
            return ApplyAsync(new LearningCommand(command.CommandId, undo, revision), ct);
        }
    }

    private sealed class FakeVocabularyRepository(IEnumerable<VocabularyWord> words, IEnumerable<VocabularySense>? senses = null) : IVocabularyRepository
    {
        private readonly VocabularyWord[] all = words.OrderBy(x => x.WordId).ToArray();
        private readonly VocabularySense[] allSenses = (senses ?? []).ToArray();
        public Task<Page<VocabularyWord>> GetWordsAsync(PageRequest page, CancellationToken ct) =>
            Task.FromResult(new Page<VocabularyWord>(all.Skip(page.Offset).Take(page.Limit).ToArray(), all.Length, page.Offset + Math.Min(page.Limit, Math.Max(0, all.Length - page.Offset)) < all.Length, "words:v1"));
        public Task<VocabularyWord?> GetWordAsync(Guid wordId, CancellationToken ct) => Task.FromResult(all.SingleOrDefault(x => x.WordId == wordId));
        public Task<ExhaustionProbe> ProbeWordsEndAsync(int offset, string snapshotId, CancellationToken ct) => Task.FromResult(new ExhaustionProbe(offset >= all.Length, "words:v1"));
        public Task<Page<VocabularySense>> GetSensesAsync(Guid wordId, PageRequest page, CancellationToken ct) =>
            Task.FromResult(new Page<VocabularySense>(allSenses.Where(x => x.WordId == wordId).Skip(page.Offset).Take(page.Limit).ToArray(), allSenses.Count(x => x.WordId == wordId), false, "senses:v1"));
        public Task<ExhaustionProbe> ProbeSensesEndAsync(Guid wordId, int offset, string snapshotId, CancellationToken ct) => Task.FromResult(new ExhaustionProbe(offset >= allSenses.Count(x => x.WordId == wordId), "senses:v1"));
    }
}
