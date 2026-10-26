using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Privacy;

/// <summary>An inactive person who may be anonymized (admin privacy page).</summary>
public sealed record AnonymizationCandidate(
    int Id, string Username, string DisplayName, string? Email, string? SiteKey, DateTimeOffset LastSyncedAt,
    int OpenLoans, int TotalLoans);

/// <summary>What one anonymization changed (counts only; also stored in the audit event).</summary>
public sealed record AnonymizationResult(int PersonId, string Pseudonym, int LoansScrubbed, int OutboxRowsScrubbed,
    int OutboxRowsSkipped, int GrantsRemoved);

/// <summary>
/// Client-directed anonymization of one person (docs/privacy-and-retention.md). Admin only; audited as
/// <c>privacy.anonymize</c> in the same transaction.
/// <para>
/// Replaces the person's name, username, title, email, SID and site with a pseudonym/null and sets
/// <see cref="Person.IsAnonymized"/> so directory sync never refreshes or reactivates the row. Scrubs the holder
/// snapshots on the person's loans and the subject/body/recipient/dedupe key of outbox rows about those loans or
/// addressed to the person (Pending rows become Skipped, so nothing is sent). Removes the person's manager site grants.
/// Ids, quantities, dates and statuses are kept, so stock balances and loan timelines are unchanged.
/// </para>
/// <para>
/// Append-only ledgers are not touched: <see cref="LoanEvent"/> stores only <see cref="LoanEvent.HolderId"/>
/// (plus IT free-text notes/reasons), and <see cref="AuditEvent.DetailsJson"/> may still contain the former name or
/// address. Handling those is a documented DBA procedure under client instruction, not an application path.
/// </para>
/// Only inactive people without open loans can be anonymized.
/// </summary>
public class PrivacyService(AppDbContext db, AccessScope access, AuditWriter audit)
{
    public const int ReasonMaxLength = 2000;
    public const int SearchLimit = 50;
    public const string ScrubbedText = "[removed: privacy request]";

    public static string PseudonymFor(int personId) => $"anonymized-{personId}";

    /// <summary>Inactive, not yet anonymized people matching username, display name or email (min. 2 characters).</summary>
    public async Task<IReadOnlyList<AnonymizationCandidate>> SearchInactiveAsync(string? query, CancellationToken ct = default)
    {
        access.EnsureAdmin();
        query = query?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length < 2) return [];
        var pattern = "%" + EscapeLike(query.ToLowerInvariant()) + "%";
        return await db.People.AsNoTracking()
            .Where(p => !p.IsActive && !p.IsAnonymized)
            .Where(p => EF.Functions.Like(p.Username.ToLower(), pattern, "\\") ||
                        EF.Functions.Like(p.DisplayName.ToLower(), pattern, "\\") ||
                        (p.Email != null && EF.Functions.Like(p.Email.ToLower(), pattern, "\\")))
            .OrderBy(p => p.DisplayName).ThenBy(p => p.Username)
            .Take(SearchLimit)
            .Select(p => new AnonymizationCandidate(p.Id, p.Username, p.DisplayName, p.Email,
                p.Site == null ? null : p.Site.Key, p.LastSyncedAt,
                db.Loans.Count(l => l.HolderId == p.Id && l.ClosedAt == null),
                db.Loans.Count(l => l.HolderId == p.Id)))
            .ToListAsync(ct);
    }

    public async Task<AnonymizationResult> AnonymizePersonAsync(int personId, string? reason, CancellationToken ct = default)
    {
        access.EnsureAdmin();
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason))
            throw new DomainException("privacy.reason_required", "A reason (e.g. the client's instruction reference) is required.");
        if (reason.Length > ReasonMaxLength)
            throw new DomainException("privacy.reason_length", $"Reason may be at most {ReasonMaxLength} characters.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var person = await db.People.SingleOrDefaultAsync(p => p.Id == personId, ct)
                     ?? throw new DomainException("privacy.not_found", "Person not found.");
        if (person.IsAnonymized)
            throw new DomainException("privacy.already_anonymized", "This person has already been anonymized.");
        if (person.IsActive)
            throw new DomainException("privacy.person_active",
                "Only people who are inactive in the directory snapshot can be anonymized.");
        if (await db.Loans.AnyAsync(l => l.HolderId == personId && l.ClosedAt == null, ct))
            throw new ConflictException("privacy.open_loans",
                "This person still has open loans. Return or correct them before anonymizing.");

        var pseudonym = PseudonymFor(person.Id);
        var oldEmail = person.Email?.Trim().ToLowerInvariant();

        // Loan holder snapshots (mutable effective columns; the LoanEvent ledger stores only HolderId).
        var loans = await db.Loans.Where(l => l.HolderId == personId).ToListAsync(ct);
        foreach (var l in loans)
        {
            l.HolderNameSnapshot = pseudonym;
            l.HolderEmailSnapshot = null;
        }
        var loanIds = loans.Select(l => l.Id).ToList();

        // Outbox rows about the person's loans (holder and IT copies) or addressed to the person.
        var rows = await db.Outbox
            .Where(x => (x.LoanId != null && loanIds.Contains(x.LoanId.Value)) ||
                        (oldEmail != null && x.RecipientEmail != null && x.RecipientEmail.ToLower() == oldEmail))
            .ToListAsync(ct);
        var skipped = 0;
        foreach (var r in rows)
        {
            if (r.Status == NotificationStatus.Pending)
            {
                r.Status = NotificationStatus.Skipped;
                r.SkipReason = "Scrubbed by privacy anonymization before sending.";
                skipped++;
            }
            if (oldEmail is not null && string.Equals(r.RecipientEmail?.Trim(), oldEmail, StringComparison.OrdinalIgnoreCase))
                r.RecipientEmail = null;
            r.Subject = ScrubbedText;
            r.Body = ScrubbedText;
            r.LastError = r.LastError is null ? null : ScrubbedText;
            r.DedupeKey = $"anonymized:{r.Id}";
        }

        var grants = await db.ManagerSiteGrants.Where(g => g.PrincipalDirectoryId == person.DirectoryId).ToListAsync(ct);
        db.ManagerSiteGrants.RemoveRange(grants);

        person.Username = pseudonym;
        person.DisplayName = pseudonym;
        person.JobTitle = null;
        person.Email = null;
        person.Sid = null;
        person.SiteId = null;
        person.IsActive = false;
        person.IsAnonymized = true;

        var result = new AnonymizationResult(person.Id, pseudonym, loans.Count, rows.Count, skipped, grants.Count);
        audit.Record("privacy.anonymize", nameof(Person), person.Id, null, reason, new
        {
            result.Pseudonym, result.LoansScrubbed, result.OutboxRowsScrubbed, result.OutboxRowsSkipped,
            result.GrantsRemoved,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
