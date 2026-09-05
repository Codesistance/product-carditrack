using System.Diagnostics.CodeAnalysis;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// Whether a persisted <see cref="MemberStatusLine"/> may be shown, in one predicate for the two
/// readers that ask: <c>HealthInsightService.GetCurrentStatusMessageAsync</c> (the dashboard hero
/// card) and <c>MemberChatService</c>'s status rung.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="AdviseServability"/>, and written for the same reason: the moment a
/// second reader exists, "may this row be shown" is a decision they share rather than a rule one of
/// them happens to hold. Chat asking "how is he today" and the header two centimetres above it must
/// not answer from different rules about the same row.
/// </para>
/// <para>
/// Checks the message as well as the age, unlike the reader it was lifted from.
/// <see cref="StatusLineGenerationService"/> keeps the previous row rather than storing a blank
/// message, so a blank one should not exist — but the property is non-null with an empty default,
/// which makes the row representable, and a serving rule that assumes it away is the kind of
/// assumption <see cref="AdviseServability"/> was written after one of these surfaces got wrong.
/// The headline is deliberately not checked: it is documented as droppable, and the dashboard has
/// per-tier copy to fall back on.
/// </para>
/// <para>
/// Callers still apply their own paused/deactivated-member guard first — that is about the member,
/// not the row.
/// </para>
/// </remarks>
public static class StatusLineServability
{
    /// <summary>
    /// True when <paramref name="line"/> exists, is inside <see cref="StatusLineStaleness.MaxAge"/>,
    /// and actually carries a sentence to say.
    /// </summary>
    /// <remarks>
    /// <see cref="NotNullWhenAttribute"/> so a caller that has checked this can read the row
    /// without a null-forgiving operator, for the reason <see cref="AdviseServability.IsServable"/>
    /// gives: the compiler should enforce that, not the caller asserting it.
    /// </remarks>
    public static bool IsServable([NotNullWhen(true)] MemberStatusLine? line, DateTime utcNow) =>
        line is not null
        && utcNow - line.GeneratedAtUtc <= StatusLineStaleness.MaxAge
        && !string.IsNullOrWhiteSpace(line.Message);
}
