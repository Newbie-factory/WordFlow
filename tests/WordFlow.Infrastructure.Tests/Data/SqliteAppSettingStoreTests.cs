using WordFlow.Infrastructure.Data;

namespace WordFlow.Infrastructure.Tests.Data;

public sealed class SqliteAppSettingStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wordflow-settings-{Guid.NewGuid():N}");

    public SqliteAppSettingStoreTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Fullscreen_suppression_default_and_changes_are_durable_in_app_setting()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "user.db"));
        await new MigrationRunner(factory).MigrateAsync(default);
        var settings = new SqliteAppSettingStore(factory);

        Assert.True(await settings.GetBooleanAsync("floating_card.suppress_topmost_fullscreen", true, default));
        await settings.SetBooleanAsync("floating_card.suppress_topmost_fullscreen", false, default);

        Assert.False(await new SqliteAppSettingStore(factory).GetBooleanAsync("floating_card.suppress_topmost_fullscreen", true, default));
    }

    [Fact]
    public async Task Multiple_settings_commit_atomically()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "atomic-user.db"));
        await new MigrationRunner(factory).MigrateAsync(default);
        var settings = new SqliteAppSettingStore(factory);

        await settings.SetManyAsync(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }, default);
        var restored = await settings.GetManyAsync(["a", "b"], default);

        Assert.Equal("1", restored["a"]);
        Assert.Equal("2", restored["b"]);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
