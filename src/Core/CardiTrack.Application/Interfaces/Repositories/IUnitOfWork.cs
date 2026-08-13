namespace CardiTrack.Application.Interfaces.Repositories;

public interface IUnitOfWork : IDisposable
{
    IOrganizationRepository Organizations { get; }
    IUserRepository Users { get; }
    ICardiMemberRepository CardiMembers { get; }
    ISubscriptionRepository Subscriptions { get; }
    IUserCardiMemberRepository UserCardiMembers { get; }
    IDeviceConnectionRepository DeviceConnections { get; }
    IActivityLogRepository ActivityLogs { get; }
    IDeviceActivityLogRepository DeviceActivityLogs { get; }
    IDeviceRepository Devices { get; }
    IAlertRepository Alerts { get; }
    IPatternBaselineRepository PatternBaselines { get; }
    IGranularMetricRepository GranularMetrics { get; }
    IDigestRepository Digests { get; }
    IRealtimeAssessmentRepository RealtimeAssessments { get; }
    IMemberQuestionnaireRepository MemberQuestionnaires { get; }
    IEnvironmentalReadingRepository EnvironmentalReadings { get; }
    INotificationRepository Notifications { get; }
    INotificationMuteRepository NotificationMutes { get; }
    INotificationDeliveryRepository NotificationDeliveries { get; }
    IPushDeviceTokenRepository PushDeviceTokens { get; }
    INotificationPreferenceRepository NotificationPreferences { get; }

    Task<int> SaveChangesAsync();
    Task BeginTransactionAsync();
    Task CommitTransactionAsync();
    Task RollbackTransactionAsync();
}
