using System.Linq.Expressions;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AssetTracker.Core.Time;

/// <summary>A site's local "now" in its own timezone.</summary>
public sealed record SiteDay(int SiteId, TimeZoneInfo TimeZone, DateTimeOffset LocalNow)
{
    public DateOnly Today => DateOnly.FromDateTime(LocalNow.DateTime);
    public TimeOnly TimeOfDay => TimeOnly.FromDateTime(LocalNow.DateTime);
}

/// <summary>
/// Per-site calendar (WP4.2). Every calendar-day decision about a loan (due-date validation, the overdue rule in
/// <see cref="OverduePolicy"/>, days overdue, reminder time and the reminder dedupe date) uses the local day of
/// <b>the loan's site</b>: a tagged loan uses its asset's <i>current</i> site, a pool loan its pool's site.
/// <para>
/// A site's timezone is <see cref="Site.TimeZoneId"/>; when empty, the default <see cref="ClockOptions.TimeZoneId"/>
/// (<c>Clock:TimeZoneId</c>, read live through <see cref="IOptionsMonitor{TOptions}"/>). When neither is set the
/// call fails with <see cref="InvalidOperationException"/> rather than silently guessing a zone. IANA and Windows
/// ids are both accepted. Site timezone ids and resolved zones are cached for the lifetime of the (scoped) instance.
/// </para>
/// Instants are still stored and compared in UTC (<see cref="IClock.UtcNow"/>); only calendar days are local.
/// </summary>
public class SiteCalendar(AppDbContext db, IClock clock, IOptionsMonitor<ClockOptions> options)
{
    private Dictionary<int, string?>? _siteZoneIds;
    private readonly Dictionary<string, TimeZoneInfo> _zones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The site of a loan as a query expression: the asset's current site, else the pool's site.</summary>
    public static readonly Expression<Func<Loan, int>> LoanSiteId =
        l => l.AssetId != null ? l.Asset!.SiteId : l.Pool!.SiteId;

    /// <summary>
    /// The default timezone (<c>Clock:TimeZoneId</c>). Throws when it is not configured or not a known zone.
    /// Use directly only for things that belong to no site (e.g. an export's file name).
    /// </summary>
    public TimeZoneInfo DefaultTimeZone
    {
        get
        {
            var id = options.CurrentValue.TimeZoneId;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException(
                    "Clock:TimeZoneId is not configured. Set it to the client's agreed default timezone " +
                    "(sites without their own timezone use it).");
            return Resolve(id, "Clock:TimeZoneId");
        }
    }

    /// <summary>Today in the default timezone. Not for loan logic: use <see cref="TodayForAsync(Loan, CancellationToken)"/>.</summary>
    public DateOnly DefaultToday => LocalDate(clock.UtcNow, DefaultTimeZone);

    /// <summary>Finds a timezone by IANA or Windows id; false when the id is empty or unknown on this server.</summary>
    public static bool TryFindTimeZone(string? id, out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    public static DateOnly LocalDate(DateTimeOffset utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);

    public async Task<TimeZoneInfo> TimeZoneForAsync(int siteId, CancellationToken ct = default)
    {
        var zones = await SiteZoneIdsAsync(siteId, ct);
        return zones.TryGetValue(siteId, out var id) && !string.IsNullOrWhiteSpace(id)
            ? Resolve(id, $"site {siteId}")
            : DefaultTimeZone;
    }

    /// <summary>The site's local date and time now.</summary>
    public async Task<DateTimeOffset> LocalTimeForAsync(int siteId, CancellationToken ct = default) =>
        TimeZoneInfo.ConvertTime(clock.UtcNow, await TimeZoneForAsync(siteId, ct));

    public async Task<DateOnly> TodayForAsync(int siteId, CancellationToken ct = default) =>
        DateOnly.FromDateTime((await LocalTimeForAsync(siteId, ct)).DateTime);

    /// <summary>Today at the loan's site: a tagged loan's asset's current site, a pool loan's pool's site.</summary>
    public async Task<DateOnly> TodayForAsync(Loan loan, CancellationToken ct = default) =>
        await TodayForAsync(await SiteIdOfAsync(loan, ct), ct);

    /// <summary>The loan's site id (navigations are used when loaded, otherwise looked up).</summary>
    public async Task<int> SiteIdOfAsync(Loan loan, CancellationToken ct = default)
    {
        if (loan.Asset is { } a) return a.SiteId;
        if (loan.Pool is { } p) return p.SiteId;
        if (loan.AssetId is { } aid)
            return await db.Assets.AsNoTracking().Where(x => x.Id == aid).Select(x => x.SiteId).SingleAsync(ct);
        if (loan.PoolId is { } pid)
            return await db.Pools.AsNoTracking().Where(x => x.Id == pid).Select(x => x.SiteId).SingleAsync(ct);
        throw new InvalidOperationException($"Loan {loan.Id} has neither an asset nor a pool.");
    }

    /// <summary>Every site's local now (all sites, active or not: loans can sit on an inactive site).</summary>
    public async Task<IReadOnlyList<SiteDay>> AllSitesAsync(CancellationToken ct = default)
    {
        var zones = await SiteZoneIdsAsync(null, ct);
        var now = clock.UtcNow;
        var result = new List<SiteDay>(zones.Count);
        foreach (var (siteId, id) in zones.OrderBy(z => z.Key))
        {
            var zone = string.IsNullOrWhiteSpace(id) ? DefaultTimeZone : Resolve(id, $"site {siteId}");
            result.Add(new SiteDay(siteId, zone, TimeZoneInfo.ConvertTime(now, zone)));
        }
        return result;
    }

    /// <summary>
    /// Query predicate: the loan has a due date and <see cref="OverduePolicy"/> considers it overdue on its site's
    /// local today. It does not check that the loan is open; callers add <c>ClosedAt == null</c>.
    /// </summary>
    public async Task<Expression<Func<Loan, bool>>> OverduePredicateAsync(CancellationToken ct = default) =>
        OverduePredicate(await AllSitesAsync(ct));

    /// <summary>
    /// The overdue predicate for the given sites only (loans at other sites never match). Sites that share a local
    /// date share one clause, so the SQL has only a few clauses however many sites exist.
    /// </summary>
    public static Expression<Func<Loan, bool>> OverduePredicate(IEnumerable<SiteDay> sites)
    {
        var param = LoanSiteId.Parameters[0];
        Expression body = Expression.Constant(false);
        foreach (var group in sites.GroupBy(s => s.Today).OrderBy(g => g.Key))
        {
            var ids = group.Select(s => s.SiteId).ToList();
            var latestDue = OverduePolicy.LatestOverdueDueDate(group.Key);
            Expression<Func<Loan, bool>> clause = l =>
                l.DueDate != null && l.DueDate <= latestDue &&
                ids.Contains(l.AssetId != null ? l.Asset!.SiteId : l.Pool!.SiteId);
            body = Expression.OrElse(body, new ParameterSwap(clause.Parameters[0], param).Visit(clause.Body));
        }
        return Expression.Lambda<Func<Loan, bool>>(body, param);
    }

    private async Task<Dictionary<int, string?>> SiteZoneIdsAsync(int? mustContain, CancellationToken ct)
    {
        if (_siteZoneIds is null || (mustContain is { } id && !_siteZoneIds.ContainsKey(id)))
            _siteZoneIds = await db.Sites.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.TimeZoneId, ct);
        return _siteZoneIds;
    }

    private TimeZoneInfo Resolve(string id, string owner)
    {
        id = id.Trim();
        if (_zones.TryGetValue(id, out var cached)) return cached;
        if (!TryFindTimeZone(id, out var zone))
            throw new InvalidOperationException($"Timezone '{id}' configured for {owner} is not known on this server.");
        _zones[id] = zone!;
        return zone!;
    }

    private sealed class ParameterSwap(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
