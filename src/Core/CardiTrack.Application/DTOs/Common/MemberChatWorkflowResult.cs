using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Common;

/// <summary>One model call a chat turn made, ready to become a
/// <see cref="CardiTrack.Domain.Entities.MemberChatTurnUsage"/> row.</summary>
/// <remarks>
/// A struct rather than a tuple because these travel in a list across a workflow boundary now, and
/// <c>(AiCallStep, AiProviderSlot, AiUsage)</c> read positionally at the call site is the shape
/// that lets a slot and a step be transposed without the compiler noticing.
/// </remarks>
public readonly record struct AiCallRecord(AiCallStep Step, AiProviderSlot Slot, AiUsage Usage);

/// <summary>
/// What one member-chat workflow produced. The uniform output half of the contract every workflow
/// shares — see <c>docs/technical/member_chat_routing.md</c> §7.
/// </summary>
/// <remarks>
/// <para>
/// The point of the shared shape is that persistence, billing and the response envelope stop being
/// each branch's problem. Before this, all four branches of <c>MemberChatService</c> called
/// <c>PersistTurnsAsync</c>, <c>PersistUsageAsync</c> and <c>SaveChangesAsync</c> themselves and
/// built their own <see cref="Responses.MemberChatMessageResponse"/> — four copies of a sequence
/// where one branch forgetting a step is a bug nothing catches, since every branch looks locally
/// correct.
/// </para>
/// <para>
/// Deliberately carries no session id and no <c>GeneratedAt</c>: a workflow answers a question, it
/// does not own the turn it belongs to. The caller stamps both.
/// </para>
/// </remarks>
public sealed record MemberChatWorkflowResult
{
    /// <summary>Which way of answering produced this — stamped onto the assistant turn so the
    /// distribution of real caregiver questions is a query rather than a guess.</summary>
    public required MemberChatWorkflow Workflow { get; init; }

    /// <summary>The caregiver-facing answer, already capped and with the member's real name
    /// resolved back in — a workflow never returns a placeholder.</summary>
    public required string Reply { get; init; }

    /// <summary>Supporting series, empty when the answer has nothing to chart.</summary>
    public IReadOnlyList<ChartSeries> Charts { get; init; } = [];

    /// <summary>
    /// Every model call this turn actually made, in the order made — including the triage that
    /// routed it here.
    /// </summary>
    /// <remarks>
    /// The list is the workflow's own account of what it spent, not a fixed four: the steer path
    /// makes two calls, the code-assembled paths one, the full pipeline four. A turn's cost is the
    /// sum of the steps that produced it, and a usage row that skipped the routing call would make
    /// the cheap paths look free.
    /// </remarks>
    public required IReadOnlyList<AiCallRecord> Calls { get; init; }
}
