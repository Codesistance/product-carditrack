namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// One pass of the R1 statistical alert engine: evaluate every recently-active member's daily
/// readings against their established 30-day baseline with the rule set in
/// <c>StatisticalAlertRules</c>, and raise the alerts that survive cooldown and same-day
/// deduplication. Pure rules, no AI — the deterministic counterpart to the pipeline's
/// MedGemma-routed alerts.
/// </summary>
public interface IStatisticalAlertService
{
    /// <summary>Evaluates every candidate member once; returns how many alerts were raised.</summary>
    Task<int> EvaluateAsync(DateTime utcNow, CancellationToken ct = default);
}
