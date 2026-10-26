using AssetTracker.Core.Abstractions;
using AssetTracker.Infrastructure.Mail;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;

namespace AssetTracker.Infrastructure.Demo;

/// <summary>
/// Demo mode relay: writes each message as an RFC 822 <c>.eml</c> file (MimeKit) into the pickup folder instead of
/// sending it. The <c>/Demo/Mailbox</c> page reads the folder through <see cref="DemoMailbox"/>. The <c>Date</c>
/// header uses the demo clock (time travel); the file name uses the real time so the newest delivery sorts first.
/// </summary>
public sealed class PickupDirectoryMailRelay(DemoMailbox mailbox, IClock clock, IOptionsMonitor<MailOptions> options)
    : IMailRelay
{
    public async Task SendAsync(MailMessage message, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(o.FromName) ? "Asset Tracker" : o.FromName,
            string.IsNullOrWhiteSpace(o.FromAddress) ? DemoDefaults.FromAddress : o.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Date = clock.UtcNow;
        mime.Body = new TextPart("plain") { Text = message.Body };
        await mailbox.WriteAsync(mime, ct);
    }
}

/// <summary>One message in the demo mailbox listing.</summary>
public sealed record DemoMailSummary(string Id, string To, string Subject, DateTimeOffset Date);

/// <summary>A message opened in the demo mailbox (plain-text body only).</summary>
public sealed record DemoMailMessage(string Id, string From, string To, string Subject, DateTimeOffset Date, string Body);

/// <summary>The demo mail pickup folder: write, list (newest first), read and clear <c>.eml</c> files.</summary>
public sealed class DemoMailbox(DemoOptions options)
{
    private const string Extension = ".eml";

    public string Directory => options.MailDirectory;

    public async Task WriteAsync(MimeMessage message, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}"[..40] + Extension;
        var path = Path.Combine(Directory, name);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await message.WriteToAsync(stream, ct);
        File.Move(temp, path);
    }

    /// <summary>Newest first (by delivery time).</summary>
    public IReadOnlyList<DemoMailSummary> List(int max = 500)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var result = new List<DemoMailSummary>();
        foreach (var file in new DirectoryInfo(Directory).GetFiles("*" + Extension)
                     .OrderByDescending(f => f.Name, StringComparer.Ordinal).Take(max))
        {
            try
            {
                using var stream = file.OpenRead();
                var headers = HeaderList.Load(stream);
                result.Add(new DemoMailSummary(file.Name, headers[HeaderId.To] ?? "", Decode(headers, HeaderId.Subject),
                    DateUtils.TryParse(headers[HeaderId.Date], out var date) ? date : file.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or FormatException or ParseException)
            {
                // A file being written or not a message: skip it.
            }
        }
        return result;
    }

    public int Count() => System.IO.Directory.Exists(Directory)
        ? System.IO.Directory.EnumerateFiles(Directory, "*" + Extension).Count()
        : 0;

    /// <summary>Opens one message by its file name; null when the id is not a message in the folder.</summary>
    public async Task<DemoMailMessage?> ReadAsync(string? id, CancellationToken ct)
    {
        var path = PathOf(id);
        if (path is null) return null;
        var m = await MimeMessage.LoadAsync(path, ct);
        return new DemoMailMessage(id!, m.From.ToString(), m.To.ToString(), m.Subject ?? "", m.Date,
            m.TextBody ?? "(no plain-text body)");
    }

    /// <summary>Deletes every message.</summary>
    public void Clear()
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
        {
            try { File.Delete(file); }
            catch (IOException) { /* being written right now; it stays */ }
        }
    }

    private string? PathOf(string? id)
    {
        // Only a bare file name from this folder (no path segments) is accepted.
        if (string.IsNullOrEmpty(id) || id != Path.GetFileName(id) || !id.EndsWith(Extension, StringComparison.Ordinal))
            return null;
        var path = Path.Combine(Directory, id);
        return File.Exists(path) ? path : null;
    }

    private static string Decode(HeaderList headers, HeaderId id)
    {
        var header = headers.FirstOrDefault(h => h.Id == id);
        return header is null ? "" : header.Value;
    }
}
