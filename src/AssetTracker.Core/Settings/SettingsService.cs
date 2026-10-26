using System.Text.RegularExpressions;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AssetTracker.Core.Settings;

/// <summary>Where a setting's effective value comes from.</summary>
public enum SettingSource
{
    /// <summary>An <see cref="AppSetting"/> row saved on the Settings page.</summary>
    Database,

    /// <summary>appsettings files, environment variables or command line.</summary>
    ConfigFile,

    /// <summary>The options class default.</summary>
    Default,

    /// <summary>Nothing sets it and there is no default.</summary>
    NotSet,
}

/// <summary>One setting as shown on the Settings page. Secrets never carry a value, only <see cref="SecretSet"/>.</summary>
public sealed record SettingView(
    SettingDefinition Definition,
    string? DatabaseValue,
    string? FileValue,
    string? EffectiveValue,
    SettingSource Source,
    bool SecretSet,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

public sealed record SettingsOverview(IReadOnlyList<SettingView> Settings, string? ItMailbox)
{
    public IEnumerable<SettingView> InSection(string section) => Settings.Where(s => s.Definition.Section == section);
}

/// <summary>Result of the "Test directory query" button: the preview, or the (sanitized) error of the fetch.</summary>
public sealed record DirectoryPreviewOutcome(DirectoryPreview? Preview, string? Error);

/// <summary>Result of the "Send test email" button.</summary>
public sealed record MailTestResult(string To, bool Success, string? Error);

/// <summary>Validation failure of a settings save; <see cref="Errors"/> is keyed by setting key.</summary>
public sealed class SettingsValidationException(IReadOnlyDictionary<string, string> errors)
    : DomainException("settings.invalid", string.Join(" ", errors.Values))
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

/// <summary>
/// Admin-editable, database-backed settings (WP4.1): AD directory query, sync safety thresholds, SMTP relay,
/// notifications, default time zone and the IT mailbox. Values are <see cref="AppSetting"/> rows keyed by their
/// configuration path; the infrastructure configuration provider overlays them on the appsettings files and
/// <see cref="ISettingsReloader"/> makes <c>IOptionsMonitor</c> consumers see them without a restart.
/// <para>
/// Every method requires the admin role. Each changed key is audited as <c>setting.update</c> with before/after
/// values; the SMTP password is stored encrypted (<see cref="ISecretProtector"/>) and audited without any value.
/// </para>
/// </summary>
public partial class SettingsService(
    AppDbContext db,
    AccessScope scope,
    AuditWriter audit,
    ICurrentUser user,
    IClock clock,
    ISettingsReloader reloader,
    ISecretProtector protector,
    DirectorySyncService sync,
    IMailRelay relay,
    ILogger<SettingsService> logger)
{
    public const string SettingAuditAction = "setting.update";
    public const int MaxErrorLength = 500;
    public const int PreviewSampleSize = 5;

    public async Task<SettingsOverview> GetAsync(CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        var keys = SettingCatalog.All.Select(d => d.Key).Append(AppSetting.ItMailbox).ToList();
        var rows = (await db.Settings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToListAsync(ct))
            .ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

        var views = new List<SettingView>();
        foreach (var d in SettingCatalog.All)
        {
            rows.TryGetValue(d.Key, out var row);
            var file = Blank(reloader.GetBaseValue(d.Key));
            if (d.Kind == SettingKind.Secret)
            {
                var set = row is not null || file is not null;
                var source = row is not null ? SettingSource.Database : file is not null ? SettingSource.ConfigFile : SettingSource.NotSet;
                views.Add(new SettingView(d, null, null, null, source, set, row?.UpdatedAt, row?.UpdatedBy));
                continue;
            }
            var (effective, src) = row is not null ? (row.Value, SettingSource.Database)
                : file is not null ? (file, SettingSource.ConfigFile)
                : d.Default is not null ? (d.Default, SettingSource.Default)
                : ((string?)null, SettingSource.NotSet);
            views.Add(new SettingView(d, row?.Value, file, effective, src, false, row?.UpdatedAt, row?.UpdatedBy));
        }
        rows.TryGetValue(AppSetting.ItMailbox, out var it);
        return new SettingsOverview(views, it?.Value);
    }

    /// <summary>
    /// Saves one section. <paramref name="values"/> maps setting keys (and <see cref="SettingCatalog.ItMailboxField"/>
    /// for the notifications section) to the submitted text: a key that is absent is left unchanged, an empty value
    /// removes the database override (the config file or default applies again). For the SMTP password an empty
    /// value keeps the stored password; <paramref name="clearPassword"/> removes it.
    /// All values are validated before anything is written; then each changed key is audited, saved and the
    /// configuration reloaded.
    /// </summary>
    /// <returns>The keys that changed.</returns>
    /// <exception cref="SettingsValidationException">One or more values are invalid; nothing was saved.</exception>
    public async Task<IReadOnlyList<string>> SaveSectionAsync(string section, IReadOnlyDictionary<string, string?> values,
        bool clearPassword = false, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        if (!SettingSections.All.Contains(section))
            throw new DomainException("settings.section", "Unknown settings section.");

        var defs = SettingCatalog.InSection(section).ToList();
        var keys = defs.Select(d => d.Key).Append(AppSetting.ItMailbox).ToList();
        var rows = (await db.Settings.Where(s => keys.Contains(s.Key)).ToListAsync(ct))
            .ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Planned changes: key -> new stored value (null = remove the row).
        var changes = new List<(SettingDefinition? Def, string Key, string? Value)>();

        foreach (var d in defs)
        {
            if (d.Kind == SettingKind.Secret)
            {
                if (clearPassword)
                    changes.Add((d, d.Key, null));
                else if (values.TryGetValue(d.Key, out var secret) && !string.IsNullOrEmpty(secret))
                {
                    var (_, error) = SettingCatalog.Normalize(d, secret);
                    if (error is not null) errors[d.Key] = error;
                    else changes.Add((d, d.Key, secret));
                }
                continue;
            }

            string? newValue;
            if (values.TryGetValue(d.Key, out var input))
            {
                if (string.IsNullOrWhiteSpace(input)) newValue = null;
                else
                {
                    var (normalized, error) = SettingCatalog.Normalize(d, input);
                    if (error is not null)
                    {
                        errors[d.Key] = error;
                        continue;
                    }
                    newValue = normalized;
                }
                changes.Add((d, d.Key, newValue));
            }
            else
                newValue = rows.TryGetValue(d.Key, out var existing) ? existing.Value : null;

            if (d.RequiredEffective && newValue is null && Blank(reloader.GetBaseValue(d.Key)) is null)
                errors[d.Key] = $"{d.Label} is required (it is not set in the configuration file either).";
        }

        if (section == SettingSections.Notifications && values.TryGetValue(SettingCatalog.ItMailboxField, out var itInput))
        {
            if (string.IsNullOrWhiteSpace(itInput)) changes.Add((null, AppSetting.ItMailbox, null));
            else if (EmailAddresses.Normalize(itInput) is { } it) changes.Add((null, AppSetting.ItMailbox, it));
            else errors[SettingCatalog.ItMailboxField] =
                "IT mailbox: enter a single valid email address (e.g. it-desk@example.com).";
        }

        if (errors.Count > 0) throw new SettingsValidationException(errors);

        var now = clock.UtcNow;
        var changed = new List<string>();
        foreach (var (def, key, value) in changes)
        {
            rows.TryGetValue(key, out var row);
            var before = row?.Value;
            var isSecret = def?.Kind == SettingKind.Secret;
            if (!isSecret && row?.Value == value) continue;
            if (isSecret && value is null && row is null) continue;

            if (value is null)
                db.Settings.Remove(row!);
            else
            {
                if (row is null)
                {
                    row = new AppSetting { Key = def?.Key ?? key };
                    db.Settings.Add(row);
                    rows[key] = row;
                }
                row.Value = isSecret ? protector.Protect(value) : value;
                row.UpdatedAt = now;
                row.UpdatedBy = user.Name;
            }

            if (isSecret)
                audit.Record(SettingAuditAction, nameof(AppSetting), key, null,
                    details: new { secret = true, change = value is null ? "cleared" : "set" });
            else
                audit.Record(SettingAuditAction, nameof(AppSetting), key, null,
                    details: new { before, after = value });
            changed.Add(key);
        }

        if (changed.Count == 0) return changed;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Settings section {Section} saved by {User}: {Keys}", section, user.Name, string.Join(", ", changed));
        await reloader.ReloadAsync(ct);
        return changed;
    }

    /// <summary>
    /// "Test directory query": runs the directory fetch with the saved settings and the sync's validation, read-only
    /// (no sync run, no changes to people). Audited as <c>directory.preview</c>. A fetch error is returned, not thrown.
    /// </summary>
    public async Task<DirectoryPreviewOutcome> PreviewDirectoryAsync(CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        DirectoryPreview? preview = null;
        string? error = null;
        try
        {
            preview = await sync.PreviewAsync(PreviewSampleSize, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Directory preview failed.");
            error = Sanitize(ex);
        }

        audit.Record("directory.preview", "Directory", null, null, reason: error, details: new
        {
            Success = preview is not null,
            preview?.Fetched, preview?.Valid, preview?.Quarantined, preview?.MappedToSite, preview?.UnmappedSite,
            preview?.MissingSite, preview?.WouldReject,
        });
        await db.SaveChangesAsync(CancellationToken.None);
        return new DirectoryPreviewOutcome(preview, error);
    }

    /// <summary>
    /// "Send test email": sends one message straight through the relay (not the outbox) with the saved settings.
    /// Audited as <c>mail.test</c>. A relay error is returned (sanitized), not thrown.
    /// </summary>
    public async Task<MailTestResult> SendTestMailAsync(string? to, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        var address = EmailAddresses.Normalize(to)
                      ?? throw new DomainException("mail.test-address", "Enter a single valid email address to send the test to.");
        var now = clock.UtcNow;
        string? error = null;
        try
        {
            await relay.SendAsync(new MailMessage(address, "Asset Tracker test message",
                $"This is a test message from the asset tracker, sent from Admin → Settings by {user.Name} at {now:yyyy-MM-dd HH:mm} UTC.\n\n" +
                "If you received it, the mail relay settings work."), ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Test email to {To} failed.", address);
            error = Sanitize(ex);
        }

        audit.Record("mail.test", "Mail", null, null, reason: error, details: new { To = address, Success = error is null });
        await db.SaveChangesAsync(CancellationToken.None);
        return new MailTestResult(address, error is null, error);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Exception type and message on one line, truncated; no stack trace or inner details.</summary>
    public static string Sanitize(Exception ex)
    {
        var text = Whitespace().Replace($"{ex.GetType().Name}: {ex.Message}", " ").Trim();
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength] + "…";
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
