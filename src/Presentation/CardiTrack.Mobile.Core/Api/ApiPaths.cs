namespace CardiTrack.Mobile.Core.Api;

/// <summary>
/// The one place a GET's path is spelled. The path is also the offline cache's key, so the
/// live call, the cache-only peek for the same question, and the eviction a mutation performs
/// must all produce it the same way — three call sites building the same string by hand is how
/// a peek ends up answering a neighbouring question, or an eviction missing its target.
/// </summary>
internal static class ApiPaths
{
    public const string CardiMembers = "api/Onboarding/cardimembers";
    public const string AlarmCatalogue = "api/v1/alarms/catalogue";
    public const string NotificationSummary = "api/v1/notifications/summary";
    public const string NotificationMutes = "api/v1/notifications/mutes";
    public const string NotificationPreferences = "api/v1/notifications/preferences";

    public static string CardiMember(Guid cardiMemberId) => $"api/v1/cardimembers/{cardiMemberId}";
    public static string Dashboard(Guid cardiMemberId) => $"api/v1/cardimembers/{cardiMemberId}/dashboard";
    public static string AlertPreferences(Guid cardiMemberId) => $"api/v1/cardimembers/{cardiMemberId}/alert-preferences";
    public static string MemberAlarms(Guid cardiMemberId) => $"api/v1/cardimembers/{cardiMemberId}/alarms";
    public static string JournalSettings(Guid cardiMemberId) => $"api/v1/cardimembers/{cardiMemberId}/journal-settings";
    public static string Devices(Guid cardiMemberId) => $"api/v1/cardimembers/{cardiMemberId}/devices";
    public static string CurrentStatus(Guid cardiMemberId) => $"api/v1/insights/members/{cardiMemberId}/status";
    public static string Digest(Guid cardiMemberId) => $"api/v1/insights/members/{cardiMemberId}/digest";
    public static string Advise(Guid cardiMemberId) => $"api/v1/insights/members/{cardiMemberId}/advise";
    public static string Alert(Guid alertId) => $"api/v1/alerts/{alertId}";

    public static string JournalEntries(
        Guid cardiMemberId, JournalCadence cadence, int limit, string? search, DateOnly? from, string? urgency)
    {
        var query = $"?limit={limit}&audience={cadence.WireValue()}";
        if (!string.IsNullOrWhiteSpace(search))
            query += $"&search={Uri.EscapeDataString(search.Trim())}";
        if (from is { } fromDay)
            query += $"&from={fromDay:yyyy-MM-dd}";
        if (!string.IsNullOrWhiteSpace(urgency))
            query += $"&urgency={Uri.EscapeDataString(urgency)}";
        return $"api/v1/insights/members/{cardiMemberId}/digests{query}";
    }

    public static string JournalEntry(Guid cardiMemberId, JournalCadence cadence, DateOnly localDate) =>
        $"api/v1/insights/members/{cardiMemberId}/digest?date={localDate:yyyy-MM-dd}&audience={cadence.WireValue()}";

    public static string Questionnaires(Guid cardiMemberId, string? search, int page, int pageSize)
    {
        var query = $"?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search))
            query += $"&search={Uri.EscapeDataString(search)}";
        return $"api/v1/cardimembers/{cardiMemberId}/questionnaires{query}";
    }

    public static string Alerts(
        string? severity, string? status, DateTime? from, DateTime? to, int? limit, Guid? cardiMemberId)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(severity)) filters.Add($"severity={Uri.EscapeDataString(severity)}");
        if (!string.IsNullOrWhiteSpace(status)) filters.Add($"status={Uri.EscapeDataString(status)}");
        // Round-trip ("O") keeps the offset on the wire, so a "Today" filter set on a phone in
        // Lagos isn't reinterpreted as UTC midnight by the server.
        if (from is { } f) filters.Add($"from={Uri.EscapeDataString(f.ToString("O"))}");
        if (to is { } t) filters.Add($"to={Uri.EscapeDataString(t.ToString("O"))}");
        if (limit is { } l) filters.Add($"limit={l}");

        // The member-scoped route rather than a cardiMemberId query on the collection: both exist
        // and both apply the same access check (AlertsController.ListAsync), but the route says in
        // the path whose alerts these are, which is what the audit trail records.
        var basePath = cardiMemberId is { } memberId
            ? $"api/v1/cardimembers/{memberId}/alerts"
            : "api/v1/alerts";
        return filters.Count == 0 ? basePath : $"{basePath}?{string.Join("&", filters)}";
    }

    public static string Notifications(string? state, string? category, bool? owned, int? limit)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(state)) filters.Add($"state={Uri.EscapeDataString(state)}");
        if (!string.IsNullOrWhiteSpace(category)) filters.Add($"category={Uri.EscapeDataString(category)}");
        if (owned is { } o) filters.Add($"owned={(o ? "true" : "false")}");
        if (limit is { } l) filters.Add($"limit={l}");

        return filters.Count == 0
            ? "api/v1/notifications"
            : $"api/v1/notifications?{string.Join("&", filters)}";
    }
}
