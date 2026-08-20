using System.Globalization;
using System.IO;

namespace WordFlow.App.Bootstrap;

public sealed class LocalLifecycleLog
{
    private readonly object sync = new();

    public LocalLifecycleLog(string logsDirectory)
    {
        if (string.IsNullOrWhiteSpace(logsDirectory))
            throw new ArgumentException("A logs directory is required.", nameof(logsDirectory));
        Directory.CreateDirectory(logsDirectory);
        Path = System.IO.Path.Combine(logsDirectory, "lifecycle.log");
    }

    public string Path { get; }

    public void Write(string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            throw new ArgumentException("An event name is required.", nameof(eventName));
        if (eventName.Contains('\r', StringComparison.Ordinal) || eventName.Contains('\n', StringComparison.Ordinal))
            throw new ArgumentException("An event name must fit on one line.", nameof(eventName));
        string line = $"{DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)} {eventName}{Environment.NewLine}";
        lock (sync) File.AppendAllText(Path, line);
    }
}
