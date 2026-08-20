namespace WordFlow.App.Bootstrap;

public sealed class ApplicationExitCoordinator
{
    private readonly object sync = new();
    private readonly IReadOnlyList<Func<ValueTask>> cleanupSteps;
    private readonly Action<int> shutdown;
    private readonly Action<Exception> reportFailure;
    private Task? exitTask;

    public ApplicationExitCoordinator(
        IEnumerable<Func<ValueTask>> cleanupSteps,
        Action<int> shutdown,
        Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(cleanupSteps);
        this.cleanupSteps = cleanupSteps.ToArray();
        if (this.cleanupSteps.Any(step => step is null))
            throw new ArgumentException("Cleanup steps cannot contain null.", nameof(cleanupSteps));
        this.shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        this.reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
    }

    public Task ExitAsync(int exitCode)
    {
        lock (sync) return exitTask ??= ExitCoreAsync(exitCode);
    }

    private async Task ExitCoreAsync(int exitCode)
    {
        foreach (Func<ValueTask> cleanup in cleanupSteps)
        {
            try { await cleanup().ConfigureAwait(true); }
            catch (Exception exception) { Report(exception); }
        }

        try { shutdown(exitCode); }
        catch (Exception exception) { Report(exception); }
    }

    private void Report(Exception exception)
    {
        try { reportFailure(exception); }
        catch { }
    }
}
