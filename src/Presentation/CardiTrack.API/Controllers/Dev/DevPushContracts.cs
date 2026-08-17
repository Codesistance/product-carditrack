using System.Text.Json.Serialization;
using CardiTrack.Domain.Enums;

namespace CardiTrack.API.Controllers.Dev;

/// <summary>
/// Wire shapes for the dev-only test-push endpoint. Deliberately local to the API project rather
/// than in <c>CardiTrack.Application.DTOs</c>: nothing outside this controller consumes them, and
/// Core should not carry shapes that exist only for a developer's machine.
/// </summary>
/// <remarks>
/// The enum members carry <see cref="JsonStringEnumConverter"/> individually rather than the API
/// registering it globally. Global registration would flip every enum in every response from a
/// number to a string, and the mobile client deserializes those — a breaking change to the whole
/// API surface, to fix the ergonomics of one developer endpoint. Per-property keeps the blast
/// radius at this file. Numbers still bind, so callers written against the old shape keep working.
/// </remarks>
public sealed record DevPushRequest
{
    /// <summary>Who to push to. Bound into the token's MAC, so a token cannot be re-pointed.</summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// Note <see cref="DeliveryCategory.Nudge"/> never produces a push — <c>DeliveryPlanner</c>
    /// routes it to in-app — so a Nudge request comes back with an explanatory hint rather than a
    /// notification.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeliveryCategory Category { get; init; } = DeliveryCategory.Safety;

    /// <summary>Required for <see cref="DeliveryCategory.Health"/>; only Red and Orange push.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
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
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeliveryChannel Channel { get; init; }

    /// <summary>Non-null means quiet hours deferred it — nothing has been sent yet.</summary>
    public DateTime? ScheduledFor { get; init; }

    /// <summary>Named rather than numbered: this response is read by a human diagnosing a push, and "Sent" carries more than "2".</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
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

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DevicePlatform Platform { get; init; } = DevicePlatform.Android;

    public string AppVersion { get; init; } = string.Empty;

    /// <summary>
    /// First 6 hex characters of the token's SHA-256 fingerprint — enough to tell two devices
    /// apart in a log, and never the token itself, which is Tier 1 data.
    /// </summary>
    public string Fingerprint { get; init; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OsAuthorizationStatus OsAuthorizationStatus { get; init; }

    public bool SafetyChannelEnabled { get; init; }
    public DateTime? LastAckDate { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeliveryState State { get; init; }
    public string? ProviderMessageId { get; init; }
    public string? LastError { get; init; }
}
