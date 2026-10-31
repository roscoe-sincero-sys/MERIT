namespace Merit.Application.Data;

/// <summary>Recover an offset emptied by a concurrent change without mistaking it for an empty dataset.</summary>
public static class Paging
{
    public static async Task<(IReadOnlyList<T> Rows, int Total, int Page)> ReadAsync<T>(int page, int size,
        Func<int, Task<IReadOnlyList<T>>> read, Func<T, int> count)
    {
        var rows = await read(page);
        if (rows.Count > 0) return (rows, count(rows[0]), page);
        if (page == 1) return (rows, 0, 1);

        // Obtain the total independently of the requested offset, within the same authorized filters.
        var first = await read(1);
        var total = first.Count == 0 ? 0 : count(first[0]);
        var last = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        return (last == 1 ? first : await read(last), total, last);
    }
}
