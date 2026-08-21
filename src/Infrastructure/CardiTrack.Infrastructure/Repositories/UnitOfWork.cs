using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Storage;

namespace CardiTrack.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly CardiTrackDbContext _context;
    private IDbContextTransaction? _transaction;

    public IOrganizationRepository Organizations { get; }
    public IUserRepository Users { get; }
    public ICardiMemberRepository CardiMembers { get; }
    public ISubscriptionRepository Subscriptions { get; }
    public IUserCardiMemberRepository UserCardiMembers { get; }
    public IDeviceConnectionRepository DeviceConnections { get; }
    public IActivityLogRepository ActivityLogs { get; }
    public IDeviceActivityLogRepository DeviceActivityLogs { get; }
    public IDeviceRepository Devices { get; }
    public IAlertRepository Alerts { get; }
    public IPatternBaselineRepository PatternBaselines { get; }
    public IGranularMetricRepository GranularMetrics { get; }
    public IDigestRepository Digests { get; }
    public IRealtimeAssessmentRepository RealtimeAssessments { get; }
    public IMemberQuestionnaireRepository MemberQuestionnaires { get; }
    public IEnvironmentalReadingRepository EnvironmentalReadings { get; }
    public INotificationRepository Notifications { get; }
    public INotificationMuteRepository NotificationMutes { get; }
    public INotificationDeliveryRepository NotificationDeliveries { get; }
    public IPushDeviceTokenRepository PushDeviceTokens { get; }
    public INotificationPreferenceRepository NotificationPreferences { get; }
    public IAlertPreferenceRepository AlertPreferences { get; }
    public IMemberChatSessionRepository MemberChatSessions { get; }
    public IMemberChatTurnRepository MemberChatTurns { get; }
    public IMemberChatTurnUsageRepository MemberChatTurnUsages { get; }
    public IMemberStatusLineRepository MemberStatusLines { get; }

    public UnitOfWork(
        CardiTrackDbContext context,
        IOrganizationRepository organizations,
        IUserRepository users,
        ICardiMemberRepository cardiMembers,
        ISubscriptionRepository subscriptions,
        IUserCardiMemberRepository userCardiMembers,
        IDeviceConnectionRepository deviceConnections,
        IActivityLogRepository activityLogs,
        IDeviceActivityLogRepository deviceActivityLogs,
        IDeviceRepository devices,
        IAlertRepository alerts,
        IPatternBaselineRepository patternBaselines,
        IGranularMetricRepository granularMetrics,
        IDigestRepository digests,
        IRealtimeAssessmentRepository realtimeAssessments,
        IMemberQuestionnaireRepository memberQuestionnaires,
        IEnvironmentalReadingRepository environmentalReadings,
        INotificationRepository notifications,
        INotificationMuteRepository notificationMutes,
        INotificationDeliveryRepository notificationDeliveries,
        IPushDeviceTokenRepository pushDeviceTokens,
        INotificationPreferenceRepository notificationPreferences,
        IAlertPreferenceRepository alertPreferences,
        IMemberChatSessionRepository memberChatSessions,
        IMemberChatTurnRepository memberChatTurns,
        IMemberChatTurnUsageRepository memberChatTurnUsages,
        IMemberStatusLineRepository memberStatusLines)
    {
        _context = context;
        Organizations = organizations;
        Users = users;
        CardiMembers = cardiMembers;
        Subscriptions = subscriptions;
        UserCardiMembers = userCardiMembers;
        DeviceConnections = deviceConnections;
        ActivityLogs = activityLogs;
        DeviceActivityLogs = deviceActivityLogs;
        Devices = devices;
        Alerts = alerts;
        PatternBaselines = patternBaselines;
        GranularMetrics = granularMetrics;
        Digests = digests;
        RealtimeAssessments = realtimeAssessments;
        MemberQuestionnaires = memberQuestionnaires;
        EnvironmentalReadings = environmentalReadings;
        Notifications = notifications;
        NotificationMutes = notificationMutes;
        NotificationDeliveries = notificationDeliveries;
        PushDeviceTokens = pushDeviceTokens;
        NotificationPreferences = notificationPreferences;
        AlertPreferences = alertPreferences;
        MemberChatSessions = memberChatSessions;
        MemberChatTurns = memberChatTurns;
        MemberChatTurnUsages = memberChatTurnUsages;
        MemberStatusLines = memberStatusLines;
    }

    public async Task<int> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync();
    }

    public async Task BeginTransactionAsync()
    {
        _transaction = await _context.Database.BeginTransactionAsync();
    }

    public async Task CommitTransactionAsync()
    {
        if (_transaction != null)
        {
            await _transaction.CommitAsync();
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public async Task RollbackTransactionAsync()
    {
        if (_transaction != null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _context.Dispose();
    }
}
