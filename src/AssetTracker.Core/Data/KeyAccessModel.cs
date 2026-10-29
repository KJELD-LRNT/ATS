using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Data;

internal static class KeyAccessModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<AccessBuilding>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => new { x.SiteId, x.Name }).IsUnique();
            e.HasOne(x => x.Site).WithMany().OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AccessRoom>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.DrawingRoomId).HasMaxLength(100);
            e.HasIndex(x => new { x.BuildingId, x.Code }).IsUnique();
            e.HasOne(x => x.Building).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Floor).WithMany().OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<KeyDefinition>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => new { x.BuildingId, x.Code }).IsUnique();
            e.HasOne(x => x.Building).WithMany().OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<KeyRoom>(e =>
        {
            e.HasKey(x => new { x.KeyDefinitionId, x.RoomId });
            e.HasOne<KeyDefinition>().WithMany().HasForeignKey(x => x.KeyDefinitionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccessRoom>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<KeyLink>(e =>
        {
            e.HasKey(x => new { x.MasterId, x.SubordinateId });
            e.HasOne<KeyDefinition>().WithMany().HasForeignKey(x => x.MasterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<KeyDefinition>().WithMany().HasForeignKey(x => x.SubordinateId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<KeyCopy>(e =>
        {
            e.Property(x => x.Tag).HasMaxLength(64);
            e.Property(x => x.StorageLocation).HasMaxLength(200);
            e.HasIndex(x => x.Tag).IsUnique();
            e.HasOne(x => x.Definition).WithMany().HasForeignKey(x => x.KeyDefinitionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Lockbox).WithMany().HasForeignKey(x => x.LockboxId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<DigitalLock>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Manufacturer).HasMaxLength(100);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.ExternalId).HasMaxLength(200);
            e.Property(x => x.Location).HasMaxLength(200);
            e.HasIndex(x => new { x.BuildingId, x.Name }).IsUnique();
            e.HasOne(x => x.Building).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Room).WithMany().OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<DigitalPermission>(e =>
        {
            e.Property(x => x.IssuedBy).HasMaxLength(256);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.VerificationReference).HasMaxLength(200);
            e.HasOne(x => x.Device).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Person).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PersonId, x.State });
            e.ToTable(t => t.HasCheckConstraint("CK_DigitalPermission_Window", "[EndsAt] > [StartsAt]"));
        });
        b.Entity<KeyLoan>(e =>
        {
            e.Property(x => x.IssuedBy).HasMaxLength(256);
            e.Property(x => x.ReceivedBy).HasMaxLength(256);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.ReturnCondition).HasMaxLength(200);
            e.HasOne(x => x.Copy).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Person).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Permission).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.CopyId).IsUnique().HasFilter("[ClosedAt] IS NULL").HasDatabaseName("UX_KeyLoan_OpenCopy");
            e.HasIndex(x => new { x.PersonId, x.ClosedAt });
        });
        b.Entity<KeyAccessRevision>().HasData(new KeyAccessRevision { Id = 1, Version = 0 });
    }
}
