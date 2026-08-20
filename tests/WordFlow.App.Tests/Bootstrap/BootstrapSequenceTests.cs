using WordFlow.App.Bootstrap;

namespace WordFlow.App.Tests.Bootstrap;

public sealed class BootstrapSequenceTests
{
    [Fact]
    public async Task RunAsync_executes_storage_startup_before_creating_the_card()
    {
        List<string> calls = [];

        await BootstrapSequence.RunAsync(
            () => calls.Add("directories"),
            _ => { calls.Add("verify"); return Task.CompletedTask; },
            _ => { calls.Add("migrate"); return Task.CompletedTask; },
            _ => { calls.Add("card"); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(["directories", "verify", "migrate", "card"], calls);
    }

    [Fact]
    public async Task RunAsync_does_not_migrate_or_create_UI_after_corpus_failure()
    {
        List<string> calls = [];

        await Assert.ThrowsAsync<CorpusIntegrityException>(() => BootstrapSequence.RunAsync(
            () => calls.Add("directories"),
            _ => throw new CorpusIntegrityException("invalid"),
            _ => { calls.Add("migrate"); return Task.CompletedTask; },
            _ => { calls.Add("card"); return Task.CompletedTask; },
            CancellationToken.None));

        Assert.Equal(["directories"], calls);
    }

    [Fact]
    public async Task RunAsync_awaits_asynchronous_card_initialization()
    {
        var releaseCard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var bootstrap = BootstrapSequence.RunAsync(
            () => { },
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            async _ => await releaseCard.Task,
            CancellationToken.None);

        Assert.False(bootstrap.IsCompleted);
        releaseCard.SetResult();
        await bootstrap;
    }
}
