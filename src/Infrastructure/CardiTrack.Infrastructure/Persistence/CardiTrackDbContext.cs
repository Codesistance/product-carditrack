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
    public DbSet<GranularMetricHour> GranularMetricHours => Set<GranularMetricHour>();
    public DbSet<MetricRollupHourly> MetricRollupsHourly => Set<MetricRollupHourly>();
    public DbSet<DigestEntry> DigestEntries => Set<DigestEntry>();
    public DbSet<RealtimeAssessment> RealtimeAssessments => Set<RealtimeAssessment>();
    public DbSet<MemberQuestionnaire> MemberQuestionnaires => Set<MemberQuestionnaire>();
    public DbSet<EnvironmentalReading> EnvironmentalReadings => Set<EnvironmentalReading>();

    // Business & Compliance
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // Notifications (data-completeness nudges)
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationMute> NotificationMutes => Set<NotificationMute>();
    public DbSet<NotificationRunLog> NotificationRunLogs => Set<NotificationRunLog>();

    // Push delivery spine (notification_engine.md Phase 3)
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<PushDeviceToken> PushDeviceTokens => Set<PushDeviceToken>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<AlertPreference> AlertPreferences => Set<AlertPreference>();

    // Member chat (Scenario 1)
    public DbSet<MemberChatSession> MemberChatSessions => Set<MemberChatSession>();
    public DbSet<MemberChatTurn> MemberChatTurns => Set<MemberChatTurn>();
    public DbSet<MemberChatTurnUsage> MemberChatTurnUsages => Set<MemberChatTurnUsage>();

    // Dashboard status line (batch-generated, API-served)
    public DbSet<MemberStatusLine> MemberStatusLines => Set<MemberStatusLine>();

    // Health-data exports. The row is the request and its outcome; the rendered bytes live in
    // the export bucket (docs/infrastructure.md — files never in the database).
    public DbSet<Report> Reports => Set<Report>();

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
