using WordFlow.Domain.Learning;
using WordFlow.Domain.Scheduling;

namespace WordFlow.Domain.Tests.Learning;

public sealed class QueuePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Default_plan_uses_forty_new_and_a_soft_one_hundred_twenty_review_target()
    {
        var plan = DailyPlan.Default;

        Assert.Equal(40, plan.NewLimit);
        Assert.Equal(120, plan.SoftReviewLimit);
    }

    [Fact]
    public void Default_plan_builds_a_suggested_queue_up_to_its_soft_review_target()
    {
        var reviews = Enumerable.Range(1, 121)
            .Select(index => Card(index, Now.AddDays(-index), 2))
            .ToArray();

        var queue = new QueuePolicy(new Fsrs6Scheduler()).Build(
            new QueueInput(reviews, Array.Empty<Guid>(), DailyPlan.Default, Now));

        Assert.Equal(DailyPlan.Default.SoftReviewLimit, queue.Count);
    }

    [Fact]
    public void Due_reviews_precede_new_words_and_each_daily_limit_is_applied()
    {
        var reviews = Enumerable.Range(0, 3)
            .Select(index => Card(index + 1, Now.AddDays(-index - 1), stabilityDays: 2))
            .ToArray();
        var newCards = Enumerable.Range(101, 3).Select(Id).Reverse().ToArray();
        var input = new QueueInput(reviews, newCards, new DailyPlan(2, 2), Now);

        var queue = new QueuePolicy(new Fsrs6Scheduler()).Build(input);

        Assert.Equal(4, queue.Count);
        Assert.All(queue.Take(2), id => Assert.Contains(id, reviews.Select(card => card.Id)));
        Assert.Equal(newCards.OrderBy(id => id).Take(2), queue.Skip(2));
    }

    [Fact]
    public void Due_order_is_stable_by_risk_then_due_time_then_entry_id()
    {
        var highestRisk = Card(9, Now.AddHours(-1), stabilityDays: 1);
        var earlierDue = Card(5, Now.AddHours(-2), stabilityDays: 20);
        var lowerId = Card(2, Now.AddHours(-1), stabilityDays: 20);
        var higherId = Card(3, Now.AddHours(-1), stabilityDays: 20);
        var cards = new[] { higherId, earlierDue, highestRisk, lowerId };

        var first = new QueuePolicy(new Fsrs6Scheduler())
            .Build(new QueueInput(cards, Array.Empty<Guid>(), new DailyPlan(0, 10), Now));
        var second = new QueuePolicy(new Fsrs6Scheduler())
            .Build(new QueueInput(cards.Reverse().ToArray(), Array.Empty<Guid>(), new DailyPlan(0, 10), Now));

        Assert.Equal(new[] { highestRisk.Id, earlierDue.Id, lowerId.Id, higherId.Id }, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Future_slashed_and_same_day_protected_cards_are_not_queued()
    {
        var active = Card(1, Now.AddMinutes(-1), 2);
        var future = Card(2, Now.AddMinutes(1), 2);
        var slashed = LearningActions.Slash(Card(3, Now.AddMinutes(-1), 2), Now.AddHours(-1));
        var protectedCard = Card(4, Now.AddMinutes(-1), 2) with
        {
            HardWordProtectedUntil = Now.Date.AddDays(1),
        };

        var queue = new QueuePolicy(new Fsrs6Scheduler()).Build(
            new QueueInput(
                new[] { protectedCard, slashed, future, active },
                new[] { active.Id, Id(6) },
                DailyPlan.Default,
                Now));

        Assert.Equal(new[] { active.Id, Id(6) }, queue);
    }

    [Fact]
    public void Every_known_review_card_is_excluded_from_new_candidates_even_when_not_selected()
    {
        var selected = Card(1, Now.AddDays(-30), 1);
        var capped = Card(2, Now.AddDays(-1), 20);
        var future = Card(3, Now.AddMinutes(1), 2);
        var slashed = LearningActions.Slash(Card(4, Now.AddMinutes(-1), 2), Now.AddHours(-1));
        var protectedCard = Card(5, Now.AddMinutes(-1), 2) with
        {
            HardWordProtectedUntil = Now.AddHours(1),
        };
        var allKnownIds = new[] { selected.Id, capped.Id, future.Id, slashed.Id, protectedCard.Id };

        var queue = new QueuePolicy(new Fsrs6Scheduler()).Build(
            new QueueInput(
                new[] { selected, capped, future, slashed, protectedCard },
                allKnownIds.Append(Id(6)),
                new DailyPlan(10, 1),
                Now));

        Assert.Equal(new[] { selected.Id, Id(6) }, queue);
    }

    [Fact]
    public void Duplicate_due_entries_do_not_consume_the_review_limit()
    {
        var highRisk = Card(1, Now.AddDays(-2), 1);
        var lowerRisk = Card(2, Now.AddDays(-1), 20);

        var queue = new QueuePolicy(new Fsrs6Scheduler()).Build(
            new QueueInput(
                new[] { highRisk, highRisk, lowerRisk },
                Array.Empty<Guid>(),
                new DailyPlan(0, 2),
                Now));

        Assert.Equal(new[] { highRisk.Id, lowerRisk.Id }, queue);
    }

    [Fact]
    public void Conflicting_snapshots_for_one_card_are_rejected_regardless_of_input_order()
    {
        var first = Card(1, Now.AddDays(-1), 1);
        var conflicting = first with
        {
            MemoryState = new MemoryState(
                first.MemoryState!.Difficulty + 1,
                first.MemoryState.StabilityDays + 5,
                first.MemoryState.LastReviewAt),
        };

        Assert.Throws<ArgumentException>(() => new QueueInput(
            new[] { first, conflicting },
            Array.Empty<Guid>(),
            DailyPlan.Default,
            Now));
        Assert.Throws<ArgumentException>(() => new QueueInput(
            new[] { conflicting, first },
            Array.Empty<Guid>(),
            DailyPlan.Default,
            Now));
    }

    [Fact]
    public void Negative_limits_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DailyPlan(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DailyPlan(0, -1));
    }

    private static CardState Card(int id, DateTimeOffset dueAt, double stabilityDays) =>
        new(
            Id(id),
            new MemoryState(5, stabilityDays, Now.AddDays(-10)),
            dueAt);

    private static Guid Id(int value) => new($"00000000-0000-0000-0000-{value:D12}");
}
