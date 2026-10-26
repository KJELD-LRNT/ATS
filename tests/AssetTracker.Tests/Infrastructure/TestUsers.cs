namespace AssetTracker.Tests.Infrastructure;

/// <summary>AD group SIDs used by the test configuration.</summary>
public static class TestGroups
{
    public const string It = "S-1-5-21-1000-1000-1000-5001";
    public const string Manager = "S-1-5-21-1000-1000-1000-5002";
    public const string Admin = "S-1-5-21-1000-1000-1000-5003";
}

/// <summary>A simulated Windows principal.</summary>
public sealed record TestUser(string Name, string Sid, params string[] GroupSids)
{
    public static readonly TestUser It = new(@"CORP\it.user", "S-1-5-21-1000-1000-1000-2001", TestGroups.It);
    public static readonly TestUser Manager = new(@"CORP\manager.user", "S-1-5-21-1000-1000-1000-2002", TestGroups.Manager);
    public static readonly TestUser Admin = new(@"CORP\admin.user", "S-1-5-21-1000-1000-1000-2003", TestGroups.Admin);
    public static readonly TestUser Nobody = new(@"CORP\plain.user", "S-1-5-21-1000-1000-1000-2004");
}
