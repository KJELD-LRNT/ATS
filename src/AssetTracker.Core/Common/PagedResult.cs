namespace AssetTracker.Core.Common;

/// <summary>One page of a server-paginated list.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
{
    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < PageCount;
}
