using CardiTrack.Domain.Entities;

namespace CardiTrack.Infrastructure.Services.PromptContext;

/// <summary>
/// One thing the model is told about a member, in the prompts where knowing it changes the answer.
/// </summary>
/// <remarks>
/// <para>
/// The point of the abstraction is that adding a context source is one class and one registration:
/// what a new source knows then appears in every prompt it declares itself relevant to, rather than
/// in whichever of the three prompt services someone remembered to edit. Before this existed, age
/// and caregiver notes reached all three prompts and the environmental readings reached exactly one
/// — not by decision, but because that was the service the enrichment landed in.
/// </para>
/// <para>
/// A source fetches its own data. That is what keeps the promise above honest: a source that
/// depended on its caller to hand it data would need every caller changed to add it, which is the
/// thing being removed.
/// </para>
/// </remarks>
public interface IMemberContextSource
{
    /// <summary>The prompts this source contributes to. See <see cref="PromptPurpose"/>.</summary>
    PromptPurpose Purposes { get; }

    /// <summary>
    /// Where this section sits relative to the others. Fixed numbers rather than registration order
    /// because DI enumeration order is not a contract, and a prompt whose sections shuffle between
    /// hosts is a prompt whose cached prefix stops matching.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// The section, or null when this source has nothing to say about this member right now.
    /// </summary>
    /// <remarks>
    /// Null is the normal answer, not an error: a member with no unresolved alerts, no recent
    /// exercise session and no answered questions should produce a prompt with none of those
    /// sections in it. Silence is how a section stays meaningful when it does appear.
    /// </remarks>
    Task<MemberContextSection?> BuildAsync(MemberContextRequest request, CancellationToken ct);
}

/// <summary>What a source is being asked about.</summary>
/// <param name="Member">
/// The member, already loaded by the caller — every prompt service has it in hand, and re-fetching
/// per source would mean five lookups of the same row. Null when the caller has no profile, which
/// sources must tolerate rather than assume away.
/// </param>
/// <param name="CardiMemberId">
/// The id, separately, because it is what sources query by — and it is known even when
/// <paramref name="Member"/> is null.
/// </param>
/// <param name="Today">The member's own local day, for age and day-labelling.</param>
/// <param name="UtcNow">The caller's clock, so staleness rules and tests share one notion of now.</param>
/// <param name="Purpose">Which prompt is being built.</param>
public sealed record MemberContextRequest(
    CardiMember? Member,
    Guid CardiMemberId,
    DateOnly Today,
    DateTime UtcNow,
    PromptPurpose Purpose);

/// <summary>
/// A rendered section. The composer owns the delimiter and the safety rules, so a source returns
/// only what it wants to say and cannot accidentally define its own structure.
/// </summary>
/// <param name="Label">The section heading, without dashes.</param>
/// <param name="Body">The lines beneath it.</param>
public sealed record MemberContextSection(string Label, string Body);
