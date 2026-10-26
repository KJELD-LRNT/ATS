using System.Text;
using AssetTracker.Core.Domain;

namespace AssetTracker.Core.Notifications;

/// <summary>What a mail says about a loan's item. Built once per loan, shared by all its recipients.</summary>
public sealed record LoanMailFacts(
    string ItemSummary,
    string? ItemDescription,
    bool IsPool,
    int Quantity,
    int Outstanding,
    string HolderName,
    string? SiteName,
    DateOnly? DueDate,
    string? PhoneNumber)
{
    /// <param name="includePhoneNumber">See <see cref="NotificationOptions.IncludePhoneNumberInMail"/>.</param>
    public static LoanMailFacts From(Loan loan, IndividualAsset? asset, Pool? pool, Site? site, bool includePhoneNumber)
    {
        var holder = !string.IsNullOrWhiteSpace(loan.HolderNameSnapshot)
            ? loan.HolderNameSnapshot
            : loan.Holder?.DisplayName ?? $"person #{loan.HolderId}";

        if (asset is not null)
        {
            return new LoanMailFacts(
                ItemSummary: $"{asset.Type} {asset.Tag}",
                ItemDescription: string.IsNullOrWhiteSpace(asset.Description) ? null : asset.Description,
                IsPool: false, Quantity: loan.Quantity, Outstanding: loan.OutstandingQuantity,
                HolderName: holder, SiteName: site?.Name, DueDate: loan.DueDate,
                PhoneNumber: includePhoneNumber && asset.Type is AssetType.Phone or AssetType.PhoneNumber
                    ? asset.PhoneNumber : null);
        }

        var sku = pool?.Sku ?? $"pool #{loan.PoolId}";
        return new LoanMailFacts(
            ItemSummary: $"{loan.Quantity} x {sku}",
            ItemDescription: pool is null || string.IsNullOrWhiteSpace(pool.Description) ? null : $"{pool.Type}: {pool.Description}",
            IsPool: true, Quantity: loan.Quantity, Outstanding: loan.OutstandingQuantity,
            HolderName: holder, SiteName: site?.Name, DueDate: loan.DueDate, PhoneNumber: null);
    }
}

/// <summary>Who a single message is addressed to; drives the greeting and IT-only notes.</summary>
public sealed record MailAudience(bool IsIt, string? Name, IReadOnlyList<string> ItNotes);

public sealed record ComposedMail(string Subject, string Body);

/// <summary>
/// Plain-text subject/body templates. Content is limited to what the recipient needs to identify the item;
/// phone numbers only when <see cref="NotificationOptions.IncludePhoneNumberInMail"/> is set.
/// </summary>
public static class NotificationTemplates
{
    private const string Footer =
        "This is an automated message from the asset tracker. Please contact IT if anything is incorrect.";

    public static ComposedMail Checkout(LoanMailFacts f, MailAudience to) => Compose(
        $"Checked out: {f.ItemSummary}",
        to,
        to.IsIt ? $"{f.ItemSummary} has been checked out to {f.HolderName}." : $"{f.ItemSummary} has been checked out to you.",
        f, includeOutstanding: false, extra: null);

    /// <param name="returnedQuantity">Units returned by this event.</param>
    /// <param name="partial">True when units remain outstanding (pool partial return).</param>
    public static ComposedMail Return(LoanMailFacts f, MailAudience to, int returnedQuantity, bool partial)
    {
        var subject = partial ? $"Partial return recorded: {f.ItemSummary}" : $"Returned: {f.ItemSummary}";
        var lead = f.IsPool
            ? $"A return of {returnedQuantity} unit(s) has been recorded for {f.ItemSummary} held by {f.HolderName}."
            : $"The return of {f.ItemSummary} held by {f.HolderName} has been recorded.";
        var extra = new List<(string, string)> { ("Returned now", returnedQuantity.ToString()) };
        if (f.IsPool) extra.Add(("Still outstanding", f.Outstanding.ToString()));
        return Compose(subject, to, lead, f, includeOutstanding: false, extra);
    }

    public static ComposedMail Correction(LoanMailFacts f, MailAudience to, string? reason, IReadOnlyList<string> previousHolders)
    {
        var extra = new List<(string, string)>();
        if (previousHolders.Count > 0) extra.Add(("Previous holder", string.Join(", ", previousHolders)));
        extra.Add(("Reason", string.IsNullOrWhiteSpace(reason) ? "(not given)" : reason!));
        return Compose($"Loan record corrected: {f.ItemSummary}", to,
            $"IT has corrected the loan record for {f.ItemSummary}. The corrected details are below.",
            f, includeOutstanding: f.IsPool, extra);
    }

    public static ComposedMail OverdueReminder(LoanMailFacts f, MailAudience to, DateOnly localToday)
    {
        var due = f.DueDate ?? localToday;
        // D4: overdue from the due date itself; 0 days past due reads as "due today".
        var days = OverduePolicy.DaysOverdue(due, localToday);
        var state = days == 0 ? "is due back today" : $"was due back on {due:yyyy-MM-dd} and is {days} day(s) overdue";
        var lead = to.IsIt
            ? $"{f.ItemSummary} held by {f.HolderName} {state}."
            : $"{f.ItemSummary} {state}. Please return it to IT or contact IT to agree a new date.";
        var subject = days == 0
            ? $"Due today: {f.ItemSummary} is due {due:yyyy-MM-dd}"
            : $"Overdue: {f.ItemSummary} was due {due:yyyy-MM-dd}";
        return Compose(subject, to, lead, f, includeOutstanding: f.IsPool, extra: null);
    }

    private static ComposedMail Compose(string subject, MailAudience to, string lead, LoanMailFacts f,
        bool includeOutstanding, IEnumerable<(string Label, string Value)>? extra)
    {
        var sb = new StringBuilder();
        sb.AppendLine(to.IsIt ? "Hello IT," : $"Hello {to.Name},").AppendLine();
        sb.AppendLine(lead).AppendLine();

        Line(sb, "Item", f.ItemSummary);
        if (f.ItemDescription is not null) Line(sb, "Description", f.ItemDescription);
        if (f.IsPool) Line(sb, "Quantity", f.Quantity.ToString());
        if (includeOutstanding) Line(sb, "Outstanding", f.Outstanding.ToString());
        Line(sb, "Holder", f.HolderName);
        Line(sb, "Site", f.SiteName ?? "(unknown)");
        Line(sb, "Due date", f.DueDate?.ToString("yyyy-MM-dd") ?? "none");
        if (f.PhoneNumber is not null) Line(sb, "Phone number", f.PhoneNumber);
        foreach (var (label, value) in extra ?? []) Line(sb, label, value);

        if (to.IsIt && to.ItNotes.Count > 0)
        {
            sb.AppendLine();
            foreach (var n in to.ItNotes) sb.AppendLine($"Note for IT: {n}");
        }

        sb.AppendLine().AppendLine(Footer);
        return new ComposedMail(Truncate(subject, 500), sb.ToString());
    }

    private static void Line(StringBuilder sb, string label, string value) => sb.AppendLine($"{label + ":",-20}{value}");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
