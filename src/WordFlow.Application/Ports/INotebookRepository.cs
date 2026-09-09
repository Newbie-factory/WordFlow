namespace WordFlow.Application.Ports;

public sealed record NotebookEntry(Guid WordId, DateTimeOffset AddedAtUtc);

public interface INotebookRepository
{
    Task<bool> AddAsync(Guid wordId, DateTimeOffset addedAtUtc, CancellationToken ct);

    Task<bool> RemoveAsync(Guid wordId, CancellationToken ct);

    Task<bool> ContainsAsync(Guid wordId, CancellationToken ct);

    Task<Page<NotebookEntry>> GetEntriesAsync(PageRequest page, CancellationToken ct);

    Task<ExhaustionProbe> ProbeEntriesEndAsync(int offset, string snapshotId, CancellationToken ct);
}