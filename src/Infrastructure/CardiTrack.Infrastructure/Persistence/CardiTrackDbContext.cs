using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Persistence;

public class CardiTrackDbContext : DbContext
{
    public CardiTrackDbContext(DbContextOptions<CardiTrackDbContext> options) : base(options)
    {
    }

    // Core Entities
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<CardiMember> CardiMembers => Set<CardiMember>();
    public DbSet<UserCardiMember> UserCardiMembers => Set<UserCardiMember>();

    // Device & Health Data
    public DbSet<DeviceConnection> DeviceConnections => Set<DeviceConnection>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<DeviceActivityLog> DeviceActivityLogs => Set<DeviceActivityLog>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<PatternBaseline> PatternBaselines => Set<PatternBaseline>();
    public DbSet<DeviceTypeSyncProfile> DeviceTypeSyncProfiles => Set<DeviceTypeSyncProfile>();

    // Business & Compliance
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all configurations from assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CardiTrackDbContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

    private void UpdateTimestamps()
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.Entity is Domain.Interfaces.IEntity &&
                       (e.State == EntityState.Added || e.State == EntityState.Modified));

        foreach (var entry in entries)
        {
            var entity = (Domain.Interfaces.IEntity)entry.Entity;

            if (entry.State == EntityState.Modified)
            {
                entity.UpdatedDate = DateTime.UtcNow;
            }
        }
    }
}
