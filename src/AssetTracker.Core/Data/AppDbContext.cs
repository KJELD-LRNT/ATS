using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AssetTracker.Core.Data;

/// <summary>
/// Provider-agnostic model. Production uses SQL Server; tests may use SQLite.
/// Index filters use bracket-quoted identifiers, which both providers accept.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<IndividualAsset> Assets => Set<IndividualAsset>();
    public DbSet<Pool> Pools => Set<Pool>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<LoanEvent> LoanEvents => Set<LoanEvent>();
    public DbSet<StockEvent> StockEvents => Set<StockEvent>();
    public DbSet<ManagerSiteGrant> ManagerSiteGrants => Set<ManagerSiteGrant>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<NotificationOutbox> Outbox => Set<NotificationOutbox>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        // SQLite (tests only) cannot compare/order DateTimeOffset in SQL; store as sortable binary there.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            c.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Site>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.DirectoryValue).HasMaxLength(200);
            e.HasIndex(x => x.Key).IsUnique();
            e.HasIndex(x => x.DirectoryValue).IsUnique().HasFilter("[DirectoryValue] IS NOT NULL");
        });

        b.Entity<Person>(e =>
        {
            e.Property(x => x.DirectoryId).HasMaxLength(64);
            e.Property(x => x.Username).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.JobTitle).HasMaxLength(256);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Sid).HasMaxLength(100);
            e.HasIndex(x => x.DirectoryId).IsUnique();
            e.HasIndex(x => x.Sid).IsUnique().HasFilter("[Sid] IS NOT NULL");
            e.HasIndex(x => x.Username);
            e.HasIndex(x => x.SiteId);
            e.HasOne(x => x.Site).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IndividualAsset>(e =>
        {
            e.ToTable("IndividualAssets");
            e.Property(x => x.Tag).HasMaxLength(64);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Condition).HasMaxLength(200);
            e.Property(x => x.SerialNumber).HasMaxLength(128);
            e.Property(x => x.Imei).HasMaxLength(20);
            e.Property(x => x.PhoneNumber).HasMaxLength(20);
            e.HasIndex(x => x.Tag).IsUnique();
            // Active (non-retired) phone numbers and IMEIs must be unique.
            e.HasIndex(x => x.PhoneNumber).IsUnique()
                .HasFilter("[PhoneNumber] IS NOT NULL AND [RetiredAt] IS NULL");
            e.HasIndex(x => x.Imei).IsUnique()
                .HasFilter("[Imei] IS NOT NULL AND [RetiredAt] IS NULL");
            e.HasIndex(x => new { x.SiteId, x.Type, x.State });
            e.HasOne(x => x.Site).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Pool>(e =>
        {
            e.Property(x => x.Sku).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => new { x.Sku, x.SiteId }).IsUnique();
            e.HasIndex(x => x.SiteId);
            e.HasOne(x => x.Site).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Pool_Available_NonNegative", "[Available] >= 0");
                t.HasCheckConstraint("CK_Pool_Outstanding_NonNegative", "[Outstanding] >= 0");
            });
        });

        b.Entity<Loan>(e =>
        {
            e.Property(x => x.CheckoutCondition).HasMaxLength(200);
            e.Property(x => x.ReturnCondition).HasMaxLength(200);
            e.Property(x => x.CheckoutNote).HasMaxLength(2000);
            e.Property(x => x.ReturnNote).HasMaxLength(2000);
            e.Property(x => x.HolderNameSnapshot).HasMaxLength(256);
            e.Property(x => x.HolderEmailSnapshot).HasMaxLength(320);
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            // One effective open loan per tagged asset, enforced by the database.
            e.HasIndex(x => x.AssetId).IsUnique()
                .HasFilter("[AssetId] IS NOT NULL AND [ClosedAt] IS NULL")
                .HasDatabaseName("UX_Loans_OpenAsset");
            e.HasIndex(x => new { x.PoolId, x.ClosedAt });
            e.HasIndex(x => new { x.HolderId, x.ClosedAt });
            e.HasIndex(x => new { x.ClosedAt, x.DueDate });
            e.HasOne(x => x.Asset).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Pool).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Holder).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Loan_ExactlyOneTarget",
                    "([AssetId] IS NOT NULL AND [PoolId] IS NULL) OR ([AssetId] IS NULL AND [PoolId] IS NOT NULL)");
                t.HasCheckConstraint("CK_Loan_Quantity_Positive", "[Quantity] > 0");
                t.HasCheckConstraint("CK_Loan_Returned_Range", "[ReturnedQuantity] >= 0 AND [ReturnedQuantity] <= [Quantity]");
            });
        });

        b.Entity<LoanEvent>(e =>
        {
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.Condition).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.HasIndex(x => x.LoanId);
            e.HasOne(x => x.Loan).WithMany(l => l.Events).HasForeignKey(x => x.LoanId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Supersedes).WithMany().HasForeignKey(x => x.SupersedesEventId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StockEvent>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.HasIndex(x => x.PoolId);
            e.HasOne(x => x.Pool).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ManagerSiteGrant>(e =>
        {
            e.Property(x => x.PrincipalDirectoryId).HasMaxLength(64);
            e.Property(x => x.PrincipalName).HasMaxLength(256);
            e.Property(x => x.GrantedBy).HasMaxLength(256);
            e.HasIndex(x => new { x.PrincipalDirectoryId, x.SiteId }).IsUnique();
            e.HasOne(x => x.Site).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });

        b.Entity<NotificationOutbox>(e =>
        {
            e.ToTable("NotificationOutbox");
            e.Property(x => x.DedupeKey).HasMaxLength(400);
            e.Property(x => x.RecipientEmail).HasMaxLength(320);
            e.Property(x => x.SkipReason).HasMaxLength(500);
            e.Property(x => x.Subject).HasMaxLength(500);
            e.HasIndex(x => x.DedupeKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasOne(x => x.Loan).WithMany().HasForeignKey(x => x.LoanId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.LoanEvent).WithMany().HasForeignKey(x => x.LoanEventId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<SyncRun>(e =>
        {
            e.Property(x => x.TriggeredBy).HasMaxLength(256);
            e.HasIndex(x => new { x.Status, x.FinishedAt });
        });

        b.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Value).HasMaxLength(2000);
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
        });
    }
}
