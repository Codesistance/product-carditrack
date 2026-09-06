using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Responses;

/// <summary>One alarm as the client sees it. Enums serialize as integers, like the rest of the API.</summary>
public sealed class MetricAlarmResponse
{
    public Guid Id { get; init; }

    /// <summary>Null on an account-level default; set on a member row.</summary>
    public Guid? CardiMemberId { get; init; }

    public Guid? DerivedFromAlarmId { get; init; }

    public string Name { get; init; } = string.Empty;

    public AlarmMetric Metric { get; init; }
    public AlarmStatistic Statistic { get; init; }
    public AlarmOperator Operator { get; init; }
    public AlarmThresholdKind ThresholdKind { get; init; }
    public decimal ThresholdValue { get; init; }
    public int PeriodMinutes { get; init; }
    public int EvaluationPeriods { get; init; }
    public int DatapointsToAlarm { get; init; }
    public AlarmMissingDataTreatment MissingDataTreatment { get; init; }
    public AlertSeverity Severity { get; init; }
    public AlarmContextGate ContextGate { get; init; }
    public bool IsEnabled { get; init; }

    /// <summary>
    /// The condition in one sentence, composed server-side so the settings card, the alert it
    /// raises and any future surface cannot describe the same alarm three different ways.
    /// </summary>
    public string Condition { get; init; } = string.Empty;

    /// <summary>Only present on a member's effective list: whether this row is inherited from the
    /// account, an override of one, or the member's own.</summary>
    public string? Provenance { get; init; }

    /// <summary>The current evaluation state for this member, when one has been recorded.</summary>
    public AlarmEvaluationState? State { get; init; }

    public DateTime? StateSinceUtc { get; init; }
}

/// <summary>The builder's option list — what may legally be combined.</summary>
public sealed class AlarmCatalogueResponse
{
    public IReadOnlyList<AlarmMetricOptionResponse> Metrics { get; init; } = [];

    /// <summary>The most datapoints an evaluation range may span, for any metric.</summary>
    public int MaxEvaluationPeriods { get; init; }
}

public sealed class AlarmMetricOptionResponse
{
    public AlarmMetric Metric { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;

    /// <summary>"granular" for a sub-daily series, "daily" for one value per day or night.</summary>
    public string Source { get; init; } = string.Empty;

    public IReadOnlyList<AlarmStatistic> Statistics { get; init; } = [];
    public IReadOnlyList<int> PeriodMinutes { get; init; } = [];

    /// <summary>The band an absolute threshold must fall inside. Bounds exist so a caregiver cannot
    /// build an alarm that pages them every night — see the catalogue for why each one is where it is.</summary>
    public decimal MinThreshold { get; init; }

    public decimal MaxThreshold { get; init; }

    public bool SupportsBaselinePercent { get; init; }
    public bool SupportsBaselineSigma { get; init; }

    /// <summary>Whether the stillness gate can be applied — only sub-daily metrics can, since it is
    /// measured from the step series over the same minutes.</summary>
    public bool SupportsContextGate { get; init; }
}
