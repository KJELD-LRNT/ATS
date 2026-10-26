using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Common;

/// <summary>One page of a server-paginated list.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int PageCount => TotalCount == 0 ? 1 : (TotalCount + PageSize - 1) / PageSize;
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < PageCount;
}

public static class Paging
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 200;

    /// <summary>Counts and fetches one page of an already-ordered query. Page numbers are 1-based and clamped.</summary>
    public static async Task<PagedResult<T>> ToPageAsync<T>(this IQueryable<T> ordered, int page, int pageSize,
        CancellationToken ct = default)
    {
        pageSize = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        page = Math.Max(page, 1);
        var total = await ordered.CountAsync(ct);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<T>(items, total, page, pageSize);
    }

    /// <summary>Builds a LIKE "contains" pattern with wildcard characters in user input escaped (escape char '\').</summary>
    public static string ContainsPattern(string term) =>
        "%" + term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[") + "%";
}
