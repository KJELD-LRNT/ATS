using AssetTracker.Core.Abstractions;
using System.Security.Cryptography;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AssetTracker.Infrastructure.Mail;

public enum MailSecureSocket
{
    /// <summary>Plain SMTP. Only for a relay on a trusted segment where Stage 0 confirms TLS is unavailable.</summary>
    None,

    /// <summary>STARTTLS, required (the connection fails if the relay does not offer it).</summary>
    StartTls,

    /// <summary>Implicit TLS from connect (usually port 465).</summary>
    SslOnConnect,
}

/// <summary>
/// Bound from the "Mail" section, normally set on Admin → Settings (database-backed, see
/// <c>DatabaseSettingsConfigurationProvider</c>). Host and FromAddress have no defaults: they come from Stage 0
/// (approved internal relay and sending identity). Prefer no credentials (relay restricted by source host).
/// </summary>
public class MailOptions
{
    public const string SectionName = "Mail";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public MailSecureSocket SecureSocket { get; set; } = MailSecureSocket.StartTls;

    /// <summary>Optional. Leave empty for an IP/host-restricted relay.</summary>
    public string? Username { get; set; }

    /// <summary>
    /// Plain-text password from a configuration file. Not recommended: set the password on Admin → Settings, which
    /// stores it encrypted (<see cref="PasswordProtected"/>). Used only when no encrypted password is stored.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Data Protection ciphertext of the password saved on Admin → Settings (database row <c>Mail:Password</c>,
    /// exposed to configuration under this key). Decrypted only at send time.
    /// </summary>
    public string? PasswordProtected { get; set; }

    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Asset Tracker";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>The relay settings one send uses (current settings, password decrypted).</summary>
internal sealed record SmtpSettings(MailOptions Options, string? Password);

/// <summary>
/// Submits plain-text mail to the approved internal relay with MailKit. One connection per message: volumes are low
/// and this keeps failure handling simple. Any failure throws, so the outbox retries with backoff.
/// Settings are read per message (<see cref="IOptionsMonitor{TOptions}.CurrentValue"/>), so changes saved on
/// Admin → Settings apply without a restart.
/// </summary>
public class SmtpMailRelay(IOptionsMonitor<MailOptions> options, ISecretProtector secrets) : IMailRelay
{
    /// <summary>Validates and resolves the current settings; decrypts the stored password.</summary>
    internal SmtpSettings ResolveSettings()
    {
        var o = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.Host))
            throw new InvalidOperationException("Mail:Host is not configured. Set it on Admin → Settings (approved internal relay).");
        if (string.IsNullOrWhiteSpace(o.FromAddress))
            throw new InvalidOperationException("Mail:FromAddress is not configured. Set it on Admin → Settings (approved sending identity).");

        var password = o.Password;
        if (!string.IsNullOrEmpty(o.PasswordProtected))
        {
            try
            {
                password = secrets.Unprotect(o.PasswordProtected);
            }
            catch (CryptographicException)
            {
                // No inner exception or ciphertext in the message: it ends up in the outbox error column.
                throw new InvalidOperationException(
                    "The stored SMTP password cannot be decrypted (Data Protection key ring missing or changed). Re-enter it on Admin → Settings.");
            }
        }
        return new SmtpSettings(o, password);
    }

    public async Task SendAsync(MailMessage message, CancellationToken ct)
    {
        var (o, password) = ResolveSettings();

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        using var client = new SmtpClient { Timeout = (int)o.Timeout.TotalMilliseconds };
        var socket = o.SecureSocket switch
        {
            MailSecureSocket.None => SecureSocketOptions.None,
            MailSecureSocket.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(o.Host, o.Port, socket, ct);
        if (!string.IsNullOrEmpty(o.Username))
            await client.AuthenticateAsync(o.Username, password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}
