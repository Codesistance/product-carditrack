using System.Text.Json;
using CardiTrack.API.Controllers.Dev;
using CardiTrack.Domain.Enums;

namespace CardiTrack.IntegrationTests.Notifications;

/// <summary>
/// The dev test-push endpoint is documented — in notification_engine.md §13 and in
/// <c>scripts/dev-push.ps1</c> — as taking category and severity by name. It did not: with no
/// <c>JsonStringEnumConverter</c> anywhere in the solution, <c>"Safety"</c> failed model binding
/// and <c>[ApiController]</c> turned it into a bare 400, so the endpoint rejected every request
/// the docs told you to send. These pin the contract to what is written down.
/// </summary>
public class DevPushContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ── Requests by name, as documented ───────────────────────────────────────────

    [Theory]
    [InlineData("Safety", DeliveryCategory.Safety)]
    [InlineData("Health", DeliveryCategory.Health)]
    [InlineData("Nudge", DeliveryCategory.Nudge)]
    public void Category_BindsFromItsName(string name, DeliveryCategory expected)
    {
        var request = JsonSerializer.Deserialize<DevPushRequest>(
            $$"""{"userId":"f6b91193-4a13-42e4-b063-9d35d9489b29","category":"{{name}}"}""", Web);

        Assert.Equal(expected, request!.Category);
    }

    [Theory]
    [InlineData("Red", AlertSeverity.Red)]
    [InlineData("Orange", AlertSeverity.Orange)]
    public void Severity_BindsFromItsName(string name, AlertSeverity expected)
    {
        var request = JsonSerializer.Deserialize<DevPushRequest>(
            $$"""{"userId":"f6b91193-4a13-42e4-b063-9d35d9489b29","category":"Health","severity":"{{name}}"}""", Web);

        Assert.Equal(expected, request!.Severity);
    }

    [Fact]
    public void Severity_IsOptional()
    {
        var request = JsonSerializer.Deserialize<DevPushRequest>(
            """{"userId":"f6b91193-4a13-42e4-b063-9d35d9489b29","category":"Safety"}""", Web);

        Assert.Null(request!.Severity);
    }

    // ── Numbers still bind ────────────────────────────────────────────────────────

    [Fact]
    public void NumericCategoryAndSeverity_StillBind()
    {
        // The shape callers used while the endpoint only accepted numbers. Keeping it working is
        // why the converter is applied per-property rather than by changing the enum's own
        // serialization.
        var request = JsonSerializer.Deserialize<DevPushRequest>(
            """{"userId":"f6b91193-4a13-42e4-b063-9d35d9489b29","category":2,"severity":4}""", Web);

        Assert.Equal(DeliveryCategory.Health, request!.Category);
        Assert.Equal(AlertSeverity.Red, request.Severity);
    }

    [Fact]
    public void UnknownCategoryName_IsRejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DevPushRequest>(
            """{"userId":"f6b91193-4a13-42e4-b063-9d35d9489b29","category":"Nonsense"}""", Web));
    }

    // ── Responses read by a human ─────────────────────────────────────────────────

    [Fact]
    public void Response_NamesItsEnumsRatherThanNumberingThem()
    {
        var json = JsonSerializer.Serialize(new DevPushResponse
        {
            Channel = DeliveryChannel.Push,
            State = DeliveryState.Sent,
            Devices =
            [
                new DevPushDeviceResult
                {
                    Platform = DevicePlatform.Android,
                    OsAuthorizationStatus = OsAuthorizationStatus.Granted,
                    State = DeliveryState.Sent
                }
            ]
        }, Web);

        Assert.Contains("\"channel\":\"Push\"", json);
        Assert.Contains("\"state\":\"Sent\"", json);
        Assert.Contains("\"platform\":\"Android\"", json);
        Assert.Contains("\"osAuthorizationStatus\":\"Granted\"", json);
    }
}
