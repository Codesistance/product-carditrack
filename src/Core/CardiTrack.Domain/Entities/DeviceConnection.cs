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
    public int SyncFrequencyMinutes { get; set; } = 30; // Default: every 30 minutes

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

    // JSON: { "model": "Charge 6", "version": "1.0", "firmwareVersion": "2.3.1" }
    public string? Metadata { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation properties
    public ICollection<ActivityLog> ActivityLogs { get; set; } = new List<ActivityLog>();
}
