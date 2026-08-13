namespace WordFlow.Application.Ports;

internal static class PagedReads
{
    private const int MaximumPageSize = 500;

    public static async Task<IReadOnlyList<T>> AllAsync<T>(
        Func<PageRequest, CancellationToken, Task<Page<T>>> read,
        CancellationToken ct)
    {
        var items = new List<T>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await read(new PageRequest(items.Count, MaximumPageSize), ct).ConfigureAwait(false);
            if (page.Items.Count == 0) break;
            items.AddRange(page.Items);
            if (items.Count >= page.TotalCount) break;
        }
        return items;
    }
}
