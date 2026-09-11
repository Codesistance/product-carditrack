using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// How long a caregiver's export confirmation may authorize later exports
/// without asking again. The generate token itself stays two minutes; this
/// is the standing grant that later exports may reuse.
/// </summary>
public enum ExportConsentRememberFor
{
    [Display(Name = "Just this export")]
    ThisExport = 1,

    [Display(Name = "1 week")]
    OneWeek = 2,

    [Display(Name = "2 weeks")]
    TwoWeeks = 3,

    /// <summary>The longest a confirmation may be kept — 30 days, not a calendar month.</summary>
    [Display(Name = "1 month")]
    OneMonth = 4
}
