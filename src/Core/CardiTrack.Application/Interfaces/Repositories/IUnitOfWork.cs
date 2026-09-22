namespace CardiTrack.Application.Interfaces.Repositories;

public interface IUnitOfWork : IDisposable
{
    IOrganizationRepository Organizations { get; }
    IUserRepository Users { get; }
    ICardiMemberRepository CardiMembers { get; }
    ISubscriptionRepository Subscriptions { get; }
    IUserCardiMemberRepository UserCardiMembers { get; }
    IUserOrganizationRepository UserOrganizations { get; }
    ICaregiverInviteRepository CaregiverInvites { get; }
    IFamilyJoinRequestRepository FamilyJoinRequests { get; }
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
    IAlertPreferenceRepository AlertPreferences { get; }
    IMetricAlarmRepository MetricAlarms { get; }
    IMetricAlarmStateRepository MetricAlarmStates { get; }
    IMemberChatSessionRepository MemberChatSessions { get; }
    IMemberChatTurnRepository MemberChatTurns { get; }
    IMemberChatTurnUsageRepository MemberChatTurnUsages { get; }
    IMemberStatusLineRepository MemberStatusLines { get; }
    IMemberAdviseRepository MemberAdvises { get; }
    IMemberAdviseObservationRepository MemberAdviseObservations { get; }
    IMemberInsightRepository MemberInsights { get; }
    IReportRepository Reports { get; }
    IExportConsentRepository ExportConsents { get; }
    ICardiMemberCreationKeyRepository CardiMemberCreationKeys { get; }
    IMemberAiHoldRepository MemberAiHolds { get; }

    /// <summary>The once-per-period claim a generator takes before it pays for a model call.</summary>
    IGenerationLeaseRepository GenerationLeases { get; }
    IDeviceHistoryRepullRepository DeviceHistoryRepulls { get; }
    IDeviceConnectionInviteRepository DeviceConnectionInvites { get; }

    Task<int> SaveChangesAsync();

    /// <summary>
    /// Forgets every entity this unit of work is tracking, saved or not. For a pass that works
    /// through many members on one scope: after a failed save the failed entries stay tracked and
    /// would make every later save in the same scope fail the same way, and after a successful one
    /// they are dead weight that every subsequent save still has to scan.
    /// </summary>
    void ClearTracking();

    Task BeginTransactionAsync();
    Task CommitTransactionAsync();
    Task RollbackTransactionAsync();
}
