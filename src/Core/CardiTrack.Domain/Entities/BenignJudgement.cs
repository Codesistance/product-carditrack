using CardiTrack.Domain.Common;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// A record that the medical model looked at one rule's finding for one member on one of their
/// local days and judged it not worth the family's attention. It exists so the next pass does not
/// ask the same question about the same finished day.
/// </summary>
/// <remarks>
/// <para>
/// A row of its own rather than a green <see cref="Alert"/>, which is the reason this was left as
/// a follow-up when the statistical pass was written: an alert row is a thing a caregiver can
/// open, and a benign one would re-surface the retired benign-sleep card on every screen that
/// reads alerts. Nothing reads this table but the pass that writes it.
/// </para>
/// <para>
/// <b>Only rules over a finished period are recorded</b>
/// (<c>StatisticalAlertRules.RulesOverFinishedPeriods</c>). A rule reading yesterday, or the night
/// that has ended, is reading data that cannot change again, so re-judging it costs an inference
/// and can only reach the same answer. A rule reading today — no morning activity yet, a rhythm
/// notification the watch may still post this afternoon — is reading data that is still arriving,
/// and there the existing behaviour is the correct one: judge it again, because the readings have
/// moved. The two were indistinguishable while nothing was persisted, which is what made
/// persisting look like a straight trade of freshness for cost.
/// </para>
/// <para>
/// Not health data on its own — a rule id and a date — but it is keyed to a member, so it is
/// erased with them like every other member-scoped row.
/// </para>
/// </remarks>
public class BenignJudgement : BaseEntity
{
    public Guid CardiMemberId { get; set; }

    /// <summary>The rule id, as <c>StatisticalAlertRules</c> names it.</summary>
    public string Rule { get; set; } = string.Empty;

    /// <summary>
    /// The day this judgement covers, in the member's own local calendar: the night a night-scoped
    /// finding named, and otherwise the local day the pass ran on. The same key the same-local-day
    /// dedup uses for an alert, so the two agree about which day a finding belongs to.
    /// </summary>
    public DateOnly LocalDate { get; set; }

    /// <summary>When the judgement was made, for retention sweeps and for reading the table back.</summary>
    public DateTime JudgedAtUtc { get; set; }
}
