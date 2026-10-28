using AssetTracker.Core.Domain;
using Microsoft.AspNetCore.Identity;

namespace AssetTracker.Core.Security;

/// <summary>
/// The built-in super admin account (one local account outside AD, see <c>docs/decisions.md</c> D12): fixed names,
/// limits and the password rules shared by the sign-in page, the in-app password change and the setup command.
/// </summary>
public static class SuperAdmin
{
    /// <summary>The only username that signs in on <c>/superadmin</c>.</summary>
    public const string UserName = "superadmin";

    /// <summary>Name claim of the signed-in super admin: the audit actor and every "granted by"/"changed by" value.</summary>
    public const string DisplayName = "superadmin (local)";

    /// <summary>Authentication type of the super admin identity (and the name of its cookie scheme).</summary>
    public const string AuthenticationType = "SuperAdmin";

    public const int MinimumPasswordLength = 16;

    /// <summary>Upper bound so a huge form value cannot make hashing expensive.</summary>
    public const int MaximumPasswordLength = 256;

    /// <summary>The demo seed's password (local demo mode only). The setup command and the password change refuse it.</summary>
    public const string DemoPassword = "demo-superadmin-2026";

    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromHours(8);

    /// <summary>Per-IP limit on sign-in (and password change) posts, enforced by ASP.NET Core's rate limiter.</summary>
    public const int RateLimitPermits = 10;
    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(15);

    private static readonly PasswordHasher<LocalAccount> Hasher = new();
    private static readonly LocalAccount HashUser = new() { UserName = UserName };

    /// <summary>Why a new password is refused, or null when it is acceptable.</summary>
    public static string? PasswordProblem(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
            return $"The password must be at least {MinimumPasswordLength} characters long.";
        if (password.Length > MaximumPasswordLength)
            return $"The password must be at most {MaximumPasswordLength} characters long.";
        if (string.Equals(password.Trim(), DemoPassword, StringComparison.OrdinalIgnoreCase))
            return "This is the published demo password. Choose another one.";
        return null;
    }

    /// <summary>Hashes a password with ASP.NET Core Identity's <c>PasswordHasher</c> (format V3).</summary>
    public static string HashPassword(string password) => Hasher.HashPassword(HashUser, password);

    public static PasswordVerificationResult VerifyPassword(string hash, string password)
    {
        try
        {
            return Hasher.VerifyHashedPassword(HashUser, hash, password);
        }
        catch (FormatException)
        {
            return PasswordVerificationResult.Failed;
        }
    }

    /// <summary>
    /// Whether <paramref name="hash"/> looks like a <c>PasswordHasher</c> V3 hash (base64, format marker 0x01,
    /// header + salt + subkey). The setup command only stores hashes made by <c>superadmin hash</c>.
    /// </summary>
    public static bool IsValidHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length > 512) return false;
        var bytes = new byte[hash.Length];
        if (!Convert.TryFromBase64String(hash, bytes, out var length)) return false;
        return length >= 13 + 16 + 32 && bytes[0] == 0x01;
    }

    public static string NewSecurityStamp() => Guid.NewGuid().ToString("N");
}
