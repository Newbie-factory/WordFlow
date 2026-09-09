using WordFlow.Application.Ports;

namespace WordFlow.Application.Learning;

public sealed record NotebookItem(
    Guid WordId,
    string Lemma,
    string Phonetic,
    string Chinese,
    DateTimeOffset AddedAtUtc);

public sealed class GetNotebookEntries
{
    private readonly INotebookRepository notebook;
    private readonly IVocabularyRepository vocabulary;

    public GetNotebookEntries(INotebookRepository notebook, IVocabularyRepository vocabulary)
    {
        this.notebook = notebook ?? throw new ArgumentNullException(nameof(notebook));
        this.vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
    }

    public async Task<UseCaseResult<IReadOnlyList<NotebookItem>>> HandleAsync(CancellationToken ct)
    {
        try
        {
            var entries = await PagedReads.AllAsync(
                (page, token) => notebook.GetEntriesAsync(page, token),
                (offset, snapshot, token) => notebook.ProbeEntriesEndAsync(offset, snapshot, token), ct).ConfigureAwait(false);
            var items = new List<NotebookItem>(entries.Count);
            foreach (var entry in entries)
            {
                var word = await vocabulary.GetWordAsync(entry.WordId, ct).ConfigureAwait(false);
                items.Add(new NotebookItem(
                    entry.WordId,
                    word?.Lemma ?? entry.WordId.ToString("D"),
                    word?.Phonetic ?? "",
                    word?.Chinese ?? "",
                    entry.AddedAtUtc));
            }
            return new Success<IReadOnlyList<NotebookItem>>(items);
        }
        catch (TransientStorageException exception)
        {
            return new StorageFailure<IReadOnlyList<NotebookItem>>(exception.Message);
        }
    }
}