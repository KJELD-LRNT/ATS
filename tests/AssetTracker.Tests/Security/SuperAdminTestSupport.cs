using System.Net;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Tests.Security;

/// <summary>Arranging the super admin account and signing in through <c>/superadmin</c> like a browser.</summary>
public static class SuperAdminTestSupport
{
    public const string Password = "correct horse battery staple 42";

    /// <summary>Sets (or creates) the super admin with <paramref name="password"/>: enabled, not locked, a new stamp.</summary>
    public static Task<LocalAccount> ResetAccountAsync(AppFactory f, string password = Password, bool enabled = true) =>
        f.WithDbAsync(async db =>
        {
            var a = await db.LocalAccounts.SingleOrDefaultAsync(x => x.UserName == SuperAdmin.UserName);
            if (a is null) db.LocalAccounts.Add(a = new LocalAccount { UserName = SuperAdmin.UserName });
            a.PasswordHash = SuperAdmin.HashPassword(password);
            a.PasswordChangedAt = f.Clock.UtcNow;
            a.IsEnabled = enabled;
            a.FailedCount = 0;
            a.LockedUntil = null;
            a.SecurityStamp = SuperAdmin.NewSecurityStamp();
            await db.SaveChangesAsync();
            return a;
        });

    public static Task<LocalAccount> AccountAsync(AppFactory f) =>
        f.WithDbAsync(db => db.LocalAccounts.AsNoTracking().SingleAsync(x => x.UserName == SuperAdmin.UserName));

    /// <summary>GETs the sign-in form for a token and posts the credentials.</summary>
    public static Task<HttpResponseMessage> PostSignInAsync(HttpClient client, string password,
        string userName = SuperAdmin.UserName) =>
        FormHelper.PostFormAsync(client, "/superadmin", "/superadmin", ("UserName", userName), ("Password", password));

    /// <summary>An anonymous client (own IP address) signed in as the super admin.</summary>
    public static async Task<HttpClient> SignedInClientAsync(AppFactory f, string password = Password, string? ip = null)
    {
        var client = f.CreateAnonymousClient(ip);
        var res = await PostSignInAsync(client, password);
        Assert.True(res.StatusCode == HttpStatusCode.Redirect, $"sign-in: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return client;
    }

    public static async Task<HttpStatusCode> StatusAsync(HttpClient client, string url) => (await client.GetAsync(url)).StatusCode;
}
