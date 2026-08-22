using System.Diagnostics.CodeAnalysis;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// Whether a persisted <see cref="MemberAdvise"/> row may be shown, in one predicate for the three
/// readers that ask: <c>HealthInsightService.GetAdviseAsync</c> (the CardiMember Details card),
/// <c>DashboardService</c> (the hero card's pulse indicator) and <c>MemberChatService.AdviseReply</c>
/// (an advice question in chat).
/// </summary>
/// <remarks>
/// <para>
/// The three used to answer differently. Staleness was already shared — <see cref="AdviseStaleness"/>
/// exists because two of them had drifted apart once before, flagged in PR #456's review — but the
/// field checks were not: chat withheld a row with no <see cref="MemberAdvise.GuidelineCited"/>
/// while the card rendered it on a nonblank <see cref="MemberAdvise.Suggestion"/> alone and the
/// pulse dot lit on age alone. The property is nullable, so that row is representable: a caregiver
/// could see a pulsing dot, open the card, read a suggestion, ask chat about it and be told there
/// is no current suggestion.
/// </para>
/// <para>
/// Aligned on chat's stricter rule rather than the card's looser one, because it is the rule
/// <see cref="DTOs.Responses.AdviseResponse.GuidelineCited"/> already documents: clients should
/// treat a null the same as an empty suggestion, and not render a suggestion without its grounding.
/// The card was not honouring its own contract.
/// </para>
/// <para>
/// A predicate over the row rather than a method on it: <c>MemberAdvise</c> is a persistence shape,
/// and when a row may be shown is a decision the readers share, not a property of the record.
/// </para>
/// </remarks>
public static class AdviseServability
{
    /// <summary>
    /// True when <paramref name="advise"/> exists, is inside <see cref="AdviseStaleness.MaxAge"/>,
    /// and carries all three of the fields a suggestion is only meaningful with. Callers still
    /// apply their own paused/deactivated-member guard first — that is about the member, not the
    /// row.
    /// </summary>
    /// <remarks>
    /// <see cref="NotNullWhenAttribute"/> so a caller that has checked this can go on to read the
    /// row without a null-forgiving operator — the fields are only worth reading when it said yes,
    /// and the compiler should be the one enforcing that rather than the caller asserting it.
    /// </remarks>
    public static bool IsServable([NotNullWhen(true)] MemberAdvise? advise, DateTime utcNow) =>
        advise is not null
        && utcNow - advise.GeneratedAtUtc <= AdviseStaleness.MaxAge
        && !string.IsNullOrWhiteSpace(advise.Summary)
        && !string.IsNullOrWhiteSpace(advise.Suggestion)
        && !string.IsNullOrWhiteSpace(advise.GuidelineCited);
}
