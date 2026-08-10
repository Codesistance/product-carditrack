using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Interfaces;

namespace CardiTrack.Domain.Entities;

public class DeviceConnection : BaseEntity, ISoftDeletable
{
    public Guid CardiMemberId { get; set; }
    public DeviceType DeviceType { get; set; }
    public string DeviceName { get; set; } = string.Empty; // e.g., "Mom's Fitbit"
    public bool IsPrimary { get; set; } // Primary device for data sync
    public ConnectionStatus ConnectionStatus { get; set; }

    // OAuth Tokens (encrypted in database)
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? TokenExpiry { get; set; }

    // JSON: ["activity", "heartrate", "sleep", "profile"]
    public string Scopes { get; set; } = "[]";

    public DateTime? ConnectedDate { get; set; }
    public DateTime? LastSyncDate { get; set; }
    /// <summary>
    /// How often this connection is pulled. Ten minutes rather than thirty: the wearer's own
    /// watch-to-cloud sync is the dominant delay a caregiver feels, and polling three times as
    /// often is what makes the dashboard catch each upload soon after it lands. Affordable
    /// because a pull now costs one day's snapshot rather than the whole trailing window — see
    /// <c>DeviceSyncService.PullWindowAsync</c>.
    /// </summary>
    public int SyncFrequencyMinutes { get; set; } = 10;

    /// <summary>
    /// When this connection should next be pulled. Null means due-ness falls back to
    /// <see cref="LastSyncDate"/> plus <see cref="SyncFrequencyMinutes"/>, which is how every
    /// connection behaves until cadence calibration starts writing a schedule here.
    /// </summary>
    public DateTime? NextPullAt { get; set; }

    /// <summary>
    /// Pulls in a row that returned nothing new. Drives dormancy backoff: past the device type's
    /// configured threshold the interval widens, and any pull that finds data resets it to zero.
    /// A device that is simply not being worn should not be polled at the same rate as one in use.
    /// </summary>
    public int ConsecutiveEmptyPulls { get; set; }

    /// <summary>
    /// The provider's public health-user id — the `users/{user}` segment webhook notifications
    /// and subscriptions are addressed by. Captured opportunistically on the first sync after
    /// this column shipped (see <c>DeviceSyncService</c>), so existing connections self-heal;
    /// null until then, or when the provider exposes none. This is what lets a notification be
    /// mapped back to a connection without trusting anything else in its payload.
    /// </summary>
    public string? HealthUserId { get; set; }

    /// <summary>
    /// Earliest day the history backfill has fetched (or confirmed empty) for this connection;
    /// null until the first backfill chunk runs. The backfill walks this marker backwards from
    /// the routine sync window towards the provider's configured horizon, a chunk per pull, so
    /// a freshly connected wearable's existing history reaches the baseline within hours instead
    /// of the baseline waiting a month for new days — see <c>DeviceSyncService</c>.
    /// </summary>
    public DateOnly? HistoryBackfilledTo { get; set; }

    // JSON: { "model": "Charge 6", "version": "1.0", "firmwareVersion": "2.3.1" }
    public string? Metadata { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation properties
    public ICollection<ActivityLog> ActivityLogs { get; set; } = new List<ActivityLog>();
}
