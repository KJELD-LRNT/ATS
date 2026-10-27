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
    public DbSet<AssetType> AssetTypes => Set<AssetType>();
    public DbSet<AssetFieldDefinition> FieldDefinitions => Set<AssetFieldDefinition>();
    public DbSet<AssetFieldValue> FieldValues => Set<AssetFieldValue>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<AssetModel> Models => Set<AssetModel>();
    public DbSet<TagCounter> TagCounters => Set<TagCounter>();
    public DbSet<Pool> Pools => Set<Pool>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<LoanEvent> LoanEvents => Set<LoanEvent>();
    public DbSet<StockEvent> StockEvents => Set<StockEvent>();
    public DbSet<ManagerSiteGrant> ManagerSiteGrants => Set<ManagerSiteGrant>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<Floor> Floors => Set<Floor>();
    public DbSet<Workspot> Workspots => Set<Workspot>();
    public DbSet<FloorTeam> FloorTeams => Set<FloorTeam>();
    public DbSet<WorkspotAssignment> WorkspotAssignments => Set<WorkspotAssignment>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<NotificationOutbox> Outbox => Set<NotificationOutbox>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    /// <summary>Entities whose rows may only ever be inserted (audit trail and event ledgers).</summary>
    private static readonly Type[] AppendOnlyTypes = [typeof(AuditEvent), typeof(LoanEvent), typeof(StockEvent)];

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Append-only enforcement for tracked changes: audit, loan and stock events can be added but never modified
    /// or deleted. (Bulk <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> bypass the change tracker; never use them on these sets.)
    /// </summary>
    private void EnsureAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted &&
                AppendOnlyTypes.Contains(entry.Metadata.ClrType) &&
                !IsTransferLinkOnce(entry))
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.ClrType.Name} is append-only; it cannot be {entry.State.ToString().ToLowerInvariant()}.");
            }
        }
    }

    /// <summary>
    /// The single permitted update: linking a transfer event to its counterpart, once (null → id), inside the
    /// transaction that created the pair. Ids are only known after insert, so the link cannot be set up front.
    /// </summary>
    private static bool IsTransferLinkOnce(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        if (entry.State != EntityState.Modified || entry.Entity is not StockEvent) return false;
        var modified = entry.Properties.Where(p => p.IsModified).ToList();
        return modified.Count == 1 &&
               modified[0].Metadata.Name == nameof(StockEvent.CounterpartEventId) &&
               modified[0].OriginalValue is null;
    }

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
            e.Property(x => x.TimeZoneId).HasMaxLength(100);
            e.Property(x => x.TagCode).HasMaxLength(16);
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

        b.Entity<AssetType>(e =>
        {
            e.ToTable("AssetTypes");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.TagPattern).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<AssetFieldDefinition>(e =>
        {
            e.ToTable("AssetFields");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Options).HasMaxLength(2000);
            e.HasIndex(x => new { x.TypeId, x.Name }).IsUnique();
            e.HasOne(x => x.Type).WithMany(t => t.Fields).HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Vendor>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<AssetModel>(e =>
        {
            e.ToTable("AssetModels");
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => new { x.VendorId, x.TypeId, x.Name }).IsUnique();
            e.HasIndex(x => x.TypeId);
            e.HasOne(x => x.Vendor).WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Type).WithMany().HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TagCounter>(e =>
        {
            e.HasKey(x => new { x.TypeId, x.SiteId });
            e.HasOne<AssetType>().WithMany().HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IndividualAsset>(e =>
        {
            e.ToTable("IndividualAssets");
            e.Property(x => x.Tag).HasMaxLength(64);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Condition).HasMaxLength(200);
            e.Property(x => x.SerialNumber).HasMaxLength(128);
            e.HasIndex(x => x.Tag).IsUnique();
            e.HasIndex(x => new { x.SiteId, x.TypeId, x.State });
            e.HasIndex(x => x.ModelId);
            e.HasOne(x => x.Site).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Type).WithMany().HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Vendor).WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Model).WithMany().HasForeignKey(x => x.ModelId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AssetFieldValue>(e =>
        {
            e.ToTable("AssetFieldValues");
            e.Property(x => x.Value).HasMaxLength(500);
            e.Property(x => x.UniqueKey).HasMaxLength(450);
            e.HasIndex(x => new { x.AssetId, x.FieldId, x.Position }).IsUnique();
            e.HasIndex(x => x.FieldId);
            // A unique field value (or phone number / IMEI, which are unique across fields) may be on one active asset only.
            e.HasIndex(x => x.UniqueKey).IsUnique().HasFilter("[UniqueKey] IS NOT NULL");
            e.HasOne(x => x.Asset).WithMany(a => a.Values).HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Field).WithMany().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Floor>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.ViewBox).HasMaxLength(100);
            e.Property(x => x.ImportedBy).HasMaxLength(256);
            e.HasIndex(x => new { x.SiteId, x.Name }).IsUnique();
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Workspot>(e =>
        {
            e.Property(x => x.ElementId).HasMaxLength(200);
            e.Property(x => x.Label).HasMaxLength(100);
            e.Property(x => x.RoomId).HasMaxLength(100);
            e.Property(x => x.RoomName).HasMaxLength(200);
            e.Property(x => x.RoomType).HasMaxLength(50);
            e.Property(x => x.TableId).HasMaxLength(100);
            e.HasIndex(x => new { x.FloorId, x.ElementId }).IsUnique();
            e.HasIndex(x => new { x.FloorId, x.Label });
            e.HasIndex(x => x.TeamId);
            e.HasOne(x => x.Floor).WithMany(f => f.Workspots).HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<FloorTeam>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => new { x.FloorId, x.Name }).IsUnique();
            e.HasOne(x => x.Floor).WithMany(f => f.Teams).HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkspotAssignment>(e =>
        {
            e.Property(x => x.AssignedBy).HasMaxLength(256);
            e.Property(x => x.ReleasedBy).HasMaxLength(256);
            e.HasIndex(x => new { x.WorkspotId, x.ReleasedAt });
            // Open rows only: a person is in one place, an asset is in one place, a workspot has one person and one desktop.
            e.HasIndex(x => x.PersonId).IsUnique().HasFilter("[PersonId] IS NOT NULL AND [ReleasedAt] IS NULL").HasDatabaseName("UX_WorkspotAssignments_OpenPerson");
            e.HasIndex(x => x.AssetId).IsUnique().HasFilter("[AssetId] IS NOT NULL AND [ReleasedAt] IS NULL").HasDatabaseName("UX_WorkspotAssignments_OpenAsset");
            e.HasIndex(x => x.WorkspotId).IsUnique().HasFilter("[PersonId] IS NOT NULL AND [ReleasedAt] IS NULL").HasDatabaseName("UX_WorkspotAssignments_OpenSeatPerson");
            e.HasIndex(x => new { x.WorkspotId, x.Kind }).IsUnique().HasFilter("[Kind] = 2 AND [ReleasedAt] IS NULL").HasDatabaseName("UX_WorkspotAssignments_OpenSeatDesktop");
            e.HasOne(x => x.Workspot).WithMany().HasForeignKey(x => x.WorkspotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Person).WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_WorkspotAssignment_Kind",
                "([Kind] = 1 AND [PersonId] IS NOT NULL AND [AssetId] IS NULL) OR ([Kind] IN (2, 3) AND [PersonId] IS NULL AND [AssetId] IS NOT NULL)"));
        });

        b.Entity<Pool>(e =>
        {
            e.Property(x => x.Sku).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => new { x.Sku, x.SiteId }).IsUnique();
            e.HasIndex(x => x.SiteId);
            e.HasOne(x => x.Site).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Type).WithMany().HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.Restrict);
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

        b.Entity<RoleAssignment>(e =>
        {
            e.Property(x => x.PrincipalDirectoryId).HasMaxLength(64);
            e.Property(x => x.PrincipalName).HasMaxLength(256);
            e.Property(x => x.GrantedBy).HasMaxLength(256);
            e.HasIndex(x => new { x.Role, x.PrincipalDirectoryId }).IsUnique();
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
