namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// How caregiver invitations are addressed and how long they live.
/// </summary>
public class CaregiverInviteOptions
{
    public const string SectionName = "CaregiverInvites";

    /// <summary>
    /// The API's own public origin, e.g. <c>https://api.dev.carditrack.com</c>. Invitation URLs are
    /// built from this.
    /// </summary>
    /// <remarks>
    /// Load-bearing for the same reason <see cref="DeviceInviteOptions.PublicBaseUrl"/> is: when it
    /// is empty the service falls back to the origin of the request that asked for the invitation,
    /// which is right for a developer on localhost and wrong anywhere a Host header arrives from
    /// outside. An attacker who could set it would have CardiTrack mint an invitation pointing at
    /// their own host, and the admin would forward it in good faith.
    /// </remarks>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether an unconfigured <see cref="PublicBaseUrl"/> may fall back to the origin of the
    /// request that asked for the invitation. <strong>False unless a config file says otherwise</strong>,
    /// which means every deployed environment refuses rather than guesses.
    /// </summary>
    /// <remarks>
    /// The fallback is a developer convenience for localhost and nothing else — the request origin
    /// comes from a client-supplied Host header. Named rather than inferred from the environment
    /// name so that turning it on is a visible line in a settings file somebody has to write, not
    /// a property of which machine the code happens to be running on.
    /// </remarks>
    public bool AllowRequestOriginFallback { get; set; }

    /// <summary>
    /// How long an invitation lasts, in days. Seven, matching the contract in
    /// <c>docs/execution/backend/api/family.md</c>: long enough that a sibling who is away for the
    /// week still finds it live, short enough that a forwarded message does not stay useful for
    /// ever.
    /// </summary>
    public int LifetimeDays { get; set; } = 7;
}
