using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

/// <inheritdoc cref="INotificationSnapshotQueries"/>
public class NotificationSnapshotQueries : INotificationSnapshotQueries
{
    private readonly CardiTrackDbContext _context;

    public NotificationSnapshotQueries(CardiTrackDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Guid>> GetActiveOrganizationIdsAsync(
        Guid? after, int take, CancellationToken ct = default)
    {
        var query = _context.Organizations.Where(o => o.IsActive);

        // Ordered by id and resumed from the last one finished, so a crashed run picks up where it
        // stopped instead of starting the estate again.
        if (after.HasValue)
            query = query.Where(o => o.Id.CompareTo(after.Value) > 0);

        return await query.OrderBy(o => o.Id).Take(take).Select(o => o.Id).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<NudgeContext>> BuildContextsForOrganizationAsync(
        Guid organizationId, DateTime utcNow, CancellationToken ct = default)
    {
        var users = await _context.Users
            .Where(u => u.OrganizationId == organizationId && u.IsActive)
            .ToListAsync(ct);

        return await BuildAsync(users, memberFilter: null, utcNow, ct);
    }

    public async Task<IReadOnlyList<NudgeContext>> BuildContextsForUserAsync(
        Guid userId, DateTime utcNow, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        return user is null ? [] : await BuildAsync([user], memberFilter: null, utcNow, ct);
    }

    public async Task<IReadOnlyList<NudgeContext>> BuildContextsForCardiMemberAsync(
        Guid cardiMemberId, DateTime utcNow, CancellationToken ct = default)
    {
        var userIds = await _context.UserCardiMembers
            .Where(l => l.CardiMemberId == cardiMemberId && l.IsActive && l.CanViewHealthData)
            .Select(l => l.UserId)
            .Distinct()
            .ToListAsync(ct);

        if (userIds.Count == 0)
            return [];

        var users = await _context.Users
            .Where(u => userIds.Contains(u.Id) && u.IsActive)
            .ToListAsync(ct);

        return await BuildAsync(users, memberFilter: cardiMemberId, utcNow, ct);
    }

    /// <summary>
    /// Assembles one context per (user, member) pair plus one member-less context per user for
    /// account-level rules.
    /// </summary>
    /// <remarks>
    /// Every per-member figure below — days captured, last activity, whether a baseline exists,
    /// whether a red alert is open — is a <b>single set-based query across all members at once</b>.
    /// Loading activity rows per member to count them in memory would push the estate's entire
    /// daily history through the worker every morning to produce a handful of integers.
    /// </remarks>
    private async Task<IReadOnlyList<NudgeContext>> BuildAsync(
        List<User> users, Guid? memberFilter, DateTime utcNow, CancellationToken ct)
    {
        if (users.Count == 0)
            return [];

        var userIds = users.Select(u => u.Id).ToList();

        var links = await _context.UserCardiMembers
            .Where(l => userIds.Contains(l.UserId) && l.IsActive && l.CanViewHealthData)
            .ToListAsync(ct);

        if (memberFilter.HasValue)
            links = [.. links.Where(l => l.CardiMemberId == memberFilter.Value)];

        var memberIds = links.Select(l => l.CardiMemberId).Distinct().ToList();

        var members = await _context.CardiMembers
            .Where(m => memberIds.Contains(m.Id) && m.IsActive)
            .ToListAsync(ct);

        var connections = await _context.DeviceConnections
            .Where(c => memberIds.Contains(c.CardiMemberId))
            .Select(c => new
            {
                c.Id,
                c.CardiMemberId,
                c.DeviceType,
                c.ConnectionStatus,
                c.LastSyncDate,
                c.Scopes,
                c.IsActive
            })
            .ToListAsync(ct);

        var windowStart = DateOnly.FromDateTime(utcNow).AddDays(-BaselineProgress.PeriodDays);

        // Distinct days that produced data inside the baseline window — progress is measured in
        // days that taught us something, not days that elapsed.
        var coverage = await _context.ActivityLogs
            .Where(a => memberIds.Contains(a.CardiMemberId) && a.Date >= windowStart)
            .GroupBy(a => a.CardiMemberId)
            .Select(g => new { CardiMemberId = g.Key, Days = g.Select(a => a.Date).Distinct().Count() })
            .ToDictionaryAsync(x => x.CardiMemberId, x => x.Days, ct);

        // Unwindowed: a member whose last data predates the window still needs a last-seen date,
        // and that is exactly the member a stall rule cares about.
        var lastActivity = await _context.ActivityLogs
            .Where(a => memberIds.Contains(a.CardiMemberId))
            .GroupBy(a => a.CardiMemberId)
            .Select(g => new { CardiMemberId = g.Key, Last = g.Max(a => a.Date) })
            .ToDictionaryAsync(x => x.CardiMemberId, x => x.Last, ct);

        var withBaseline = (await _context.PatternBaselines
            .Where(b => memberIds.Contains(b.CardiMemberId) && b.PeriodDays >= BaselineProgress.PeriodDays)
            .Select(b => b.CardiMemberId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        // Device-silence alerts, keyed on the rule marker every automated producer stamps into
        // MetricValues. Same discriminator AlertRuleMarkers reads, matched here rather than
        // referenced because that helper is Infrastructure-internal to the alert path.
        var withDeviceSilence = (await _context.Alerts
            .Where(a => memberIds.Contains(a.CardiMemberId)
                        && a.IsActive
                        && !a.IsResolved
                        && a.AlertType == AlertType.Inactivity
                        && a.MetricValues != null
                        && a.MetricValues.Contains("\"rule\":\"device_silence\""))
            .Select(a => a.CardiMemberId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        var withRedAlert = (await _context.Alerts
            .Where(a => memberIds.Contains(a.CardiMemberId)
                        && a.IsActive
                        && !a.IsResolved
                        && a.AcknowledgedDate == null
                        && a.Severity == AlertSeverity.Red)
            .Select(a => a.CardiMemberId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        var mutes = await _context.NotificationMutes
            .Where(m => userIds.Contains(m.UserId))
            .ToListAsync(ct);

        // A user is reachable if any of their tokens is live (not disabled), OS-authorized, has
        // the safety channel enabled, and has shown a recent liveness signal — either a real
        // delivery ack, or the foreground registration heartbeat (DeviceTokenService.RegisterAsync
        // touches LastSeenDate on every foreground). Requiring only LastAckDate would flag a
        // healthy device that simply hasn't had a real alert to ack in the last week.
        var livenessFloor = utcNow.AddDays(-7);
        var reachableUserIds = (await _context.PushDeviceTokens
            .Where(t => userIds.Contains(t.UserId)
                        && t.DisabledDate == null
                        && t.SafetyChannelEnabled
                        && (t.OsAuthorizationStatus == OsAuthorizationStatus.Granted
                            || t.OsAuthorizationStatus == OsAuthorizationStatus.Provisional)
                        && (t.LastAckDate >= livenessFloor || t.LastSeenDate >= livenessFloor))
            .Select(t => t.UserId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        var contexts = new List<NudgeContext>();

        foreach (var user in users)
        {
            var userMutes = mutes
                .Where(m => m.UserId == user.Id)
                .Select(m => new NudgeMuteSnapshot
                {
                    RuleCode = m.RuleCode,
                    Category = m.Category,
                    CardiMemberId = m.CardiMemberId,
                    MutedUntil = m.MutedUntil
                })
                .ToList();

            var userSnapshot = new NudgeUserSnapshot
            {
                Id = user.Id,
                TimeZoneId = user.TimeZoneId,
                Locale = user.Locale,
                CreatedDate = user.CreatedDate,
                HasReachablePushDevice = reachableUserIds.Contains(user.Id)
            };

            // Account-level rules get their own member-less context, so they are asked once of the
            // person rather than once per relative they watch.
            if (!memberFilter.HasValue)
            {
                contexts.Add(new NudgeContext
                {
                    UtcNow = utcNow,
                    OrganizationId = user.OrganizationId,
                    User = userSnapshot,
                    Member = null,
                    Mutes = userMutes,
                    IsOwner = true
                });
            }

            foreach (var link in links.Where(l => l.UserId == user.Id))
            {
                var member = members.FirstOrDefault(m => m.Id == link.CardiMemberId);
                if (member is null)
                    continue;

                var memberConnections = connections.Where(c => c.CardiMemberId == member.Id).ToList();

                contexts.Add(new NudgeContext
                {
                    UtcNow = utcNow,
                    OrganizationId = user.OrganizationId,
                    User = userSnapshot,
                    Mutes = userMutes,
                    IsOwner = IsOwnerOf(user.Id, member.Id, links),
                    Member = new NudgeMemberSnapshot
                    {
                        Id = member.Id,
                        CreatedDate = member.CreatedDate,
                        HasMedicalNotes = !string.IsNullOrWhiteSpace(member.MedicalNotes),
                        MonitoringPausedUntil = member.MonitoringPausedUntil,
                        EverHadConnection = memberConnections.Count > 0,
                        DaysCaptured = coverage.GetValueOrDefault(member.Id),
                        LastActivityDate = lastActivity.TryGetValue(member.Id, out var last) ? last : null,
                        HasEstablishedBaseline = withBaseline.Contains(member.Id),
                        HasUnacknowledgedRedAlert = withRedAlert.Contains(member.Id),
                        HasOpenDeviceSilenceAlert = withDeviceSilence.Contains(member.Id)
                    },
                    Connections =
                    [
                        .. memberConnections
                            .Where(c => c.IsActive)
                            .Select(c => new NudgeConnectionSnapshot
                            {
                                Id = c.Id,
                                DeviceType = c.DeviceType,
                                Status = c.ConnectionStatus,
                                LastSyncDate = c.LastSyncDate,
                                Scopes = ParseScopes(c.Scopes)
                            })
                    ]
                });
            }
        }

        return contexts;
    }

    /// <summary>
    /// Which caregiver is asked to close a member's gaps: the primary, else the longest-standing
    /// active link. One owner, so a family of five is not nagged five times about one missing
    /// emergency contact — the rest see the item read-only.
    /// </summary>
    private static bool IsOwnerOf(Guid userId, Guid memberId, List<UserCardiMember> links)
    {
        var candidates = links
            .Where(l => l.CardiMemberId == memberId)
            .OrderByDescending(l => l.IsPrimaryCaregiver)
            .ThenBy(l => l.AssignedDate)
            .ThenBy(l => l.Id)
            .ToList();

        return candidates.Count > 0 && candidates[0].UserId == userId;
    }

    private static IReadOnlyList<string> ParseScopes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            // A connection with unreadable scopes is a data problem, not a reason to stop the whole
            // morning run. Treated as "granted nothing", which at worst raises a scope nudge.
            return [];
        }
    }
}
