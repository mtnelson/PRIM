using Microsoft.EntityFrameworkCore;

namespace Prim.Data;

public class PrimDbContext : DbContext
{
    public PrimDbContext(DbContextOptions<PrimDbContext> options) : base(options) { }

    public DbSet<RecordItem> Records => Set<RecordItem>();
    public DbSet<Container> Containers => Set<Container>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<WorkspaceItem> WorkspaceItems => Set<WorkspaceItem>();
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<UserGridLayout> UserGridLayouts => Set<UserGridLayout>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Location>().HasIndex(l => new { l.LocationType, l.ParentId, l.LocationName }).IsUnique();
        m.Entity<Container>().HasIndex(c => new { c.ContainerType, c.ContainerName }).IsUnique();
        m.Entity<RecordItem>().HasIndex(r => r.RecordNumber).IsUnique();
        m.Entity<RecordItem>().HasIndex(r => r.Barcode).IsUnique();
        m.Entity<AppUser>().HasIndex(u => u.UserId).IsUnique();
        m.Entity<WorkspaceItem>().HasIndex(w => new { w.OwnerUserId, w.Slot, w.ObjectKind, w.ObjectId }).IsUnique();
        m.Entity<UserGridLayout>().HasIndex(g => new { g.UserId, g.GridId }).IsUnique();
    }
}
