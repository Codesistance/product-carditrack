namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// Limits on a member-chat send as a whole, rather than on any one call inside it.
/// </summary>
public class MemberChatOptions
{
    public const string SectionName = "MemberChat";

    /// <summary>
    /// How long one send may run on the server, end to end, before it is abandoned and answered
    /// with a 503. The one budget every outer layer is derived from: Terraform sets the API's
    /// Cloud Run request timeout a minute past it, and the app's send timeout
    /// (<c>CardiTrackApiClient.MemberChatSendTimeout</c>) a minute past that.
    /// </summary>
    /// <remarks>
    /// A send is a chain — pre-check, route, plan, one or two clinical reads, one or two rewrites —
    /// and each call has its own timeout and retries, so no sum of per-call ceilings is a real
    /// worst case, and sized as one it would be longer than anyone waits. This caps the chain
    /// instead: long enough for a clinical read that queued behind another caller on the
    /// single-request MedGemma service (AI:Private:TimeoutSeconds) plus the Vertex calls either
    /// side of it, and anything slower gives up here, where it can still say so, rather than at
    /// Cloud Run's edge as a bare 504.
    /// </remarks>
    public int SendBudgetSeconds { get; set; } = 1020;
}
