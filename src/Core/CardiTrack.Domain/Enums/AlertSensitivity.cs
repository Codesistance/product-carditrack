using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// How far a metric must drift from baseline before the caregiver hears about it (M1-14).
/// </summary>
/// <remarks>
/// Stored per CardiMember but <b>not yet consumed</b>: alert generation lives in the
/// GCP inference pipeline described in docs/llm_design.md, which is not built. Until then
/// this records the caregiver's preference and changes nothing about alerting.
/// </remarks>
public enum AlertSensitivity
{
    [Display(Name = "Low")]
    Low = 1,

    [Display(Name = "Medium")]
    Medium = 2,

    [Display(Name = "High")]
    High = 3
}
