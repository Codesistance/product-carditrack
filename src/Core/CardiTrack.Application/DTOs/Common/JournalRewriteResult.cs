using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.DTOs.Common;

/// <summary>
/// How a caregiver-requested rewrite of one CardiJournal book ended. The reasons the scheduled
/// pass logs and moves past are, here, things a person asked about and is waiting to hear.
/// </summary>
public enum JournalRewriteOutcome
{
    /// <summary>The new book is stored; any earlier book for the period is gone.</summary>
    Written = 1,

    /// <summary>The member is inactive or their monitoring is paused — the same members the
    /// scheduled pass writes nothing for.</summary>
    MemberUnavailable = 2,

    /// <summary>The period has not ended in the member's own local time, so there is nothing
    /// finished to account for.</summary>
    PeriodNotFinished = 3,

    /// <summary>The period carried no readings (a Daybook), or too few days of them (a Weekbook or
    /// Monthbook, whose minimum <see cref="JournalRewriteResult.DaysNeeded"/> names).</summary>
    NoReadings = 4,

    /// <summary>The model replied, and the reply failed one of the register guards every book
    /// is held to. Nothing was written and nothing was removed.</summary>
    Discarded = 5,
}

/// <summary>
/// The result of <c>IDigestGenerationService.RewriteBookAsync</c>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Entry">The stored book when <paramref name="Outcome"/> is <see cref="JournalRewriteOutcome.Written"/>; otherwise null.</param>
/// <param name="Usage">The model call the attempt made, when one was made — for the caller to bill.</param>
/// <param name="ReplacedAnEarlierBook">True when a book for the period existed and was removed for this one.</param>
/// <param name="DaysWithData">For <see cref="JournalRewriteOutcome.NoReadings"/> on a Weekbook or Monthbook: how many days carried readings.</param>
/// <param name="DaysNeeded">For the same case: how many the book needs.</param>
public sealed record JournalRewriteResult(
    JournalRewriteOutcome Outcome,
    DigestEntry? Entry,
    AiUsage? Usage,
    bool ReplacedAnEarlierBook,
    int DaysWithData = 0,
    int DaysNeeded = 0);
