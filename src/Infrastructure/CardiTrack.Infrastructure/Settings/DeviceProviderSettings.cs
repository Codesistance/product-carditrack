namespace CardiTrack.Infrastructure.Settings;

public class DeviceProviderSettings
{
    public const string SectionName = "DeviceProviders";

    /// <summary>
    /// Matches a <see cref="Domain.Enums.HealthApi"/> enum name (e.g. "GoogleHealth",
    /// "SamsungHealth") — the data-source API this block configures, not a hardware brand.
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// The <see cref="Domain.Enums.DeviceType"/> enum names this API serves (e.g.
    /// ["Fitbit", "GooglePixelWatch"] for GoogleHealth). This list is the whole brand→API
    /// mapping: a brand added here connects and syncs through this block's engine with no code
    /// change. A DeviceType may appear in at most one provider block.
    /// </summary>
    public List<string> DeviceTypes { get; set; } = [];
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AuthorizationUrl { get; set; } = string.Empty;
    public string TokenUrl { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = [];

    /// <summary>
    /// Provider-facing redirect URI. When set (e.g. Google requires an https redirect for web
    /// clients), it is used in the authorize URL and code exchange instead of the app deep link;
    /// the API's oauth/redirect endpoint then bounces the browser back to the deep link.
    /// </summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// Extra authorize-URL query parameters sent on every authorization, e.g. Google's
    /// access_type=offline (without which no refresh token is issued).
    /// </summary>
    public Dictionary<string, string> AdditionalAuthorizationParams { get; set; } = [];

    /// <summary>
    /// Authorize-URL parameters added only while we hold no refresh token for the member on this
    /// provider — Google's prompt=consent belongs here. Google re-issues a refresh token only when
    /// consent is shown again, but sending it unconditionally makes the user re-approve a device
    /// they have already approved on every reconnect.
    /// </summary>
    public Dictionary<string, string> FirstConsentAuthorizationParams { get; set; } = [];

    /// <summary>Access token lifetime in hours. Used to compute TokenExpiry on storage.</summary>
    public int TokenLifetimeHours { get; set; } = 8;

    /// <summary>
    /// How many <em>complete</em> days back each sync re-fetches. The window itself ends at today,
    /// so a pull covers this many days plus the day in progress.
    /// </summary>
    /// <remarks>
    /// Providers finalise a day's data some hours after midnight, and a sync that only ever
    /// fetched the newest day would leave a permanent hole for any day the worker was down.
    /// Re-fetching a short trailing window makes the job self-healing; the upsert keeps it
    /// idempotent.
    /// <para>
    /// Note the cost: one day's snapshot is <b>13 requests</b> (6 activity roll-ups, 2 heart rate,
    /// 1 sleep, 4 additional metrics), so each day here is another 13 against a ceiling of 300
    /// requests per minute <em>per wearer</em>. That is why these days are re-fetched once a UTC
    /// day rather than on every pull — see <c>DeviceSyncService.SyncCardiMemberAsync</c>. Nothing
    /// enforces the ceiling at runtime: the interval bounds and dormancy settings below are
    /// validated at startup but never consulted by the sync pipeline, and
    /// <see cref="MaxRequestsPerSecond"/> is not read at all.
    /// </para>
    /// </remarks>
    public int SyncLookbackDays { get; set; } = 3;

    /// <summary>
    /// How far back a connection's history is backfilled, in days from today. The backfill walks
    /// backwards from the routine window to this horizon, one
    /// <see cref="BackfillChunkDays"/>-sized chunk per pull, so it never competes with the pull a
    /// caregiver is actually waiting on. 0 disables backfill. Defaults to 90 — the depth most
    /// Google Health API daily data types serve, and enough to fill the longest baseline window.
    /// </summary>
    public int BackfillDays { get; set; } = 90;

    /// <summary>
    /// Days of history fetched per pull while a connection still has backfilling to do. Sized
    /// against the per-wearer request ceiling: a day's snapshot is 13 requests, so 7 days is ~91
    /// requests on top of the routine pull — comfortably inside 300/min while still finishing a
    /// 90-day horizon in about two hours at a 10-minute cadence.
    /// </summary>
    public int BackfillChunkDays { get; set; } = 7;

    /// <summary>
    /// Window used by the periodic audit pull over a sample of connections. Wider than
    /// <see cref="SyncLookbackDays"/> on purpose: a routine sync can only ever observe revisions
    /// inside its own window, so a provider that amends day 5 is invisible until something looks
    /// further back. Defaults to 14 days, the widest range the Google Health API accepts for
    /// heart-rate, active-zone-minutes and calorie roll-ups.
    /// </summary>
    public int AuditLookbackDays { get; set; } = 14;

    // ── Pull cadence bounds ───────────────────────────────────────────────────────────────────
    // Set per device type from tfvars (DeviceProviders__<i>__* env vars). Providers differ in how
    // fast they finalise a day and how hard they rate-limit, so these belong to the provider
    // rather than to any one connection. Calibration may move a connection's interval only
    // *within* this range — widening it is a deploy, deliberately, because these bounds are the
    // only guard between a miscomputed cadence and either a rate-limit ban or stale health data.

    /// <summary>
    /// Floor on a connection's pull interval — derived from the provider's rate limit and the
    /// device population it has to sustain. Never poll a single connection more often than this.
    /// </summary>
    public int MinPullIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Ceiling on a connection's pull interval, so dormancy backoff cannot park a connection
    /// indefinitely. A device that starts reporting again must still be picked up within a day.
    /// </summary>
    public int MaxPullIntervalMinutes { get; set; } = 1440;

    /// <summary>
    /// Provider-wide request ceiling shared across all connections, used to size the dispatch rate
    /// of the pull queue. 0 leaves it unset — no app-side governor.
    /// </summary>
    public double MaxRequestsPerSecond { get; set; }

    /// <summary>
    /// Consecutive pulls that return nothing new before backoff starts. 0 disables backoff, which
    /// is the behaviour before cadence calibration is switched on.
    /// </summary>
    public int DormancyThresholdPulls { get; set; }

    /// <summary>
    /// Multiplier applied to the interval on each empty pull past the threshold, clamped at
    /// <see cref="MaxPullIntervalMinutes"/>. A single non-empty pull resets the interval to the floor.
    /// </summary>
    public double DormancyBackoffFactor { get; set; } = 2.0;
}
