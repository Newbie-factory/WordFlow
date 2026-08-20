using WordFlow.App.Bootstrap;

namespace WordFlow.App.Tests.Bootstrap;

public sealed class ApplicationExitCoordinatorTests
{
    [Fact]
    public async Task Normal_exit_is_serialized_and_releases_coordinator_even_when_component_disposal_fails()
    {
        List<string> calls = [];
        List<Exception> failures = [];
        var exit = new ApplicationExitCoordinator(
            [
                () => { calls.Add("tray"); throw new InvalidOperationException("tray dispose"); },
                () => { calls.Add("services"); return ValueTask.CompletedTask; },
                () => { calls.Add("coordinator"); return ValueTask.CompletedTask; },
            ],
            code => calls.Add($"shutdown:{code}"), failures.Add);

        Task first = exit.ExitAsync(0);
        Task concurrent = exit.ExitAsync(9);
        await Task.WhenAll(first, concurrent);

        Assert.Same(first, concurrent);
        Assert.Equal(["tray", "services", "coordinator", "shutdown:0"], calls);
        Assert.Single(failures);
        Assert.Contains("tray dispose", failures[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fatal_startup_exit_attempts_every_cleanup_and_shutdown_without_rethrowing()
    {
        List<string> calls = [];
        List<Exception> failures = [];
        var exit = new ApplicationExitCoordinator(
            [
                () => { calls.Add("lifetime"); throw new AggregateException("cancel callbacks"); },
                () => { calls.Add("coordinator"); throw new IOException("release fault"); },
                () => { calls.Add("lifetime-dispose"); return ValueTask.CompletedTask; },
            ],
            code => calls.Add($"shutdown:{code}"), failures.Add);

        await exit.ExitAsync(2);

        Assert.Equal(["lifetime", "coordinator", "lifetime-dispose", "shutdown:2"], calls);
        Assert.Equal(2, failures.Count);
    }
}
