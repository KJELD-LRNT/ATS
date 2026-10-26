using System.Net.Mail;

namespace AssetTracker.Core.Notifications;

public static class EmailAddresses
{
    /// <summary>
    /// True for a single bare address (no display name, no list) with a dotted domain, e.g. "a.b@corp.example".
    /// Deliberately conservative: anything doubtful is treated as invalid and surfaced to IT as a skipped recipient.
    /// </summary>
    public static bool IsValid(string? address) => Normalize(address) is not null;

    /// <summary>Trimmed address when valid, otherwise null.</summary>
    public static string? Normalize(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var s = address.Trim();
        if (s.Length > 320 || s.Any(char.IsWhiteSpace) || s.IndexOfAny([',', ';', '<', '>', '"']) >= 0) return null;
        if (!MailAddress.TryCreate(s, out var parsed)) return null;
        if (!string.Equals(parsed.Address, s, StringComparison.Ordinal)) return null;
        var host = parsed.Host;
        if (host.Length < 3 || !host.Contains('.') || host.StartsWith('.') || host.EndsWith('.') || host.Contains(".."))
            return null;
        return s;
    }
}
