using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// An extra condition the period must satisfy before a breach counts. The one gate that matters is
/// stillness: 120 bpm on a staircase is what a working heart looks like, and the same 120 bpm in a
/// chair is the finding. Both Apple and Fitbit gate their high- and low-heart-rate notifications on
/// the wearer having been inactive for ten minutes, and it is the gate rather than the number that
/// makes those notifications bearable.
/// </summary>
public enum AlarmContextGate
{
    /// <summary>No gate — every period counts.</summary>
    [Display(Name = "Any time")]
    None = 1,

    /// <summary>Only periods in which the member was still. Requires a step series for the same
    /// period; a period whose movement cannot be established is not counted as a breach.</summary>
    [Display(Name = "Only while they are still")]
    Inactive = 2,
}
