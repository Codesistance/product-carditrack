namespace CardiTrack.Application.DTOs.Responses;

public sealed class AlertPreferencesResponse
{
    public Guid CardiMemberId { get; init; }
    public IReadOnlyList<AlertRuleClusterResponse> Clusters { get; init; } = [];
}

public sealed class AlertRuleClusterResponse
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<AlertRuleSettingResponse> Rules { get; init; } = [];
}

public sealed class AlertRuleSettingResponse
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Effective enablement — default true when the caregiver has never toggled this rule.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>False until the producer for this rule ships; the client should not offer a toggle.</summary>
    public bool IsImplemented { get; init; }
}
