using System.Runtime.Versioning;
using System.Security.Principal;
using AssetTracker.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ad = System.DirectoryServices;

namespace AssetTracker.Infrastructure.Directory;

/// <summary>
/// AD query settings (config section <c>ActiveDirectory</c>). The search root and site attribute are Stage 0 facts
/// and have no default; the remaining attribute names default to standard AD schema names.
/// </summary>
public class ActiveDirectoryOptions
{
    public const string SectionName = "ActiveDirectory";

    /// <summary>LDAP path of the search base, e.g. <c>LDAP://OU=Staff,DC=corp,DC=example</c>. Required.</summary>
    public string SearchRoot { get; set; } = "";

    /// <summary>LDAP filter selecting in-scope users.</summary>
    public string Filter { get; set; } = "(&(objectCategory=person)(objectClass=user))";

    /// <summary>Page size for the paged search (must be at or below the server's MaxPageSize, usually 1000).</summary>
    public int PageSize { get; set; } = 500;

    /// <summary>Client-side timeout for the whole search; null = no client timeout.</summary>
    public TimeSpan? ClientTimeout { get; set; }

    /// <summary>Stable identity attribute. objectGUID (binary) is formatted as a lowercase GUID string.</summary>
    public string IdAttribute { get; set; } = "objectGUID";

    /// <summary>Attribute holding the designated site value mapped via <c>Site.DirectoryValue</c>. Required, no default.</summary>
    public string SiteAttribute { get; set; } = "";

    public string UsernameAttribute { get; set; } = "sAMAccountName";
    public string DisplayNameAttribute { get; set; } = "displayName";
    public string TitleAttribute { get; set; } = "title";
    public string MailAttribute { get; set; } = "mail";
    public string SidAttribute { get; set; } = "objectSid";
    public string UserAccountControlAttribute { get; set; } = "userAccountControl";
}

/// <summary>
/// Production <see cref="IDirectorySource"/>: a read-only, paged <see cref="Ad.DirectorySearcher"/> over the configured
/// search root (PageSize set so results are not truncated at the server's unpaged size limit). Runs under the
/// process identity (the managed service account). Any LDAP error propagates, so the sync run fails and the
/// previous snapshot is kept.
/// </summary>
[SupportedOSPlatform("windows")]
public class ActiveDirectorySource(IOptions<ActiveDirectoryOptions> options, ILogger<ActiveDirectorySource> logger)
    : IDirectorySource
{
    private const int AccountDisable = 0x2;

    public Task<IReadOnlyList<DirectoryEntry>> FetchAllUsersAsync(CancellationToken ct) =>
        Task.Run(() => Fetch(options.Value, ct), ct);

    private IReadOnlyList<DirectoryEntry> Fetch(ActiveDirectoryOptions o, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.SearchRoot))
            throw new InvalidOperationException("ActiveDirectory:SearchRoot is not configured.");
        if (string.IsNullOrWhiteSpace(o.SiteAttribute))
            throw new InvalidOperationException("ActiveDirectory:SiteAttribute is not configured.");
        if (string.IsNullOrWhiteSpace(o.IdAttribute))
            throw new InvalidOperationException("ActiveDirectory:IdAttribute is not configured.");
        if (o.PageSize <= 0)
            throw new InvalidOperationException("ActiveDirectory:PageSize must be positive.");

        using var root = new Ad.DirectoryEntry(o.SearchRoot);
        using var searcher = new Ad.DirectorySearcher(root, o.Filter)
        {
            SearchScope = Ad.SearchScope.Subtree,
            PageSize = o.PageSize,
            SizeLimit = 0,
            Asynchronous = false,
            CacheResults = false,
        };
        if (o.ClientTimeout is { } timeout) searcher.ClientTimeout = timeout;
        searcher.PropertiesToLoad.AddRange(
        [
            o.IdAttribute, o.SidAttribute, o.UsernameAttribute, o.DisplayNameAttribute, o.TitleAttribute,
            o.MailAttribute, o.SiteAttribute, o.UserAccountControlAttribute,
        ]);

        var result = new List<DirectoryEntry>();
        using (var found = searcher.FindAll())
        {
            foreach (Ad.SearchResult r in found)
            {
                ct.ThrowIfCancellationRequested();
                var uac = First(r, o.UserAccountControlAttribute) is { } u ? Convert.ToInt32(u) : (int?)null;
                result.Add(new DirectoryEntry(
                    DirectoryId: FormatId(First(r, o.IdAttribute)),
                    Sid: First(r, o.SidAttribute) is byte[] sid ? new SecurityIdentifier(sid, 0).Value : First(r, o.SidAttribute)?.ToString(),
                    Username: First(r, o.UsernameAttribute)?.ToString(),
                    DisplayName: First(r, o.DisplayNameAttribute)?.ToString(),
                    JobTitle: First(r, o.TitleAttribute)?.ToString(),
                    Email: First(r, o.MailAttribute)?.ToString(),
                    SiteValue: First(r, o.SiteAttribute)?.ToString(),
                    // A missing userAccountControl is treated as disabled (cannot receive new loans);
                    // the sync's drop-fraction check rejects a run where this would deactivate many people.
                    Enabled: uac is { } flags && (flags & AccountDisable) == 0));
            }
        }
        logger.LogInformation("Fetched {Count} directory entries from {SearchRoot}.", result.Count, o.SearchRoot);
        return result;
    }

    private static object? First(Ad.SearchResult r, string attribute) =>
        r.Properties.Contains(attribute) && r.Properties[attribute].Count > 0 ? r.Properties[attribute][0] : null;

    private static string? FormatId(object? value) => value switch
    {
        null => null,
        byte[] { Length: 16 } guid => new Guid(guid).ToString("D"),
        byte[] bytes => Convert.ToHexString(bytes),
        _ => value.ToString(),
    };
}

/// <summary>Registered on non-Windows hosts (development only): every sync run fails with a clear error.</summary>
public class UnsupportedDirectorySource : IDirectorySource
{
    public Task<IReadOnlyList<DirectoryEntry>> FetchAllUsersAsync(CancellationToken ct) =>
        throw new PlatformNotSupportedException("Active Directory sync is only supported on Windows.");
}
