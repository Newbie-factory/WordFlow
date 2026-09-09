using WordFlow.Application.Ports;

namespace WordFlow.Application.Learning;

public sealed record AddToNotebookRequest(Guid WordId, DateTimeOffset AddedAtUtc);

public sealed record AddToNotebookResult(bool Added, bool AlreadyPresent);

public sealed class AddToNotebook
{
    private readonly INotebookRepository notebook;
    private readonly IVocabularyRepository vocabulary;

    public AddToNotebook(INotebookRepository notebook, IVocabularyRepository vocabulary)
    {
        this.notebook = notebook ?? throw new ArgumentNullException(nameof(notebook));
        this.vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
    }

    public async Task<UseCaseResult<AddToNotebookResult>> HandleAsync(AddToNotebookRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WordId == Guid.Empty)
            return new NotFound<AddToNotebookResult>("A word ID cannot be empty.");
        try
        {
            var word = await vocabulary.GetWordAsync(request.WordId, ct).ConfigureAwait(false);
            if (word is null)
                return new NotFound<AddToNotebookResult>($"Word {request.WordId:D} was not found.");
            if (await notebook.ContainsAsync(request.WordId, ct).ConfigureAwait(false))
                return new Success<AddToNotebookResult>(new(false, true));
            bool added = await notebook.AddAsync(request.WordId, request.AddedAtUtc, ct).ConfigureAwait(false);
            return new Success<AddToNotebookResult>(new(added, false));
        }
        catch (TransientStorageException exception)
        {
            return new StorageFailure<AddToNotebookResult>(exception.Message);
        }
    }
}