using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Responses;

/// <summary>Never carries the token itself — it is Tier 1 data (§7.2 C2) and the client already knows its own token.</summary>
public class PushDeviceTokenResponse
{
    public Guid Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public DevicePlatform Platform { get; set; }
    public OsAuthorizationStatus OsAuthorizationStatus { get; set; }
    public bool SafetyChannelEnabled { get; set; }
    public DateTime LastSeenDate { get; set; }
}
