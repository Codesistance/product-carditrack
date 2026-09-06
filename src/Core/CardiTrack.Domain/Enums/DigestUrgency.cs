namespace CardiTrack.Domain.Enums;

/// <summary>
/// The model's own read of how soon the family should act on a digest — judged from the same
/// readings the summary describes, run alongside (never replacing) the deterministic alert
/// engine (<c>StatisticalAlertService</c>). A second, independent voice: the two can disagree,
/// and neither one drives the other's Alert rows. The dashboard hero may take the higher of
/// unresolved alerts, a recent yellow-or-worse hour assessment, and a family digest above
/// Watch — the digest still does not create Alert rows.
/// </summary>
public enum DigestUrgency
{
    /// <summary>Nothing pressing — today's readings don't ask for anything.</summary>
    Watch = 1,

    /// <summary>Worth a call or a visit today, not urgently.</summary>
    CheckIn = 2,

    /// <summary>Worth prompt attention — today, not "when convenient."</summary>
    Concerning = 3,

    /// <summary>Worth acting on right away.</summary>
    ActNow = 4,
}
