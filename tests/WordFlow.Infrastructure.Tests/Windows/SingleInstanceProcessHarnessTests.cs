using System.Diagnostics;
using System.Reflection;
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
        string host = FindHostBuild().Executable;
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

    [Fact]
    public void Host_executable_is_resolved_from_the_current_build_configuration()
    {
        (string configuration, string executable) = FindHostBuild();
        string currentConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException("Test output configuration directory not found.");

        Assert.Equal(currentConfiguration, configuration);
        Assert.Contains(
            Path.Combine("bin", currentConfiguration, "net8.0-windows10.0.19041.0"),
            executable,
            StringComparison.OrdinalIgnoreCase);
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

    private static (string Configuration, string Executable) FindHostBuild()
    {
        IReadOnlyDictionary<string, string?> metadata = typeof(SingleInstanceProcessHarnessTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        string configuration = metadata.GetValueOrDefault("SingleInstanceTestHostConfiguration")
            ?? throw new InvalidOperationException("The test-host build configuration was not supplied by MSBuild.");
        string executable = metadata.GetValueOrDefault("SingleInstanceTestHostPath")
            ?? throw new InvalidOperationException("The test-host output path was not supplied by MSBuild.");
        if (!File.Exists(executable))
            throw new FileNotFoundException("The single-instance process test host was not built for this test configuration.", executable);
        return (configuration, executable);
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
