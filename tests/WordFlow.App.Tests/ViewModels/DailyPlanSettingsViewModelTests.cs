using WordFlow.App.ViewModels;
using WordFlow.Infrastructure.Data;

namespace WordFlow.App.Tests.ViewModels;

public sealed class DailyPlanSettingsViewModelTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wordflow-daily-plan-{Guid.NewGuid():N}");
    public DailyPlanSettingsViewModelTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Daily_plan_is_restored_and_saved_durably()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "user.db"));
        await new MigrationRunner(factory).MigrateAsync(default);
        var store = new SqliteAppSettingStore(factory);
        var settings = new DailyPlanSettingsViewModel(store);
        await settings.RestoreAsync();
        Assert.Equal(40, settings.NewLimit);
        Assert.Equal(120, settings.SoftReviewLimit);
        settings.NewLimit = 25;
        settings.SoftReviewLimit = 80;
        await settings.SaveAsync();

        var restored = new DailyPlanSettingsViewModel(new SqliteAppSettingStore(factory));
        await restored.RestoreAsync();
        Assert.Equal(25, restored.NewLimit);
        Assert.Equal(80, restored.SoftReviewLimit);
        Assert.Equal(25, restored.Plan.NewLimit);
        Assert.Equal(80, restored.Plan.SoftReviewLimit);
    }

    [Fact]
    public async Task Negative_limits_are_rejected()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "validation.db"));
        await new MigrationRunner(factory).MigrateAsync(default);
        var settings = new DailyPlanSettingsViewModel(new SqliteAppSettingStore(factory));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.NewLimit = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.SoftReviewLimit = -1);
    }

    public void Dispose() => Directory.Delete(directory, true);

}
