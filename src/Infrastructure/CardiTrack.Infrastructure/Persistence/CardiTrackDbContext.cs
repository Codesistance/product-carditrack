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
    public DbSet<UserOrganization> UserOrganizations => Set<UserOrganization>();
    public DbSet<CaregiverInvite> CaregiverInvites => Set<CaregiverInvite>();
    public DbSet<FamilyJoinRequest> FamilyJoinRequests => Set<FamilyJoinRequest>();

    // Device & Health Data
    public DbSet<DeviceConnection> DeviceConnections => Set<DeviceConnection>();
    // Caregiver-requested history re-pulls: the work order the Worker drains, and its record
    public DbSet<DeviceHistoryRepull> DeviceHistoryRepulls => Set<DeviceHistoryRepull>();
    public DbSet<PendingGrantRevocation> PendingGrantRevocations => Set<PendingGrantRevocation>();
    public DbSet<DeviceConnectionInvite> DeviceConnectionInvites => Set<DeviceConnectionInvite>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<DeviceActivityLog> DeviceActivityLogs => Set<DeviceActivityLog>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AlertResponse> AlertResponses => Set<AlertResponse>();
    public DbSet<PatternBaseline> PatternBaselines => Set<PatternBaseline>();
    public DbSet<DeviceTypeSyncProfile> DeviceTypeSyncProfiles => Set<DeviceTypeSyncProfile>();
    public DbSet<GranularMetricHour> GranularMetricHours => Set<GranularMetricHour>();
    public DbSet<MetricRollupHourly> MetricRollupsHourly => Set<MetricRollupHourly>();
    public DbSet<DigestEntry> DigestEntries => Set<DigestEntry>();
    public DbSet<RealtimeAssessment> RealtimeAssessments => Set<RealtimeAssessment>();
    public DbSet<MemberQuestionnaire> MemberQuestionnaires => Set<MemberQuestionnaire>();
    public DbSet<MedicalEntry> MedicalEntries => Set<MedicalEntry>();
    public DbSet<EnvironmentalReading> EnvironmentalReadings => Set<EnvironmentalReading>();

    /// <summary>
    /// Beat-level detail for the analysis windows behind an irregular-rhythm notification. The
    /// only sub-minute cardiac data in the schema — see <see cref="RhythmEpisode"/> for why it is
    /// evidence about a finding already made rather than a substrate for making one.
    /// </summary>
    public DbSet<RhythmEpisode> RhythmEpisodes => Set<RhythmEpisode>();

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
    public DbSet<BenignJudgement> BenignJudgements => Set<BenignJudgement>();
    public DbSet<MetricAlarm> MetricAlarms => Set<MetricAlarm>();
    public DbSet<MetricAlarmState> MetricAlarmStates => Set<MetricAlarmState>();

    // Members a generation path has stopped asking the model about, and until when
    public DbSet<MemberAiHold> MemberAiHolds => Set<MemberAiHold>();
    public DbSet<GenerationLease> GenerationLeases => Set<GenerationLease>();

    // Member chat (Scenario 1)
    public DbSet<MemberChatSession> MemberChatSessions => Set<MemberChatSession>();
    public DbSet<MemberChatTurn> MemberChatTurns => Set<MemberChatTurn>();
    public DbSet<MemberChatTurnUsage> MemberChatTurnUsages => Set<MemberChatTurnUsage>();

    // The dated record of what the Advise pass has noticed, beside the current-guidance row
    // it is deliberately not part of — see MemberAdviseObservation.
    public DbSet<MemberAdviseObservation> MemberAdviseObservations => Set<MemberAdviseObservation>();

    // Dashboard status line (batch-generated, API-served)
    public DbSet<MemberStatusLine> MemberStatusLines => Set<MemberStatusLine>();

    // Health-data exports. The row is the request and its outcome; the rendered bytes live in
    // the export bucket (docs/infrastructure.md — files never in the database).
    public DbSet<Report> Reports => Set<Report>();

    /// <summary>
    /// Recorded "I accept responsibility" step-ups that authorize one export.
    /// Append-only aside from the consume stamp.
    /// </summary>
    public DbSet<ExportConsent> ExportConsents => Set<ExportConsent>();
    public DbSet<CardiMemberCreationKey> CardiMemberCreationKeys => Set<CardiMemberCreationKey>();

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
