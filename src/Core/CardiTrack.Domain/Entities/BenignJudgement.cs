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
/// <b>Keyed on the finding's own figures, not on its day.</b> The first shape of this table
/// remembered a rule for a member's local day, on the reasoning that a rule reading yesterday was
/// reading data that could not change again. That reasoning is wrong:
/// <c>DeviceSyncService</c>'s repair pass re-pulls <c>SyncLookbackDays</c> of complete days and
/// re-merges them, and a night's readings routinely land after local midnight — which is why
/// <c>ANightThatSyncedLate_IsStillJudged_FromYesterdaysLog</c> exists. A day-keyed row would have
/// silently swallowed the re-judgement of a finding whose readings had since doubled.
/// </para>
/// <para>
/// Fingerprinting the finding's <c>MetricValues</c> makes the key say what it actually means: this
/// model judged <em>these figures</em> not worth the family's attention. Readings that move change
/// the fingerprint and the finding is judged again on the next pass, exactly as it was before this
/// table existed; readings that do not move cost one inference instead of 288. It also removes the
/// need to sort rules into ones whose data is settled and ones whose data is not — with the
/// figures in the key, every rule is safe to remember.
/// </para>
/// <para>
/// Not health data on its own — a rule id, a date and a hash — but it is keyed to a member, so it
/// is erased with them like every other member-scoped row.
/// </para>
/// </remarks>
public class BenignJudgement : BaseEntity
{
    public Guid CardiMemberId { get; set; }

    /// <summary>The rule id, as <c>StatisticalAlertRules</c> names it.</summary>
    public string Rule { get; set; } = string.Empty;

    /// <summary>
    /// The day this judgement covers, in the member's own local calendar. Not part of the key any
    /// more — <see cref="FindingFingerprint"/> is — but kept because it is what makes a row
    /// readable when someone is working out why a finding was or was not judged.
    /// </summary>
    public DateOnly LocalDate { get; set; }

    /// <summary>
    /// A hash of the finding's stored metric values: the figures that made it worth judging. Two
    /// findings with the same fingerprint are the same question, and the model's answer to it is
    /// reusable; one figure moving makes it a different question.
    /// </summary>
    public string FindingFingerprint { get; set; } = string.Empty;

    /// <summary>When the judgement was made, for retention sweeps and for reading the table back.</summary>
    public DateTime JudgedAtUtc { get; set; }
}
