using AssetTracker.Core.Abstractions;
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
/// Bound from the "Mail" section. Host and FromAddress have no defaults: they come from Stage 0
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

    public string? Password { get; set; }

    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Asset Tracker";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Submits plain-text mail to the approved internal relay with MailKit. One connection per message: volumes are low
/// and this keeps failure handling simple. Any failure throws, so the outbox retries with backoff.
/// </summary>
public class SmtpMailRelay(IOptionsMonitor<MailOptions> options) : IMailRelay
{
    public async Task SendAsync(MailMessage message, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.Host))
            throw new InvalidOperationException("Mail:Host is not configured. Set it to the approved internal relay (Stage 0).");
        if (string.IsNullOrWhiteSpace(o.FromAddress))
            throw new InvalidOperationException("Mail:FromAddress is not configured. Set it to the approved sending identity (Stage 0).");

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
            await client.AuthenticateAsync(o.Username, o.Password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}
