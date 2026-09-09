using WordFlow.Application.Ports;

namespace WordFlow.Application.Learning;

public sealed record RemoveFromNotebookRequest(Guid WordId);

public sealed class RemoveFromNotebook
{
    private readonly INotebookRepository notebook;

    public RemoveFromNotebook(INotebookRepository notebook) =>
        this.notebook = notebook ?? throw new ArgumentNullException(nameof(notebook));

    public async Task<UseCaseResult<bool>> HandleAsync(RemoveFromNotebookRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WordId == Guid.Empty)
            return new NotFound<bool>("A word ID cannot be empty.");
        try
        {
            bool removed = await notebook.RemoveAsync(request.WordId, ct).ConfigureAwait(false);
            return new Success<bool>(removed);
        }
        catch (TransientStorageException exception)
        {
            return new StorageFailure<bool>(exception.Message);
        }
    }
}