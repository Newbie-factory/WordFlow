using WordFlow.Infrastructure.Data;
using WordFlow.Application.Ports;

namespace WordFlow.Infrastructure.Tests.Data;

public sealed class DailyLearningStatisticsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wordflow-stats-{Guid.NewGuid():N}");
    public DailyLearningStatisticsTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Empty_store_reports_real_zeroes()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "user.db"));
        await new MigrationRunner(factory).MigrateAsync(default);
        var stats = await new SqliteLearningStore(factory).GetDailyStatisticsAsync(DateOnly.FromDateTime(DateTime.UtcNow), default);
        Assert.Equal(0, stats.ReviewedToday);
        Assert.Equal(0, stats.SlashedTotal);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
