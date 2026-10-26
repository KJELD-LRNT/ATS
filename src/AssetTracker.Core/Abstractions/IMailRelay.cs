namespace AssetTracker.Core.Abstractions;

public sealed record MailMessage(string To, string Subject, string Body);

/// <summary>Submits mail to the approved internal relay. Throws on failure.</summary>
public interface IMailRelay
{
    Task SendAsync(MailMessage message, CancellationToken ct);
}
