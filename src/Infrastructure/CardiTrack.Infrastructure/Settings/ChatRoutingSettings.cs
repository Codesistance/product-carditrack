namespace CardiTrack.Infrastructure.Settings;

/// <summary>How far the member-chat router is trusted — the rollout dial for
/// <c>docs/technical/member_chat_routing.md</c> §10's steps 6 and 7.</summary>
public enum ChatRoutingMode
{
    /// <summary>The router does not run. Today's five-boolean triage decides everything —
    /// the pre-cutover behaviour, and the default.</summary>
    Off = 0,

    /// <summary>
    /// The router runs on every message and its answer is logged against what the triage decided,
    /// but nothing ships on it. The disagreement rate this produces is what decides the cutover —
    /// step 6's whole purpose.
    /// </summary>
    Shadow = 1,

    /// <summary>The router's answer selects the workflow. The triage still runs first for its
    /// malicious verdict, which stays a standalone pre-check on every path.</summary>
    On = 2,
}

/// <summary>Bound from the <c>ChatRouting</c> configuration section. A missing section means
/// <see cref="ChatRoutingMode.Off"/> — the cutover must be a deliberate act, never a default.</summary>
public class ChatRoutingSettings
{
    public const string SectionName = "ChatRouting";

    public ChatRoutingMode Mode { get; set; } = ChatRoutingMode.Off;
}
