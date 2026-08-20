using WordFlow.App.Bootstrap;

namespace WordFlow.App.Tests.Bootstrap;

public sealed class LocalLifecycleLogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"WordFlow.Log.{Guid.NewGuid():N}");

    [Fact]
    public void Write_appends_timestamped_local_events()
    {
        var log = new LocalLifecycleLog(root);

        log.Write("primary-ready");
        log.Write("activation-received");

        string[] lines = File.ReadAllLines(log.Path);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith(" primary-ready", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(" activation-received", lines[1], StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParse(lines[0].Split(' ')[0], out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
