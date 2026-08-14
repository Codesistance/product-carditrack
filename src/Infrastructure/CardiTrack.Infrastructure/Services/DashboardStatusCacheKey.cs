namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The cache key <see cref="HealthInsightService"/>'s live status line lives under, shared with
/// every producer that can move a member's severity tier — so a fresh alert or resolution can
/// invalidate the line instead of leaving a caregiver reading a sentence for the tier they were
/// on before it changed, for up to the line's own TTL.
/// </summary>
internal static class DashboardStatusCacheKey
{
    public static string For(Guid cardiMemberId) => $"dashboard-status:{cardiMemberId}";
}
