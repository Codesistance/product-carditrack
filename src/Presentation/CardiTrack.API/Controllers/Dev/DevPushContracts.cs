using CardiTrack.Domain.Enums;

namespace CardiTrack.API.Controllers.Dev;

/// <summary>
/// Wire shapes for the dev-only test-push endpoint. Deliberately local to the API project rather
/// than in <c>CardiTrack.Application.DTOs</c>: nothing outside this controller consumes them, and
/// Core should not carry shapes that exist only for a developer's machine.
/// </summary>
public sealed record DevPushRequest
{
    /// <summary>Who to push to. Bound into the token's MAC, so a token cannot be re-pointed.</summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// Note <see cref="DeliveryCategory.Nudge"/> never produces a push — <c>DeliveryPlanner</c>
    /// routes it to in-app — so a Nudge request comes back with an explanatory hint rather than a
    /// notification.
    /// </summary>
    public DeliveryCategory Category { get; init; } = DeliveryCategory.Safety;

    /// <summary>Required for <see cref="DeliveryCategory.Health"/>; only Red and Orange push.</summary>
    public AlertSeverity? Severity { get; init; }
}

/// <summary>
/// Everything needed to tell "the push never left the server" apart from "the push left and the
/// device did nothing with it" — the distinction the endpoint exists to make.
/// </summary>
public sealed record DevPushResponse
{
    public Guid DeliveryId { get; init; }

    /// <summary><c>InApp</c> means <c>DeliveryPlanner</c> declined to push at all; see <see cref="Hint"/>.</summary>
    public DeliveryChannel Channel { get; init; }

    /// <summary>Non-null means quiet hours deferred it — nothing has been sent yet.</summary>
    public DateTime? ScheduledFor { get; init; }

    public DeliveryState State { get; init; }
    public DateTime ExpiresAt { get; init; }

    /// <summary>The channel id stamped on the FCM payload — what the device's channel must match.</summary>
    public string AndroidChannelId { get; init; } = string.Empty;

    /// <summary>Android <c>res/raw</c> name. The OS only honours it on the channel's first creation.</summary>
    public string AndroidSound { get; init; } = string.Empty;

    /// <summary>APNs <c>aps.sound</c> — a main-bundle filename.</summary>
    public string IosSound { get; init; } = string.Empty;

    public IReadOnlyList<DevPushDeviceResult> Devices { get; init; } = [];

    /// <summary>Plain-language reason nothing will arrive, when that is the case. Null on a clean send.</summary>
    public string? Hint { get; init; }
}

/// <summary>One device the delivery fanned out to, and what the provider said about it.</summary>
public sealed record DevPushDeviceResult
{
    public string DeviceId { get; init; } = string.Empty;
    public DevicePlatform Platform { get; init; } = DevicePlatform.Android;
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>
    /// First 6 hex characters of the token's SHA-256 fingerprint — enough to tell two devices
    /// apart in a log, and never the token itself, which is Tier 1 data.
    /// </summary>
    public string Fingerprint { get; init; } = string.Empty;

    public OsAuthorizationStatus OsAuthorizationStatus { get; init; }
    public bool SafetyChannelEnabled { get; init; }
    public DateTime? LastAckDate { get; init; }

    public DeliveryState State { get; init; }
    public string? ProviderMessageId { get; init; }
    public string? LastError { get; init; }
}
