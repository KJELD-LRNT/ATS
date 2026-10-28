using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Security;

/// <summary>Actions of the setup command (<c>AssetTracker.Worker.exe superadmin apply</c>, started by <c>deploy/Set-SuperAdmin.ps1</c>).</summary>
public enum SuperAdminSetupAction
{
    SetPassword,
    GeneratePassword,
    Unlock,
    Disable,
    Status,
}

public static class SuperAdminSetupActions
{
    /// <summary>The action names used in the request file and by the script.</summary>
    public static readonly IReadOnlyDictionary<string, SuperAdminSetupAction> ByName =
        new Dictionary<string, SuperAdminSetupAction>(StringComparer.OrdinalIgnoreCase)
        {
            ["set-password"] = SuperAdminSetupAction.SetPassword,
            ["generate-password"] = SuperAdminSetupAction.GeneratePassword,
            ["unlock"] = SuperAdminSetupAction.Unlock,
            ["disable"] = SuperAdminSetupAction.Disable,
            ["status"] = SuperAdminSetupAction.Status,
        };

    public static string Name(SuperAdminSetupAction action) => ByName.First(p => p.Value == action).Key;
}

/// <summary>
/// Applies a setup command action to the super admin account. Runs only in the worker's <c>superadmin apply</c>
/// command, under the app's gMSA, with an <see cref="ICurrentUser"/> that reports the Windows account that owns the
/// request file, so every change is audited with that account. Not registered in the web app: nobody can see or
/// reset the super admin password from inside the app.
/// <para>The command only ever receives a hash (made by <c>superadmin hash</c> in the admin's own session): the
/// password itself never reaches the task.</para>
/// </summary>
public class SuperAdminSetupService(AppDbContext db, AuditWriter audit, IClock clock)
{
    public async Task<SuperAdminStatus> ApplyAsync(SuperAdminSetupAction action, string? passwordHash,
        CancellationToken ct = default)
    {
        var account = await db.LocalAccounts.SingleOrDefaultAsync(a => a.UserName == SuperAdmin.UserName, ct);
        var name = SuperAdminSetupActions.Name(action);
        switch (action)
        {
            case SuperAdminSetupAction.SetPassword or SuperAdminSetupAction.GeneratePassword:
                if (!SuperAdmin.IsValidHash(passwordHash))
                    throw new DomainException("superadmin.hash",
                        "The request has no valid password hash. Make it with 'AssetTracker.Worker.exe superadmin hash'.");
                if (account is null)
                {
                    account = new LocalAccount { UserName = SuperAdmin.UserName };
                    db.LocalAccounts.Add(account);
                }
                account.PasswordHash = passwordHash;
                account.PasswordChangedAt = clock.UtcNow;
                account.IsEnabled = true;
                account.FailedCount = 0;
                account.LockedUntil = null;
                account.SecurityStamp = SuperAdmin.NewSecurityStamp();
                await SaveAsync(account, "superadmin.password-set", new { action = name, via = "setup command" }, ct);
                break;

            case SuperAdminSetupAction.Unlock:
                if (account is null)
                    throw new DomainException("superadmin.missing", "The super admin account has not been set up yet. Set a password first.");
                var wasLockedUntil = account.LockedUntil;
                account.FailedCount = 0;
                account.LockedUntil = null;
                await SaveAsync(account, "superadmin.unlock", new { wasLockedUntil }, ct);
                break;

            case SuperAdminSetupAction.Disable:
                if (account is null)
                    throw new DomainException("superadmin.missing", "The super admin account has not been set up yet, so it is already off.");
                account.IsEnabled = false;
                // Ends every open session; sign-in stays refused until a new password is set.
                account.SecurityStamp = SuperAdmin.NewSecurityStamp();
                await SaveAsync(account, "superadmin.disable", null, ct);
                break;

            case SuperAdminSetupAction.Status:
                audit.Record("superadmin.status", SuperAdminService.EntityType, account?.Id, null);
                await db.SaveChangesAsync(ct);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
        return SuperAdminStatus.From(account);
    }

    private async Task SaveAsync(LocalAccount account, string auditAction, object? details, CancellationToken ct)
    {
        // Insert first when new, so the audit row can name the account id; both in one transaction.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (account.Id == 0) await db.SaveChangesAsync(ct);
        audit.Record(auditAction, SuperAdminService.EntityType, account.Id, null, details: details);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
