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
            () => calls.Add("card"),
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
            () => calls.Add("card"),
            CancellationToken.None));

        Assert.Equal(["directories"], calls);
    }
}
