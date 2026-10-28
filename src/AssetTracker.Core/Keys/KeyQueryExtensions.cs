using System.Linq.Expressions;
using AssetTracker.Core.Common;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Keys;

internal static class KeyQueryExtensions
{
    public static async Task<T> RequiredAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken ct)
        where T : class => await query.SingleOrDefaultAsync(predicate, ct)
            ?? throw new DomainException("keys.not_found", "Record not found. Refresh the page and select an existing record.");
}
