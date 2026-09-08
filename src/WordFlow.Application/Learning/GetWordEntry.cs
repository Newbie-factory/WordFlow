using WordFlow.Application.Ports;

namespace WordFlow.Application.Learning;

public sealed record GetWordEntryRequest(Guid WordId);

public sealed class GetWordEntry
{
    private readonly IVocabularyRepository vocabulary;

    public GetWordEntry(IVocabularyRepository vocabulary) =>
        this.vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));

    public async Task<UseCaseResult<WordEntry>> HandleAsync(GetWordEntryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WordId == Guid.Empty)
            return new NotFound<WordEntry>("A word ID cannot be empty.");
        try
        {
            var entry = await vocabulary.GetWordEntryAsync(request.WordId, ct).ConfigureAwait(false);
            if (entry is null)
                return new NotFound<WordEntry>($"Word {request.WordId:D} was not found.");
            return new Success<WordEntry>(entry);
        }
        catch (TransientStorageException exception)
        {
            return new StorageFailure<WordEntry>(exception.Message);
        }
    }
}