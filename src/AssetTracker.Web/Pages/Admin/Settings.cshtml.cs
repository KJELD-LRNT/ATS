using AssetTracker.Core.Common;
using AssetTracker.Core.Security;
using AssetTracker.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>
/// Database-backed settings (WP4.1): directory query, sync safety, mail relay, notifications and default time zone,
/// with the effective value and its source per field, plus "Test directory query" and "Send test email".
/// Each section is its own form (field names are the configuration keys); a section save validates everything
/// first and changes nothing on any error. The SMTP password is write-only.
/// </summary>
[Authorize(Policy = Policies.AdminOnly)]
public class SettingsModel(SettingsService settings, IConfiguration configuration) : PageModel
{
    /// <summary>The config file still has the old <c>Roles:*GroupSids</c> keys (ignored now; roles are given in the app).</summary>
    public bool LegacyRoleSettingsPresent => AssetTracker.Web.Security.LegacyRoleSettings.ArePresent(configuration);

    public const string ClearPasswordField = "Mail:Password.Clear";

    private static readonly Lazy<IReadOnlyList<string>> TimeZones = new(() =>
        TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Order(StringComparer.OrdinalIgnoreCase).ToList());

    public SettingsOverview Overview { get; private set; } = new([], null);

    /// <summary>Values to re-render after a failed save (never contains the password).</summary>
    public Dictionary<string, string> Posted { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> FieldErrors { get; private set; } = new Dictionary<string, string>();

    public DirectoryPreviewOutcome? Preview { get; private set; }
    public MailTestResult? MailTest { get; private set; }

    [BindProperty]
    public string? TestMailTo { get; set; }

    [TempData]
    public string? Message { get; set; }

    public static IReadOnlyList<string> TimeZoneIds => TimeZones.Value;

    public static IReadOnlyList<(string Id, string Title)> Sections =>
    [
        (SettingSections.Directory, "Directory (Active Directory)"),
        (SettingSections.Sync, "Directory sync safety"),
        (SettingSections.Mail, "Mail relay"),
        (SettingSections.Notifications, "Notifications"),
        (SettingSections.Clock, "Default time zone"),
    ];

    public Task OnGetAsync(CancellationToken ct) => LoadAsync(ct);

    /// <summary>Saves one section (hidden field <c>section</c>; defaults to notifications, which holds the IT mailbox).</summary>
    public async Task<IActionResult> OnPostAsync([FromForm] string? section, CancellationToken ct)
    {
        section = string.IsNullOrWhiteSpace(section) ? SettingSections.Notifications : section.Trim();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var keys = SettingCatalog.InSection(section).Select(d => d.Key).ToList();
        if (section == SettingSections.Notifications) keys.Add(SettingCatalog.ItMailboxField);
        foreach (var key in keys)
            if (Request.Form.TryGetValue(key, out var v))
                values[key] = v.ToString();
        var clearPassword = Request.Form.TryGetValue(ClearPasswordField, out var clear) &&
                            clear.Any(c => string.Equals(c, "true", StringComparison.OrdinalIgnoreCase));

        try
        {
            var changed = await settings.SaveSectionAsync(section, values, clearPassword, ct);
            Message = changed.Count == 0 ? "No changes." : $"Saved: {string.Join(", ", changed)}.";
            return RedirectToPage(null, null, null, section);
        }
        catch (SettingsValidationException ex)
        {
            FieldErrors = ex.Errors;
            foreach (var (key, error) in ex.Errors) ModelState.AddModelError(key, error);
            Response.StatusCode = StatusCodes.Status400BadRequest;
        }
        catch (DomainException ex)
        {
            this.Show(ex);
        }

        foreach (var (key, value) in values)
            if (!string.Equals(key, SettingCatalog.MailPasswordKey, StringComparison.OrdinalIgnoreCase))
                Posted[key] = value ?? "";
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostTestDirectoryAsync(CancellationToken ct)
    {
        Preview = await settings.PreviewDirectoryAsync(ct);
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostTestMailAsync(CancellationToken ct)
    {
        try
        {
            MailTest = await settings.SendTestMailAsync(TestMailTo, ct);
        }
        catch (DomainException ex)
        {
            this.Show(ex);
        }
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>The value to put in a field: re-posted text after an error, else the database override (if any).</summary>
    public string FieldValue(SettingView v) =>
        v.Definition.Kind == SettingKind.Secret ? ""
        : Posted.TryGetValue(v.Definition.Key, out var posted) ? posted
        : v.DatabaseValue ?? "";

    public string? ItMailboxValue => Posted.TryGetValue(SettingCatalog.ItMailboxField, out var p) ? p : Overview.ItMailbox;

    public static string FieldId(string key) => "s-" + key.Replace(':', '-').Replace('.', '-');

    public static string SourceLabel(SettingSource source) => source switch
    {
        SettingSource.Database => "database",
        SettingSource.ConfigFile => "config file",
        SettingSource.Default => "default",
        _ => "not set",
    };

    private async Task LoadAsync(CancellationToken ct) => Overview = await settings.GetAsync(ct);
}
