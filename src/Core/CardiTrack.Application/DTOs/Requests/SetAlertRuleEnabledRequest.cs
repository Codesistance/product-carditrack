namespace CardiTrack.Application.DTOs.Requests;

public sealed class SetAlertRuleEnabledRequest
{
    /// <summary>Required. Null/missing must 400 — a bool default of false would silently disable.</summary>
    public bool? Enabled { get; set; }
}
