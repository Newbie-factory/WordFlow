namespace WordFlow.App.Bootstrap;

public static class BootstrapSequence
{
    public static async Task RunAsync(
        Action initializeDirectories,
        Func<CancellationToken, Task> verifyCorpus,
        Func<CancellationToken, Task> migrateUserDatabase,
        Action createCard,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initializeDirectories);
        ArgumentNullException.ThrowIfNull(verifyCorpus);
        ArgumentNullException.ThrowIfNull(migrateUserDatabase);
        ArgumentNullException.ThrowIfNull(createCard);
        cancellationToken.ThrowIfCancellationRequested();
        initializeDirectories();
        await verifyCorpus(cancellationToken).ConfigureAwait(true);
        await migrateUserDatabase(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        createCard();
    }
}
