namespace CardiTrack.Infrastructure.Settings;

/// <summary>The member-chat router's operating mode. Routed is the normal path — the modes below
/// it exist as an operational rollback and a diagnostic, not as a ladder to climb.</summary>
public enum ChatRoutingMode
{
    /// <summary>The rollback lever: the router does not run and the five-boolean triage decides
    /// everything — the pre-redesign behaviour, kept so one config change can restore it.</summary>
    Off = 0,

    /// <summary>Diagnostic: the router runs on every message and its answer is logged against
    /// what the triage would have decided, but the triage still decides. Useful for measuring
    /// disagreement without changing behaviour.</summary>
    Shadow = 1,

    /// <summary>The default: the router's answer selects the workflow. The triage still runs
    /// first for its malicious verdict, which stays a standalone pre-check on every path, and a
    /// router failure descends to the triage-decided path rather than failing the send.</summary>
    On = 2,
}

/// <summary>Bound from the <c>ChatRouting</c> configuration section. A missing section means
/// <see cref="ChatRoutingMode.On"/> — routing is the new normal (decision 2026-08-24), and the
/// dial remains only as the rollback and diagnostic lever.</summary>
public class ChatRoutingSettings
{
    public const string SectionName = "ChatRouting";

    public ChatRoutingMode Mode { get; set; } = ChatRoutingMode.On;
}
