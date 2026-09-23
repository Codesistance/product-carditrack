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
/// <b>This is derived health data, and the hash is not what makes it so.</b> The first version of
/// these remarks called the row "a rule id, a date and a hash" and concluded it carried none,
/// which is wrong on its own terms: <see cref="Rule"/> is stored in the clear beside the member
/// and the day, and a row reading <c>ecg_afib</c> says the wearer's device classified a recording
/// as atrial fibrillation for that member on that date. That is a health fact about a named
/// person, whatever the fingerprint does.
/// </para>
/// <para>
/// The fingerprint is deliberately <em>not</em> a privacy control and must not be read as one. It
/// is unsalted and the findings behind it are low-entropy — a rule, a date and a small count —
/// so a reader of this table could dictionary-match it back to the figures. That is acceptable
/// only because it discloses nothing the same database does not already hold in plaintext one
/// table over: <c>ActivityLogs</c> carries that member's readings for that day in full. Keying it
/// with a managed secret would move the boundary nowhere and would silently empty this cache on
/// every rotation. The control that matters is the classification: this table is governed,
/// access-controlled and erased as derived health data, like every other member-scoped row —
/// see the manual erasure runbook and DPIA A15.
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
