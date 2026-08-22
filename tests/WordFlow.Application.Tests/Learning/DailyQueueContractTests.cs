using WordFlow.Application.Learning;
using WordFlow.Application.Ports;

namespace WordFlow.Application.Tests.Learning;

public sealed class DailyQueueContractTests
{
    private static readonly DateOnly Day = new(2026, 8, 13);
    private static readonly Guid CardId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public void Displayed_cards_and_mutations_require_an_explicit_non_nullable_queue_day()
    {
        Assert.Equal(typeof(DateOnly), typeof(NextCard).GetProperty(nameof(NextCard.QueueDay))!.PropertyType);
        Assert.False(typeof(NextCard).GetConstructors().Single().GetParameters()
            .Single(parameter => parameter.Name == "QueueDay").HasDefaultValue);
        Assert.False(typeof(SubmitRatingRequest).GetConstructors().Single().GetParameters()
            .Single(parameter => parameter.Name == "QueueDay").HasDefaultValue);
        Assert.False(typeof(SlashWordRequest).GetConstructors().Single().GetParameters()
            .Single(parameter => parameter.Name == "QueueDay").HasDefaultValue);
    }

    [Theory]
    [InlineData(DailyQueueItemKind.New, DailyQueueItemStatus.Pending)]
    [InlineData(DailyQueueItemKind.Review, DailyQueueItemStatus.Completed)]
    [InlineData(DailyQueueItemKind.Relearning, DailyQueueItemStatus.CarriedForward)]
    public void Queue_item_contract_accepts_all_persisted_states(
        DailyQueueItemKind kind,
        DailyQueueItemStatus status)
    {
        var item = new DailyQueueItem(
            Guid.NewGuid(), Day, 0, CardId, kind, Day, null, status, null, null);

        Assert.Equal(kind, item.Kind);
        Assert.Equal(status, item.Status);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 2, 0.5)]
    [InlineData(3, 2, 1)]
    public void Goal_progress_ratio_is_safe_and_bounded(
        int slashedWords,
        int totalWords,
        double expectedRatio)
    {
        var progress = new GoalProgress(slashedWords, totalWords);

        Assert.Equal(expectedRatio, progress.Ratio);
    }
}
