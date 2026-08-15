using System.Text.Json;

namespace CardiTrack.Application.Services;

/// <summary>
/// Parsed sparse disable-list for one CardiMember. Missing preference row → all rules on.
/// Unknown ids in storage are ignored (forward-compatible with retired rules).
/// </summary>
public sealed class AlertRuleOverrides
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HashSet<string> _disabled;

    private AlertRuleOverrides(HashSet<string> disabled) => _disabled = disabled;

    public static AlertRuleOverrides AllEnabled { get; } = new(new HashSet<string>(StringComparer.Ordinal));

    public static AlertRuleOverrides FromJson(string? disabledRulesJson)
    {
        if (string.IsNullOrWhiteSpace(disabledRulesJson))
            return AllEnabled;

        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(disabledRulesJson, JsonOptions);
            if (ids is null || ids.Count == 0)
                return AllEnabled;

            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id) && AlertRuleCatalogue.IsKnown(id))
                    set.Add(id);
            }

            return set.Count == 0 ? AllEnabled : new AlertRuleOverrides(set);
        }
        catch (JsonException)
        {
            // Corrupt preference must not page a family harder — fail open to defaults (all on).
            return AllEnabled;
        }
    }

    public bool IsEnabled(string ruleId) => !_disabled.Contains(ruleId);

    public IReadOnlyCollection<string> DisabledRuleIds => _disabled.ToArray();

    public string ToJson()
    {
        if (_disabled.Count == 0)
            return "[]";

        var ordered = _disabled.OrderBy(id => id, StringComparer.Ordinal).ToList();
        return JsonSerializer.Serialize(ordered);
    }

    public AlertRuleOverrides WithEnabled(string ruleId, bool enabled)
    {
        var next = new HashSet<string>(_disabled, StringComparer.Ordinal);
        if (enabled)
            next.Remove(ruleId);
        else
            next.Add(ruleId);
        return new AlertRuleOverrides(next);
    }
}
