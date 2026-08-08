using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CardiTrack.Infrastructure.ExternalClients;
using Microsoft.Extensions.DependencyInjection;

// Probes the Google Health API with a real access token and reports the JSON
// *shape* of each response, then runs the real FitbitApiClient over the same
// account so any field it fails to find shows up as a zero/null.
//
// Why this exists: FitbitApiClient's field names now match the v4 reference, but
// a name that is right on paper and absent in practice fails silently — it
// yields 0 rather than throwing — so the pairing still has to be seen against a
// live account once. It also catches per-account gaps the reference cannot show:
// a data type the wearer's device never populates looks identical to a bug here.

const string BaseUrl = "https://health.googleapis.com";

// Data types read via dataPoints:dailyRollUp, with the union member
// FitbitApiClient expects on the rollup point (camelCase of the type name).
// daily-resting-heart-rate is absent by design — it is a Daily record and is
// probed through list below, the only method it supports.
string[] rollupDataTypes =
[
    "steps",
    "distance",
    "active-minutes",
    "total-calories",
    "floors",
    "heart-rate",
];

var showValues = args.Contains("--raw");

// A mistyped --date silently probing the wrong day would look like "no data"
// and send the operator hunting a field-name bug that isn't there.
if (args.Contains("--date") && ParseDate(args) is null)
{
    Console.Error.WriteLine("--date needs an ISO date (yyyy-MM-dd), e.g. --date 2026-08-06.");
    return 1;
}
var date = ParseDate(args) ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

// Token via env var or prompt — never an argv, which leaks into shell history
// and the process list.
var token = Environment.GetEnvironmentVariable("HEALTH_ACCESS_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    token = ReadTokenFromConsole();
}
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("No token supplied. Set HEALTH_ACCESS_TOKEN or paste one when prompted.");
    return 1;
}

// Pasted and piped tokens arrive with surrounding whitespace, a trailing
// newline, or a BOM (PowerShell's pipe adds one). Anything outside printable
// ASCII reaches the Authorization header and throws deep inside HttpClient,
// so it is caught here where the message can name the real problem.
const char ByteOrderMark = '\uFEFF';
const char ZeroWidthSpace = '\u200B';
token = token.Trim().Trim(ByteOrderMark, ZeroWidthSpace).Trim();
if (token.Length == 0 || token.Any(c => c is < (char)0x21 or > (char)0x7E))
{
    Console.Error.WriteLine(
        "Token is empty or contains whitespace/non-ASCII characters — check for a stray BOM or line break from copy-paste.");
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

    // range is a CivilTimeInterval: start/end are CivilDateTime, so the calendar date nests under
    // "date". Omitting "time" means midnight, giving a closed-open single-day range.
    var body = $$"""
        {
          "range": {
            "start": { "date": { "year": {{date.Year}}, "month": {{date.Month}}, "day": {{date.Day}} } },
            "end":   { "date": { "year": {{date.AddDays(1).Year}}, "month": {{date.AddDays(1).Month}}, "day": {{date.AddDays(1).Day}} } }
          },
          "windowSizeDays": 1
        }
        """;

    using var request = new HttpRequestMessage(
        HttpMethod.Post, $"/v4/users/me/dataTypes/{dataType}/dataPoints:dailyRollUp")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    await ReportAsync(http, request, token!, showValues);
}

Console.WriteLine();
Console.WriteLine("--- daily-resting-heart-rate (dataPoints list) ---");
Console.WriteLine("    FitbitApiClient expects union member: \"dailyRestingHeartRate\"");
// A Daily record carries no rollup: dataPoints:dailyRollUp answers INVALID_ARGUMENT
// naming the type, so it is listed and filtered on its own google.type.Date field. The filter
// names the data type in snake_case ({daily_summary_data_type}.date), not the camelCase union
// member the response is keyed by — that spelling is rejected as INVALID_DATA_POINT_FILTER.
var restingFilter = Uri.EscapeDataString(
    $"daily_resting_heart_rate.date >= \"{date:yyyy-MM-dd}\" AND daily_resting_heart_rate.date < \"{date.AddDays(1):yyyy-MM-dd}\"");
using (var restingRequest = new HttpRequestMessage(
    HttpMethod.Get, $"/v4/users/me/dataTypes/daily-resting-heart-rate/dataPoints?filter={restingFilter}"))
{
    await ReportAsync(http, restingRequest, token!, showValues);
}

Console.WriteLine();
Console.WriteLine("--- sleep (dataPoints list) ---");
// civil_end_time takes the ISO date literal below; its physical-instant sibling end_time would
// reject it and demand RFC-3339. Same filter FitbitApiClient sends, so the probe exercises it.
var filter = Uri.EscapeDataString(
    $"sleep.interval.civil_end_time >= \"{date:yyyy-MM-dd}\" AND sleep.interval.civil_end_time < \"{date.AddDays(1):yyyy-MM-dd}\"");
using (var sleepRequest = new HttpRequestMessage(
    HttpMethod.Get, $"/v4/users/me/dataTypes/sleep/dataPoints?filter={filter}"))
{
    await ReportAsync(http, sleepRequest, token!, showValues);
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

// ISO only, parsed invariantly: "08/06/2026" means 8 June or 6 August depending on
// the machine's locale, and silently probing the wrong day is this tool's worst
// failure mode — an empty rollup looks exactly like the field-name bug it hunts.
static DateOnly? ParseDate(string[] args)
{
    var index = Array.IndexOf(args, "--date");
    if (index < 0 || index + 1 >= args.Length)
        return null;
    return DateOnly.TryParseExact(
        args[index + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
        ? parsed
        : null;
}

// Masks the token as it is typed: it is a live credential for someone's health
// data, and an echoed one lingers in terminal scrollback and screen recordings.
// Console.ReadKey cannot read redirected input, so piping falls back to
// ReadLine — and says so rather than implying the input was hidden.
static string? ReadTokenFromConsole()
{
    if (Console.IsInputRedirected)
    {
        Console.Error.Write("Google OAuth access token (stdin is redirected — input will NOT be masked): ");
        return Console.ReadLine();
    }

    Console.Error.Write("Google OAuth access token (input hidden): ");
    var token = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Console.Error.WriteLine();
                return token.ToString();

            case ConsoleKey.Backspace:
                if (token.Length > 0)
                    token.Length--;
                break;

            case ConsoleKey.Escape:
                Console.Error.WriteLine();
                return null;

            default:
                // Control characters (arrow keys, F-keys) surface as '\0' — ignore
                // them so they cannot corrupt the token.
                if (!char.IsControl(key.KeyChar))
                    token.Append(key.KeyChar);
                break;
        }
    }
}

static string ToCamelCase(string dataType)
{
    var parts = dataType.Split('-');
    return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}

static async Task ReportAsync(HttpClient http, HttpRequestMessage request, string token, bool showValues)
{
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
