namespace WordFlow.Application.Ports;

internal static class PagedReads
{
    private const int MaximumPageSize = 500;

    public static async Task<IReadOnlyList<T>> AllAsync<T>(
        Func<PageRequest, CancellationToken, Task<Page<T>>> read,
        CancellationToken ct)
    {
        var items = new List<T>();
        var seen = new HashSet<T>();
        int? total = null;
        string? snapshotId = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await read(new PageRequest(items.Count, MaximumPageSize), ct).ConfigureAwait(false);
            if (page.HasMore is null || string.IsNullOrWhiteSpace(page.SnapshotId))
                throw new InvalidDataException("Complete paging requires a continuation marker and stable snapshot identity.");
            if (page.TotalCount < 0) throw new InvalidDataException("A page total cannot be negative.");
            total ??= page.TotalCount;
            if (page.TotalCount != total) throw new InvalidDataException("The page total changed during a complete read.");
            snapshotId ??= page.SnapshotId;
            if (page.SnapshotId != snapshotId) throw new InvalidDataException("The repository snapshot changed during a complete read.");
            if (page.Items.Count > MaximumPageSize) throw new InvalidDataException("A repository returned an overfull page.");
            if (page.Items.Count == 0)
            {
                if (items.Count != total) throw new InvalidDataException("A repository returned an empty page before its declared total.");
                break;
            }
            var moreExpected = items.Count + page.Items.Count < total;
            if (page.HasMore.Value != moreExpected)
                throw new InvalidDataException("A repository's continuation marker disagrees with its total.");
            foreach (var item in page.Items)
            {
                if (!seen.Add(item)) throw new InvalidDataException("A repository returned a duplicate page item.");
                items.Add(item);
            }
            if (items.Count > total) throw new InvalidDataException("A repository underreported its total count.");
            if (items.Count == total) break;
        }
        return items;
    }
}
