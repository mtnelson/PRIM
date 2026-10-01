using Microsoft.EntityFrameworkCore;

namespace Rim.Data;

public class RimDbContext : DbContext
{
    public RimDbContext(DbContextOptions<RimDbContext> options) : base(options) { }

    public DbSet<RecordItem> Records => Set<RecordItem>();
    public DbSet<Container> Containers => Set<Container>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AuditEventArchive> ArchivedAuditEvents => Set<AuditEventArchive>();
    public DbSet<WorkspaceItem> WorkspaceItems => Set<WorkspaceItem>();
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();
    public DbSet<SearchSession> SearchSessions => Set<SearchSession>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<UserGridLayout> UserGridLayouts => Set<UserGridLayout>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<ObjectLabel> ObjectLabels => Set<ObjectLabel>();
    public DbSet<SearchActivity> SearchActivities => Set<SearchActivity>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Location>().HasIndex(l => new { l.LocationType, l.ParentId, l.LocationName }).IsUnique();
        m.Entity<Container>().HasIndex(c => new { c.ContainerType, c.ContainerName }).IsUnique();
        m.Entity<RecordItem>().HasIndex(r => r.RecordNumber).IsUnique();
        m.Entity<RecordItem>().HasIndex(r => r.Barcode).IsUnique();
        // H3: unique barcode indexes so barcode scans resolve on every table.
        m.Entity<Container>().HasIndex(c => c.Barcode).IsUnique().HasDatabaseName("IX_Containers_Barcode");
        m.Entity<Location>().HasIndex(l => l.Barcode).IsUnique().HasDatabaseName("IX_Locations_Barcode");
        m.Entity<AppUser>().HasIndex(u => u.Barcode).IsUnique().HasDatabaseName("IX_Users_Barcode");
        // H5: covering the hot lookup paths (child expansion, home navigation).
        m.Entity<RecordItem>().HasIndex(r => r.ParentRecordId).HasDatabaseName("IX_Records_ParentRecordId");
        m.Entity<RecordItem>().HasIndex(r => new { r.HomeKind, r.HomeRefId }).HasDatabaseName("IX_Records_Home");
        m.Entity<Container>().HasIndex(c => c.ParentContainerId).HasDatabaseName("IX_Containers_ParentContainerId");
        m.Entity<Container>().HasIndex(c => new { c.HomeKind, c.HomeRefId }).HasDatabaseName("IX_Containers_Home");
        m.Entity<Location>().HasIndex(l => l.ParentId).HasDatabaseName("IX_Locations_ParentId");
        m.Entity<AppUser>().HasIndex(u => u.UserId).IsUnique();
        m.Entity<WorkspaceItem>().HasIndex(w => new { w.OwnerUserId, w.Slot, w.ObjectKind, w.ObjectId }).IsUnique();
        m.Entity<SearchSession>().HasIndex(s => new { s.OwnerUserId, s.PageKind, s.IsOpen });
        m.Entity<UserGridLayout>().HasIndex(g => new { g.UserId, g.GridId }).IsUnique();
        m.Entity<Label>().HasIndex(l => l.Name).IsUnique();
        m.Entity<ObjectLabel>().HasIndex(o => new { o.LabelId, o.ObjectKind, o.ObjectId }).IsUnique();
        m.Entity<ObjectLabel>().HasIndex(o => new { o.ObjectKind, o.ObjectId });
        m.Entity<SearchActivity>().HasIndex(s => new { s.UserId, s.TimestampUtc });
        m.Entity<AuditEvent>().HasIndex(a => new { a.ObjectKind, a.ObjectId });
        m.Entity<AuditEventArchive>().HasIndex(a => new { a.ObjectKind, a.ObjectId });
    }
}
