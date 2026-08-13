using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Domain.Tests.Learning;

public sealed class SlashLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Again_creates_a_ten_minute_relearning_step()
    {
        var engine = EngineAt(Now);

        var review = engine.Review(Id(101), ActiveCard(), Rating.Again);

        Assert.Equal(LearningAction.Again, review.Action);
        Assert.Equal(Now.AddMinutes(10), review.After.DueAt);
        Assert.Equal(1, review.After.SameDayFailureCount);
        Assert.Equal(DateOnly.FromDateTime(Now.UtcDateTime), review.After.FailureDayUtc);
        Assert.Null(review.After.HardWordProtectedUntil);
    }

    [Fact]
    public void Third_same_day_failure_activates_hard_word_protection_and_count_is_capped()
    {
        var engine = EngineAt(Now);
        var card = ActiveCard();

        card = engine.Review(Id(101), card, Rating.Again).After;
        card = engine.Review(Id(102), card, Rating.Again).After;
        card = engine.Review(Id(103), card, Rating.Again).After;
        card = engine.Review(Id(104), card, Rating.Again).After;

        Assert.Equal(3, card.SameDayFailureCount);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero), card.HardWordProtectedUntil);
        Assert.Equal(Now.AddMinutes(10), card.DueAt);
    }

    [Fact]
    public void A_failure_on_the_next_utc_day_starts_a_new_failure_window()
    {
        var nextDay = Now.AddDays(1);
        var protectedCard = ActiveCard() with
        {
            SameDayFailureCount = 3,
            FailureDayUtc = DateOnly.FromDateTime(Now.UtcDateTime),
            HardWordProtectedUntil = nextDay.Date,
        };

        var result = EngineAt(nextDay).Review(Id(105), protectedCard, Rating.Again);

        Assert.Equal(1, result.After.SameDayFailureCount);
        Assert.Null(result.After.HardWordProtectedUntil);
    }

    [Fact]
    public void Slash_is_separate_from_fsrs_and_scheduled_restore_retains_history()
    {
        var original = ActiveCard();
        var slashed = LearningActions.Slash(original, Now);

        var restored = LearningActions.Restore(slashed, RestoreMode.Scheduled, Now.AddHours(1));

        Assert.True(slashed.Slash.IsSlashed);
        Assert.Equal(original.MemoryState, slashed.MemoryState);
        Assert.False(restored.Slash.IsSlashed);
        Assert.Equal(original.MemoryState, restored.MemoryState);
        Assert.Equal(original.DueAt, restored.DueAt);
    }

    [Fact]
    public void Immediate_restore_retains_history_but_makes_card_due_at_restore_instant()
    {
        var original = ActiveCard();
        var restoredAt = Now.AddHours(1);

        var restored = LearningActions.Restore(
            LearningActions.Slash(original, Now),
            RestoreMode.Immediate,
            restoredAt);

        Assert.Equal(original.MemoryState, restored.MemoryState);
        Assert.Equal(restoredAt, restored.DueAt);
        Assert.Equal(restoredAt, restored.Slash.RestoredAt);
    }

    [Fact]
    public void Undo_appends_a_compensating_event_instead_of_mutating_the_original()
    {
        var engine = EngineAt(Now);
        var originalCard = ActiveCard();
        var review = engine.Review(Id(201), originalCard, Rating.Good);

        var compensation = engine.Undo(Id(202), review);

        Assert.Equal(LearningAction.Undo, compensation.Action);
        Assert.Equal(review.EventId, compensation.CompensatesEventId);
        Assert.Equal(review.After, compensation.Before);
        Assert.Equal(review.Before, compensation.After);
        Assert.Equal(LearningAction.Good, review.Action);
        Assert.Null(review.CompensatesEventId);
    }

    [Fact]
    public void Time_is_obtained_from_injected_time_provider()
    {
        var provider = new StubTimeProvider(Now);
        var engine = new LearningActions(new Fsrs6Scheduler(), provider);

        var first = engine.Slash(Id(301), ActiveCard());
        provider.SetUtcNow(Now.AddHours(2));
        var second = engine.Restore(Id(302), first.After, RestoreMode.Immediate);

        Assert.Equal(Now, first.OccurredAt);
        Assert.Equal(Now.AddHours(2), second.OccurredAt);
    }

    [Fact]
    public void Invalid_lifecycle_transitions_are_rejected()
    {
        var active = ActiveCard();
        var slashed = LearningActions.Slash(active, Now);

        Assert.Throws<InvalidOperationException>(() => LearningActions.Restore(active, RestoreMode.Scheduled, Now));
        Assert.Throws<InvalidOperationException>(() => LearningActions.Slash(slashed, Now.AddMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LearningActions.Restore(slashed, RestoreMode.Scheduled, Now.AddTicks(-1)));
        Assert.Throws<InvalidOperationException>(() => EngineAt(Now).Review(Id(401), slashed, Rating.Good));
    }

    private static LearningActions EngineAt(DateTimeOffset now) =>
        new(new Fsrs6Scheduler(), new StubTimeProvider(now));

    private static CardState ActiveCard() =>
        new(
            Id(1),
            new MemoryState(4.5, 12, Now.AddDays(-2)),
            Now.AddDays(5));

    private static Guid Id(int value) => new($"00000000-0000-0000-0000-{value:D12}");

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void SetUtcNow(DateTimeOffset value) => utcNow = value;
    }
}
