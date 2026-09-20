namespace CardiTrack.Application.Services;

/// <summary>
/// How long a stored <see cref="Domain.Entities.MemberInsight"/> is kept before the Worker's
/// retention sweep removes it.
/// </summary>
/// <remarks>
/// <para>
/// Ninety days, matching <c>RealtimeAssessments</c> rather than the seven months the CardiJournal
/// books carry, and the reasoning is the books' own: a book is sold as a retained account of the
/// member's condition, and an insight is a re-derivable interpretation of readings that are
/// themselves kept far longer (hourly rollups 13 months, raw daily activity 25). Nothing is lost
/// by regenerating one; something is given up by keeping model-written prose about a named person
/// for longer than the question it answered was live.
/// </para>
/// <para>
/// Enforced by a sweep in <c>CardiTrack.Worker</c> rather than by a partition drop: this table is
/// ordinary EF-tracked, like <c>MemberChatSessions</c>, so the mechanism the partitioned AI stores
/// use does not apply. See <c>docs/compliance/dpia.md</c> §6.3 and
/// <c>docs/technical/data_protection_architecture.md</c> §5.1.
/// </para>
/// </remarks>
public static class InsightRetention
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    /// <summary>
    /// The most rows one sweep will remove. The same bounded-batch shape the other retention
    /// passes use: a sweep that has fallen behind catches up over several ticks rather than
    /// loading an unbounded set into memory in one.
    /// </summary>
    public const int SweepBatchSize = 500;
}
