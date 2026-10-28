using System.Security.Cryptography;
using System.Text;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Security;

public enum SuperAdminSignInResult
{
    Success,

    /// <summary>Wrong username or password (the page never says which).</summary>
    Failed,

    /// <summary>Too many failed attempts: refused until <see cref="LocalAccount.LockedUntil"/>.</summary>
    LockedOut,

    /// <summary>No password set yet, or switched off by the setup command.</summary>
    Disabled,
}

/// <param name="SecurityStamp">The account's stamp for the session cookie (only on success).</param>
public sealed record SuperAdminSignIn(SuperAdminSignInResult Result, string? SecurityStamp = null);

/// <summary>What the setup command's <c>status</c> shows, and what <c>/superadmin</c> needs to know.</summary>
public sealed record SuperAdminStatus(
    bool Exists, bool IsEnabled, bool HasPassword, DateTimeOffset? LockedUntil, int FailedCount,
    DateTimeOffset? PasswordChangedAt, DateTimeOffset? LastSignInAt, string? LastSignInIp)
{
    public static readonly SuperAdminStatus Missing = new(false, false, false, null, 0, null, null, null);

    /// <summary>Whether a sign-in can succeed at all (enabled with a password).</summary>
    public bool CanSignIn => IsEnabled && HasPassword;

    public bool IsLockedAt(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public static SuperAdminStatus From(LocalAccount? a) => a is null
        ? Missing
        : new(true, a.IsEnabled, a.PasswordHash is not null, a.LockedUntil, a.FailedCount, a.PasswordChangedAt,
            a.LastSignInAt, a.LastSignInIp);
}

/// <summary>
/// Sign-in, session checks and the in-app password change of the built-in super admin (§2 of
/// <c>docs/plans/system-roles-and-super-admin.md</c>). Changes by the setup command on the server are in
/// <see cref="SuperAdminSetupService"/>.
/// <para><b>Brute force:</b> every attempt first claims a slot with one atomic
/// <c>UPDATE … SET FailedCount = FailedCount + 1 WHERE FailedCount &lt; 5 AND not locked</c>, before the password is
/// verified, so parallel attempts can never get past the limit. The fifth failure locks the account for 15 minutes
/// (stored in the database). A success resets the counter.</para>
/// </summary>
public class SuperAdminService(
    AppDbContext db, AuditWriter audit, NotificationService notifications, ICurrentUser user, IClock clock)
{
    public const string EntityType = nameof(LocalAccount);

    public async Task<SuperAdminStatus> GetStatusAsync(CancellationToken ct = default) =>
        SuperAdminStatus.From(await db.LocalAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.UserName == SuperAdmin.UserName, ct));

    /// <summary>
    /// Checks a username and password. Wrong usernames count as failed attempts on the one account (the username is
    /// fixed and public). The password is verified even when the username is wrong, so both take the same time.
    /// </summary>
    public async Task<SuperAdminSignIn> SignInAsync(string? userName, string? password, string? ip,
        CancellationToken ct = default)
    {
        ip = Normalize(ip);
        var now = clock.UtcNow;
        var account = await db.LocalAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserName == SuperAdmin.UserName, ct);
        if (account is null || !account.IsEnabled || account.PasswordHash is null)
            return new SuperAdminSignIn(SuperAdminSignInResult.Disabled);
        var id = account.Id;

        // Claim an attempt before verifying. A lock that has run out starts a new count at 1.
        var claimed = await db.LocalAccounts
            .Where(a => a.Id == id && a.IsEnabled &&
                        ((a.LockedUntil == null && a.FailedCount < SuperAdmin.MaxFailedAttempts) || a.LockedUntil <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.FailedCount, a => a.LockedUntil != null ? 1 : a.FailedCount + 1)
                .SetProperty(a => a.LockedUntil, (DateTimeOffset?)null), ct);
        if (claimed == 0)
        {
            // Locked: only counted, not audited one by one.
            await db.LocalAccounts.Where(a => a.Id == id && a.LockedUntil > now)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.FailedCount, a => a.FailedCount + 1), ct);
            // Recovers a count left at the limit without a lock (e.g. the process stopped mid-attempt).
            await TryLockAsync(id, ip, now, ct);
            return new SuperAdminSignIn(SuperAdminSignInResult.LockedOut);
        }

        var verified = SuperAdmin.VerifyPassword(account.PasswordHash, password ?? "");
        var nameOk = string.Equals(userName?.Trim(), SuperAdmin.UserName, StringComparison.OrdinalIgnoreCase);
        if (nameOk && verified != PasswordVerificationResult.Failed)
        {
            var tracked = await db.LocalAccounts.SingleAsync(a => a.Id == id, ct);
            // The password or the account changed between the check and now (setup command): treat as a failure.
            if (tracked.IsEnabled && tracked.PasswordHash == account.PasswordHash)
            {
                tracked.FailedCount = 0;
                tracked.LockedUntil = null;
                tracked.LastSignInAt = now;
                tracked.LastSignInIp = ip;
                if (verified == PasswordVerificationResult.SuccessRehashNeeded)
                    tracked.PasswordHash = SuperAdmin.HashPassword(password!);
                audit.RecordAs(SuperAdmin.DisplayName, "superadmin.signin", EntityType, id, null, details: new { ip });
                await notifications.QueueSystemAlertAsync(
                    "Asset Tracker: the super admin signed in",
                    $"""
                    Someone signed in to Asset Tracker with the built-in super admin account (not an AD account).

                    Time: {now:yyyy-MM-dd HH:mm} UTC
                    IP address: {ip}

                    If you did not expect this, switch the account off on the server with
                    deploy\Set-SuperAdmin.ps1 -Action disable, and check the audit log.
                    """,
                    $"superadmin-signin:{id}:{now.UtcTicks}:{Guid.NewGuid():N}", ct);
                await db.SaveChangesAsync(ct);
                return new SuperAdminSignIn(SuperAdminSignInResult.Success, tracked.SecurityStamp);
            }
        }

        var attempt = await db.LocalAccounts.Where(a => a.Id == id).Select(a => a.FailedCount).SingleAsync(ct);
        audit.RecordAs(SuperAdmin.DisplayName, "superadmin.signin-failed", EntityType, id, null,
            details: new { ip, attempt });
        await db.SaveChangesAsync(ct);
        if (attempt < SuperAdmin.MaxFailedAttempts) return new SuperAdminSignIn(SuperAdminSignInResult.Failed);
        await TryLockAsync(id, ip, now, ct);
        return new SuperAdminSignIn(SuperAdminSignInResult.LockedOut);
    }

    /// <summary>
    /// Sets the lock when the count reached the limit and no lock is set. Only the request whose update wins writes
    /// the <c>superadmin.lockout</c> audit row and the IT mail (one per lock period), in the same transaction.
    /// </summary>
    private async Task TryLockAsync(int id, string ip, DateTimeOffset now, CancellationToken ct)
    {
        var until = now + SuperAdmin.LockoutDuration;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await db.LocalAccounts
            .Where(a => a.Id == id && a.LockedUntil == null && a.FailedCount >= SuperAdmin.MaxFailedAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LockedUntil, (DateTimeOffset?)until), ct);
        if (locked == 1)
        {
            audit.RecordAs(SuperAdmin.DisplayName, "superadmin.lockout", EntityType, id, null,
                details: new { ip, lockedUntil = until });
            await notifications.QueueSystemAlertAsync(
                "Asset Tracker: the super admin account is locked",
                $"""
                The built-in super admin account of Asset Tracker was locked after {SuperAdmin.MaxFailedAttempts} failed sign-in attempts.

                Last attempt from IP address: {ip}
                Locked until: {until:yyyy-MM-dd HH:mm} UTC

                It unlocks by itself after {SuperAdmin.LockoutDuration.TotalMinutes:0} minutes. To unlock it earlier, run
                deploy\Set-SuperAdmin.ps1 -Action unlock on the server. If nobody on your team was signing in, check
                the audit log and consider switching the account off (-Action disable).
                """,
                $"superadmin-lockout:{id}:{until.UtcTicks}", ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Session check for every request with the super admin cookie (one indexed lookup): the account must still be
    /// enabled with a password, and its security stamp must equal the one in the cookie.
    /// </summary>
    public async Task<bool> IsSessionValidAsync(string? securityStamp, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(securityStamp)) return false;
        var account = await db.LocalAccounts.AsNoTracking()
            .Where(a => a.UserName == SuperAdmin.UserName)
            .Select(a => new { a.IsEnabled, a.SecurityStamp, HasPassword = a.PasswordHash != null })
            .SingleOrDefaultAsync(ct);
        return account is { IsEnabled: true, HasPassword: true } &&
               CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(account.SecurityStamp),
                   Encoding.UTF8.GetBytes(securityStamp));
    }

    /// <summary>
    /// Changes the password while signed in as the super admin. Needs the current password; the new one follows the
    /// setup command's rules. Changes the security stamp (other sessions end) and returns the new stamp so the caller
    /// can issue a fresh cookie.
    /// </summary>
    public async Task<string> ChangePasswordAsync(string? currentPassword, string? newPassword, string? confirmPassword,
        CancellationToken ct = default)
    {
        if (user.Name != SuperAdmin.DisplayName || user.Sid is not null) throw new AccessDeniedException();
        var account = await db.LocalAccounts.SingleOrDefaultAsync(a => a.UserName == SuperAdmin.UserName, ct)
                      ?? throw new AccessDeniedException();
        if (!account.IsEnabled || account.PasswordHash is null) throw new AccessDeniedException();
        if (SuperAdmin.VerifyPassword(account.PasswordHash, currentPassword ?? "") == PasswordVerificationResult.Failed)
            throw new DomainException("superadmin.current-password", "The current password is wrong.");
        if (SuperAdmin.PasswordProblem(newPassword) is { } problem)
            throw new DomainException("superadmin.password", problem);
        if (newPassword != confirmPassword)
            throw new DomainException("superadmin.password-confirm", "The new passwords do not match.");
        if (newPassword == currentPassword)
            throw new DomainException("superadmin.password-same", "The new password must differ from the current one.");

        account.PasswordHash = SuperAdmin.HashPassword(newPassword!);
        account.PasswordChangedAt = clock.UtcNow;
        account.SecurityStamp = SuperAdmin.NewSecurityStamp();
        account.FailedCount = 0;
        account.LockedUntil = null;
        audit.Record("superadmin.password-change", EntityType, account.Id, null, details: new { via = "app" });
        await db.SaveChangesAsync(ct);
        return account.SecurityStamp;
    }

    /// <summary>Audits a sign-out by the current (super admin) user.</summary>
    public async Task RecordSignOutAsync(string? ip, CancellationToken ct = default)
    {
        var id = await db.LocalAccounts.Where(a => a.UserName == SuperAdmin.UserName).Select(a => (int?)a.Id)
            .SingleOrDefaultAsync(ct);
        audit.RecordAs(SuperAdmin.DisplayName, "superadmin.signout", EntityType, id, null,
            details: new { ip = Normalize(ip) });
        await db.SaveChangesAsync(ct);
    }

    private static string Normalize(string? ip) =>
        string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Length <= 64 ? ip : ip[..64];
}
