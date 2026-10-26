using AssetTracker.Core.Abstractions;

namespace AssetTracker.Infrastructure.Demo;

/// <summary>
/// Demo mode directory: about 150 obviously fictional people (first name × made-up surname, <c>@demo.example</c>),
/// spread over the three demo sites, plus edge cases for the sync report. The output is fixed: the same entries
/// on every run and every machine.
/// <list type="bullet">
/// <item>3 disabled accounts (one of them, <see cref="LeaverUsername"/>, was enabled in the historical snapshot used
/// while seeding, so they hold an open loan as an inactive holder);</item>
/// <item>2 people whose site value maps to no site (<c>unmapped-site</c>);</item>
/// <item>1 person without an email address (cannot receive new loans);</item>
/// <item>1 pair of entries sharing a directory id (quarantined, <c>duplicate-id</c>);</item>
/// <item>the role switcher's people: <see cref="DemoDefaults.YouUsername"/> and <see cref="DemoDefaults.ManagerUsername"/>.</item>
/// </list>
/// </summary>
public sealed class DemoDirectorySource : IDirectorySource
{
    public const string AmsValue = "Amsterdam";
    public const string NycValue = "New York";
    public const string SinValue = "Singapore";

    public const int RegularCount = 144;

    private static readonly string[] FirstNames =
        ["Alex", "Billie", "Casey", "Dana", "Eli", "Frankie", "Gabi", "Harper", "Indy", "Jules", "Kai", "Lou", "Max", "Noor", "Oak"];

    private static readonly string[] Surnames =
        ["Demoson", "Sampleton", "Testwell", "Mockford", "Placeholt", "Fictaway", "Exampleby", "Stubbins", "Dummett", "Fixturo"];

    private static readonly string[] SiteValues = [AmsValue, NycValue, SinValue];

    private static readonly string[] Titles =
        ["Consultant", "Analyst", "Service desk engineer", "Project lead", "Designer", "Accountant", "Sales associate", "Office coordinator"];

    /// <summary>Indexes of regular people whose account is disabled.</summary>
    private static readonly int[] DisabledIndexes = [10, 20, 30];

    private static readonly AsyncLocal<bool> Historical = new();

    /// <summary>Username of the person who is active in the historical (seeding) snapshot and disabled now.</summary>
    public static string LeaverUsername => Username(30);

    /// <summary>Username of the person without an email address.</summary>
    public static string NoEmailUsername => Username(146);

    /// <summary>Usernames of the people whose site value is unmapped.</summary>
    public static IReadOnlyList<string> UnmappedUsernames => [Username(144), Username(145)];

    /// <summary>Username of the regular person with the given index (0-based).</summary>
    public static string Username(int index) =>
        $"{FirstNames[index % FirstNames.Length]}.{Surnames[index / FirstNames.Length]}".ToLowerInvariant();

    /// <summary>Directory site value of regular person <paramref name="index"/>.</summary>
    public static string SiteValueOf(int index) => SiteValues[index % SiteValues.Length];

    /// <summary>Whether regular person <paramref name="index"/> can receive loans (enabled, with email, mapped site).</summary>
    public static bool IsRegularEligible(int index) => index is >= 0 and < RegularCount && !DisabledIndexes.Contains(index);

    /// <summary>Runs <paramref name="action"/> with the historical snapshot (the leaver still enabled), this async flow only.</summary>
    public static async Task RunWithHistoricalSnapshotAsync(Func<Task> action)
    {
        var previous = Historical.Value;
        Historical.Value = true;
        try { await action(); }
        finally { Historical.Value = previous; }
    }

    public Task<IReadOnlyList<DirectoryEntry>> FetchAllUsersAsync(CancellationToken ct) =>
        Task.FromResult(Entries(Historical.Value));

    /// <summary>The full directory. <paramref name="historical"/>: the seeding snapshot, in which the leaver is still enabled.</summary>
    public static IReadOnlyList<DirectoryEntry> Entries(bool historical = false)
    {
        var list = new List<DirectoryEntry>(RegularCount + 9)
        {
            new("demo-you", DemoDefaults.YouSid, DemoDefaults.YouUsername, "Demo You", "Service desk engineer",
                $"you@{DemoDefaults.EmailDomain}", AmsValue, true),
            new("demo-manager-ams", DemoDefaults.ManagerSid, DemoDefaults.ManagerUsername, "Demo Manager (AMS)",
                "Site manager", $"manager.ams@{DemoDefaults.EmailDomain}", AmsValue, true),
        };
        for (var i = 0; i < RegularCount; i++)
        {
            var enabled = !DisabledIndexes.Contains(i) || (historical && Username(i) == LeaverUsername);
            list.Add(Person(i, SiteValueOf(i), enabled));
        }
        // Site values that match no site: synced without a site and reported as unmapped-site.
        list.Add(Person(144, "Utopia Office", true));
        list.Add(Person(145, "Remote", true));
        // No email address: active, but cannot receive new loans.
        list.Add(Person(146, AmsValue, true) with { Email = null });
        // Two entries sharing one directory id: both quarantined (duplicate-id).
        list.Add(Person(147, NycValue, true) with { DirectoryId = "demo-duplicate-0001" });
        list.Add(Person(148, SinValue, true) with { DirectoryId = "demo-duplicate-0001" });
        return list;
    }

    private static DirectoryEntry Person(int index, string siteValue, bool enabled)
    {
        var username = Username(index);
        var first = FirstNames[index % FirstNames.Length];
        var last = Surnames[index / FirstNames.Length];
        return new DirectoryEntry(
            DirectoryId: $"demo-{index + 1:0000}",
            Sid: DemoDefaults.SidPrefix + (2000 + index),
            Username: username,
            DisplayName: $"{first} {last}",
            JobTitle: Titles[index % Titles.Length],
            Email: $"{username}@{DemoDefaults.EmailDomain}",
            SiteValue: siteValue,
            Enabled: enabled);
    }
}
