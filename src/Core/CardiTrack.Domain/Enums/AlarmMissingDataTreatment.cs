using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// What an evaluation period with no reading counts as — CloudWatch's <c>TreatMissingData</c>,
/// minus one option.
/// <para>
/// <b>CloudWatch's <c>breaching</c> is deliberately absent.</b> Treating absent data as over the
/// line turns "the watch is off the wrist" into a page at three in the morning, and it contradicts
/// the null-vs-zero discipline this product holds everywhere else: a missing reading means
/// <em>not measured</em>, never <em>did nothing</em>. Data absence has its own producer —
/// <c>InactivityDetectionWorker</c> raises a device-silence alert after two quiet waking hours —
/// which is the same separation Cloud Monitoring draws by making metric-absence its own policy
/// type rather than a flag on a threshold condition.
/// </para>
/// </summary>
public enum AlarmMissingDataTreatment
{
    /// <summary>If every period in the evaluation range is missing, the alarm reports
    /// insufficient data and says nothing. The default, as it is in CloudWatch.</summary>
    [Display(Name = "Say nothing")]
    Missing = 1,

    /// <summary>A missing period counts as within the threshold.</summary>
    [Display(Name = "Treat as normal")]
    NotBreaching = 2,

    /// <summary>Missing periods are skipped and the alarm holds whatever state it was already in.</summary>
    [Display(Name = "Keep the current state")]
    Ignore = 3,
}
