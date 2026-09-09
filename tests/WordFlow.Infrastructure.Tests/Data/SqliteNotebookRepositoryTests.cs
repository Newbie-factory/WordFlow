using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Data;

namespace WordFlow.Infrastructure.Tests.Data;

public sealed class SqliteNotebookRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wordflow-notebook-{Guid.NewGuid():N}");

    public SqliteNotebookRepositoryTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Add_remove_contains_and_page_roundtrip_is_durable()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "user.db"));
        await new MigrationRunner(factory).MigrateAsync(default);
        var repository = new SqliteNotebookRepository(factory);
        var added = DateTimeOffset.UtcNow;
        var wordA = Guid.NewGuid();
        var wordB = Guid.NewGuid();

        Assert.True(await repository.AddAsync(wordA, added, default));
        Assert.False(await repository.AddAsync(wordA, added, default));
        Assert.True(await repository.AddAsync(wordB, added.AddMinutes(1), default));
        Assert.True(await repository.ContainsAsync(wordA, default));
        Assert.False(await repository.ContainsAsync(Guid.NewGuid(), default));

        var page = await repository.GetEntriesAsync(new PageRequest(0, 500), default);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, item => item.WordId == wordA);
        Assert.Contains(page.Items, item => item.WordId == wordB);

        Assert.True(await repository.RemoveAsync(wordA, default));
        Assert.False(await repository.ContainsAsync(wordA, default));
        var afterRemove = await repository.GetEntriesAsync(new PageRequest(0, 500), default);
        Assert.Equal(1, afterRemove.TotalCount);
        Assert.Equal(wordB, Assert.Single(afterRemove.Items).WordId);
    }

    [Fact]
    public async Task Notebook_requires_a_migration_managed_user_database()
    {
        var repository = new SqliteNotebookRepository(new SqliteConnectionFactory(Path.Combine(directory, "empty-user.db")));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => repository.AddAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public async Task Empty_word_ids_are_rejected()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "user.db"));
        await new MigrationRunner(factory).MigrateAsync(default);
        var repository = new SqliteNotebookRepository(factory);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddAsync(Guid.Empty, DateTimeOffset.UtcNow, default));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.RemoveAsync(Guid.Empty, default));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ContainsAsync(Guid.Empty, default));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}