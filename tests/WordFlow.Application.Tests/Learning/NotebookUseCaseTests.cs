using WordFlow.Application;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;

namespace WordFlow.Application.Tests.Learning;

public sealed class NotebookUseCaseTests
{
    [Fact]
    public async Task Add_to_notebook_adds_a_corpus_word_once_and_deduplicates()
    {
        var word = Word(1);
        var notebook = new FakeNotebookRepository();
        var handler = new AddToNotebook(notebook, new FakeVocabulary([word]));

        var first = await handler.HandleAsync(new AddToNotebookRequest(Id(1), DateTimeOffset.UtcNow), default);
        var second = await handler.HandleAsync(new AddToNotebookRequest(Id(1), DateTimeOffset.UtcNow), default);

        Assert.IsType<Success<AddToNotebookResult>>(first);
        Assert.True(((Success<AddToNotebookResult>)first).Value.Added);
        Assert.False(((Success<AddToNotebookResult>)first).Value.AlreadyPresent);
        Assert.IsType<Success<AddToNotebookResult>>(second);
        Assert.True(((Success<AddToNotebookResult>)second).Value.AlreadyPresent);
        Assert.False(((Success<AddToNotebookResult>)second).Value.Added);
        Assert.Single(notebook.Entries);
    }

    [Fact]
    public async Task Add_to_notebook_rejects_words_missing_from_the_corpus()
    {
        var handler = new AddToNotebook(new FakeNotebookRepository(), new FakeVocabulary([Word(1)]));

        var result = await handler.HandleAsync(new AddToNotebookRequest(Id(999), DateTimeOffset.UtcNow), default);

        Assert.IsType<NotFound<AddToNotebookResult>>(result);
    }

    [Fact]
    public async Task Remove_from_notebook_removes_an_existing_entry()
    {
        var notebook = new FakeNotebookRepository();
        await notebook.AddAsync(Id(1), DateTimeOffset.UtcNow, default);
        var handler = new RemoveFromNotebook(notebook);

        var result = await handler.HandleAsync(new RemoveFromNotebookRequest(Id(1)), default);

        Assert.IsType<Success<bool>>(result);
        Assert.True(((Success<bool>)result).Value);
        Assert.Empty(notebook.Entries);
    }

    [Fact]
    public async Task Get_notebook_entries_joins_corpus_word_details()
    {
        var notebook = new FakeNotebookRepository();
        var addedAt = DateTimeOffset.UtcNow;
        await notebook.AddAsync(Id(1), addedAt, default);
        var handler = new GetNotebookEntries(notebook, new FakeVocabulary([Word(1)]));

        var result = await handler.HandleAsync(default);

        var success = Assert.IsType<Success<IReadOnlyList<NotebookItem>>>(result);
        var item = Assert.Single(success.Value);
        Assert.Equal(Id(1), item.WordId);
        Assert.Equal("word-001", item.Lemma);
        Assert.Equal(addedAt, item.AddedAtUtc);
    }

    private static VocabularyWord Word(int value) => new(Id(value), $"word-{value:000}", value, true,
        Phonetic: $"/sample-{value}/", Chinese: $"中文释义 {value}");

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private sealed class FakeNotebookRepository : INotebookRepository
    {
        public List<(Guid WordId, DateTimeOffset AddedAt)> Entries { get; } = [];

        public Task<bool> AddAsync(Guid wordId, DateTimeOffset addedAtUtc, CancellationToken ct)
        {
            if (Entries.Any(e => e.WordId == wordId)) return Task.FromResult(false);
            Entries.Add((wordId, addedAtUtc));
            return Task.FromResult(true);
        }

        public Task<bool> RemoveAsync(Guid wordId, CancellationToken ct)
        {
            int removed = Entries.RemoveAll(e => e.WordId == wordId);
            return Task.FromResult(removed > 0);
        }

        public Task<bool> ContainsAsync(Guid wordId, CancellationToken ct) =>
            Task.FromResult(Entries.Any(e => e.WordId == wordId));

        public Task<Page<NotebookEntry>> GetEntriesAsync(PageRequest page, CancellationToken ct)
        {
            var items = Entries.OrderByDescending(e => e.AddedAt)
                .Skip(page.Offset).Take(page.Limit)
                .Select(e => new NotebookEntry(e.WordId, e.AddedAt)).ToArray();
            return Task.FromResult(new Page<NotebookEntry>(items, Entries.Count, page.Offset + items.Length < Entries.Count, "notebook:test"));
        }

        public Task<ExhaustionProbe> ProbeEntriesEndAsync(int offset, string snapshotId, CancellationToken ct) =>
            Task.FromResult(new ExhaustionProbe(offset >= Entries.Count, "notebook:test"));
    }

    private sealed class FakeVocabulary(IEnumerable<VocabularyWord> words) : IVocabularyRepository
    {
        private readonly VocabularyWord[] all = words.ToArray();

        public Task<Page<VocabularyWord>> GetWordsAsync(PageRequest page, CancellationToken ct) =>
            Task.FromResult(new Page<VocabularyWord>(all.Skip(page.Offset).Take(page.Limit).ToArray(), all.Length, false, "words:test"));

        public Task<ExhaustionProbe> ProbeWordsEndAsync(int offset, string snapshotId, CancellationToken ct) =>
            Task.FromResult(new ExhaustionProbe(offset >= all.Length, "words:test"));

        public Task<VocabularyWord?> GetWordAsync(Guid wordId, CancellationToken ct) =>
            Task.FromResult(all.SingleOrDefault(x => x.WordId == wordId));

        public Task<WordEntry?> GetWordEntryAsync(Guid wordId, CancellationToken ct) =>
            Task.FromResult(all.SingleOrDefault(x => x.WordId == wordId) is { } word
                ? new WordEntry(word.WordId, word.Lemma, word.Phonetic, word.Chinese, word.PrimaryDefinition, word.PrimaryPartOfSpeech,
                    null, null, null, word.FrequencyRank, null, null, null)
                : null);

        public Task<Page<VocabularySense>> GetSensesAsync(Guid wordId, PageRequest page, CancellationToken ct) =>
            Task.FromResult(new Page<VocabularySense>([], 0, false, "senses:test"));

        public Task<ExhaustionProbe> ProbeSensesEndAsync(Guid wordId, int offset, string snapshotId, CancellationToken ct) =>
            Task.FromResult(new ExhaustionProbe(true, "senses:test"));
    }
}