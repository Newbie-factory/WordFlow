using System.Diagnostics;
using System.Runtime.Versioning;

namespace WordFlow.Infrastructure.Tests.Windows;

[SupportedOSPlatform("windows")]
public sealed class SingleInstanceProcessHarnessTests : IDisposable
{
    private readonly string stateDirectory = Path.Combine(
        Path.GetTempPath(), $"WordFlow.ProcessHarness.{Guid.NewGuid():N}");

    [Fact]
    public async Task Executables_enforce_primary_only_bootstrap_secondary_activation_and_crash_recovery()
    {
        Directory.CreateDirectory(stateDirectory);
        string applicationId = $"WordFlow.ProcessHarness.{Guid.NewGuid():N}";
        string host = FindHostExecutable();
        string log = Path.Combine(stateDirectory, "events.log");
        Process? primary = null, secondary = null, replacement = null;
        try
        {
            primary = StartHost(host, applicationId);
            await WaitForLineAsync(log, $"ready pid={primary.Id}");

            secondary = StartHost(host, applicationId);
            await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, secondary.ExitCode);
            await WaitForLineAsync(log, $"activation pid={primary.Id}");
            Assert.Single(await File.ReadAllLinesAsync(log),
                line => line.StartsWith("bootstrap ", StringComparison.Ordinal));

            primary.Kill(entireProcessTree: false);
            await primary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            replacement = StartHost(host, applicationId);
            await WaitForLineAsync(log, $"ready pid={replacement.Id}");
            Assert.Equal(2, (await File.ReadAllLinesAsync(log)).Count(
                line => line.StartsWith("bootstrap ", StringComparison.Ordinal)));

            await File.WriteAllTextAsync(Path.Combine(stateDirectory, "stop"), "stop");
            await replacement.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, replacement.ExitCode);
        }
        finally
        {
            StopExact(primary);
            StopExact(secondary);
            StopExact(replacement);
        }

        Assert.All(new[] { primary, secondary, replacement }.Where(process => process is not null),
            process => Assert.True(process!.HasExited));
    }

    private Process StartHost(string host, string applicationId)
    {
        var startInfo = new ProcessStartInfo(host)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add(applicationId);
        startInfo.ArgumentList.Add(stateDirectory);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start process harness.");
    }

    private static async Task WaitForLineAsync(string path, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(path) && (await File.ReadAllLinesAsync(path, timeout.Token)).Contains(expected)) return;
            }
            catch (IOException) { }
            await Task.Delay(50, timeout.Token);
        }
        throw new TimeoutException($"Timed out waiting for '{expected}'.");
    }

    private static string FindHostExecutable()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WordFlow.sln")))
            directory = directory.Parent;
        string root = directory?.FullName ?? throw new DirectoryNotFoundException("WordFlow repository root not found.");
        string outputRoot = Path.Combine(root, "tests", "WordFlow.SingleInstance.TestHost", "bin");
        string? executable = new[] { "Release", "Debug" }
            .Select(configuration => Path.Combine(outputRoot, configuration,
                "net8.0-windows10.0.19041.0", "WordFlow.SingleInstance.TestHost.exe"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return executable ?? throw new FileNotFoundException("The single-instance process test host was not built.");
    }

    private static void StopExact(Process? process)
    {
        if (process is null || process.HasExited) return;
        process.Kill(entireProcessTree: false);
        process.WaitForExit(5000);
    }

    public void Dispose()
    {
        if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, recursive: true);
    }
}
