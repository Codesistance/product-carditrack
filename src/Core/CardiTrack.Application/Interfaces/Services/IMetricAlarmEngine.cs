namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Evaluates every enabled user-defined alarm across the estate for one tick. The Worker's
/// <c>MetricAlarmWorker</c> is the only caller — this is non-AI polling work and lives nowhere else.
/// </summary>
public interface IMetricAlarmEngine
{
    /// <summary>Runs one pass and returns how many alerts it raised.</summary>
    Task<int> EvaluateAsync(DateTime utcNow, CancellationToken ct = default);
}
