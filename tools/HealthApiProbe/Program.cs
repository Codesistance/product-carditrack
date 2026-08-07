using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CardiTrack.Infrastructure.ExternalClients;
using Microsoft.Extensions.DependencyInjection;

// Probes the Google Health API with a real access token and reports the JSON
// *shape* of each response, then runs the real FitbitApiClient over the same
// account so any field it fails to find shows up as a zero/null.
//
// Why this exists: Google's v4 reference documents only some rollup value
// schemas, so several field names in FitbitApiClient were inferred from the
// documented `{field}_{aggregation}` convention. A wrong guess does not throw —
// it silently yields 0 — so it has to be checked against a live account once.

const string BaseUrl = "https://health.googleapis.com";

// Data types read via dataPoints:dailyRollUp, with the union member
// FitbitApiClient expects on the rollup point (camelCase of the type name).
string[] rollupDataTypes =
[
    "steps",
    "distance",
    "active-minutes",
    "total-calories",
    "floors",
    "heart-rate",
    "resting-heart-rate",
];

var showValues = args.Contains("--raw");
var date = ParseDate(args) ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

// Token via env var or prompt — never an argv, which leaks into shell history
// and the process list.
var token = Environment.GetEnvironmentVariable("HEALTH_ACCESS_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.Write("Google OAuth access token (input not echoed to output): ");
    token = Console.ReadLine();
}
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("No token supplied. Set HEALTH_ACCESS_TOKEN or paste one when prompted.");
    return 1;
}

Console.WriteLine($"Google Health API probe — date {date:yyyy-MM-dd} (UTC), base {BaseUrl}");
Console.WriteLine(showValues
    ? "MODE: --raw — output includes REAL HEALTH VALUES. Do not paste into issues, PRs, or chat."
    : "MODE: shape only — field names and value types, values elided. Safe to share.");
Console.WriteLine(new string('=', 72));

using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

foreach (var dataType in rollupDataTypes)
{
    Console.WriteLine();
    Console.WriteLine($"--- {dataType} (dataPoints:dailyRollUp) ---");
    Console.WriteLine($"    FitbitApiClient expects union member: \"{ToCamelCase(dataType)}\"");

    var body = $$"""
        {
          "range": {
            "start": { "year": {{date.Year}}, "month": {{date.Month}}, "day": {{date.Day}} },
            "end":   { "year": {{date.AddDays(1).Year}}, "month": {{date.AddDays(1).Month}}, "day": {{date.AddDays(1).Day}} }
          },
          "windowSizeDays": 1
        }
        """;

    using var request = new HttpRequestMessage(
        HttpMethod.Post, $"/v4/users/me/dataTypes/{dataType}/dataPoints:dailyRollUp")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    await ReportAsync(request, token!, showValues);
}

Console.WriteLine();
Console.WriteLine("--- sleep (dataPoints list) ---");
var filter = Uri.EscapeDataString(
    $"sleep.interval.end_time >= \"{date:yyyy-MM-dd}\" AND sleep.interval.end_time < \"{date.AddDays(1):yyyy-MM-dd}\"");
using (var sleepRequest = new HttpRequestMessage(
    HttpMethod.Get, $"/v4/users/me/dataTypes/sleep/dataPoints?filter={filter}"))
{
    await ReportAsync(sleepRequest, token!, showValues);
}

// ── What the real client makes of the same account ───────────────────────────

Console.WriteLine();
Console.WriteLine(new string('=', 72));
Console.WriteLine("FitbitApiClient.GetHealthSnapshotAsync — parsed result");
Console.WriteLine("A zero/null here for a metric whose shape dump above showed data means the");
Console.WriteLine("field name in FitbitApiClient is wrong.");
Console.WriteLine();

var services = new ServiceCollection();
services.AddHttpClient("FitbitClient", c =>
{
    c.BaseAddress = new Uri(BaseUrl);
    c.DefaultRequestHeaders.Add("Accept", "application/json");
});
using var provider = services.BuildServiceProvider();
var client = new FitbitApiClient(provider.GetRequiredService<IHttpClientFactory>());

try
{
    var snapshot = await client.GetHealthSnapshotAsync(token!, date);
    foreach (var property in typeof(DeviceHealthSnapshot).GetProperties())
    {
        var value = property.GetValue(snapshot);
        var suspicious = value is null or 0 or 0m;
        Console.WriteLine($"  {property.Name,-20} {value ?? "(null)"}{(suspicious ? "   <-- zero/null" : "")}");
    }
}
catch (FitbitApiException ex)
{
    Console.WriteLine($"  FitbitApiException {ex.StatusCode}: {ex.Message}");
}

return 0;

static DateOnly? ParseDate(string[] args)
{
    var index = Array.IndexOf(args, "--date");
    if (index < 0 || index + 1 >= args.Length)
        return null;
    return DateOnly.TryParse(args[index + 1], out var parsed) ? parsed : null;
}

static string ToCamelCase(string dataType)
{
    var parts = dataType.Split('-');
    return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}

static async Task ReportAsync(HttpRequestMessage request, string token, bool showValues)
{
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
    using var response = await http.SendAsync(request);
    var payload = await response.Content.ReadAsStringAsync();

    Console.WriteLine($"    HTTP {(int)response.StatusCode} {response.StatusCode}");

    if (!response.IsSuccessStatusCode)
    {
        // Error bodies carry no health data, so they print verbatim — that's the
        // part worth reading (scope not granted, data type unknown, …).
        Console.WriteLine($"    {payload}");
        return;
    }

    try
    {
        using var document = JsonDocument.Parse(payload);
        WriteShape(document.RootElement, indent: 4, showValues);
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"    (unparseable JSON: {ex.Message})");
    }
}

static void WriteShape(JsonElement element, int indent, bool showValues)
{
    var pad = new string(' ', indent);
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    Console.WriteLine($"{pad}{property.Name}:");
                    WriteShape(property.Value, indent + 2, showValues);
                }
                else
                {
                    Console.WriteLine($"{pad}{property.Name}: {Describe(property.Value, showValues)}");
                }
            }
            break;

        case JsonValueKind.Array:
            var items = element.EnumerateArray().ToList();
            Console.WriteLine($"{pad}[{items.Count} item(s)]");
            // One element is enough to establish the shape; the rest repeat it.
            if (items.Count > 0)
                WriteShape(items[0], indent + 2, showValues);
            break;

        default:
            Console.WriteLine($"{pad}{Describe(element, showValues)}");
            break;
    }
}

static string Describe(JsonElement element, bool showValues) => element.ValueKind switch
{
    JsonValueKind.Number => showValues ? element.GetRawText() : "<number>",
    JsonValueKind.String => showValues ? element.GetRawText() : "<string>",
    JsonValueKind.True or JsonValueKind.False => showValues ? element.GetRawText() : "<bool>",
    JsonValueKind.Null => "<null>",
    _ => "<?>",
};
