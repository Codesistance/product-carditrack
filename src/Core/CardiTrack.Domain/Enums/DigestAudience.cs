namespace CardiTrack.Domain.Enums;

/// <summary>
/// Who a daily digest is written for, and in what reading-mode. The framing changes with the
/// reader — plain reassuring language for family, first-person encouragement for the wearer
/// (docs/llm_design.md prompt variants) — so each value is its own generated text, never a
/// re-badged copy.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DayReview"/> widened this from "who" to "who, reading how". It goes to the same
/// family as <see cref="Family"/>, but a family reading back a finished day is not the same
/// reader as one glancing at a day still in progress: the first will sit with a fuller account
/// and precise terms, the second wants to know whether anything needs them right now. That
/// difference is the same one this enum already existed to express — a different register needs
/// its own generation — so it is modelled the same way rather than as a second dimension.
/// </para>
/// <para>
/// The value is persisted as its name (<c>DigestEntryConfiguration</c> maps it through
/// <c>HasConversion&lt;string&gt;()</c>) and is part of the composite key, so adding one needs no
/// migration and gives a member a parallel series per day at no schema cost.
/// </para>
/// <para>
/// <b>Revisit when</b> a value needs to vary both axes at once — a wearer-facing day review, say.
/// At that point "audience" and "kind" have genuinely come apart and belong in two columns, which
/// is a key change and a partitioned-table migration. Until then a third column would be two
/// values wide and empty.
/// </para>
/// </remarks>
public enum DigestAudience
{
    /// <summary>Family caregivers reading a day as it happens — the app's primary readers.</summary>
    Family = 1,

    /// <summary>The wearer themselves — generated only once wearer logins exist.</summary>
    Wearer = 2,

    /// <summary>
    /// The family's account of one <em>finished</em> day: every reading the day produced, read
    /// against the member's own usual and against the published bands, in a register that may
    /// name a measurement precisely so long as it explains itself. Written once, after the
    /// member's local day is over — never recomputed as data lands, because the day it describes
    /// cannot change any more.
    /// </summary>
    DayReview = 3,
}
