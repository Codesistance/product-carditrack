using System.Net;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.UnitTests.Mobile;

public class CardiTrackApiClientTests
{
    private static (CardiTrackApiClient Client, FakeHttpMessageHandler Http) CreateSut()
    {
        var http = new FakeHttpMessageHandler();
        var client = new CardiTrackApiClient(
            new HttpClient(http) { BaseAddress = new Uri("https://api.test") });
        return (client, http);
    }

    [Fact]
    public async Task GetOnboardingStatus_UsesUnversionedRoute_AndUnwrapsEnvelope()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"hasOrganization":true,"hasUserAccount":true,
             "hasCardiMember":false,"currentStep":3,"totalSteps":7},"timestamp":"2026-08-01T00:00:00Z"}
            """);

        var status = await client.GetOnboardingStatusAsync();

        Assert.Equal("/api/Onboarding/status", http.Requests.Single().Uri!.AbsolutePath);
        Assert.True(status.HasUserAccount);
        Assert.False(status.HasCardiMember);
        Assert.Equal(3, status.CurrentStep);
    }

    [Fact]
    public async Task CreateOrganization_PostsCamelCaseJson()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"id":"6f9619ff-8b86-d011-b42d-00c04fc964ff",
             "name":"Ada's Family","type":1,"isActive":true},"timestamp":"2026-08-01T00:00:00Z"}
            """);

        var org = await client.CreateOrganizationAsync(new CreateOrganizationRequest
        {
            Name = "Ada's Family",
            Type = OrganizationType.Family,
        });

        var request = http.Requests.Single();
        Assert.Equal("/api/Onboarding/organization", request.Uri!.AbsolutePath);
        Assert.Contains("\"name\":", request.Body);
        Assert.Equal("Ada's Family", org.Name);
    }

    [Fact]
    public async Task GetDashboard_UsesV1Route()
    {
        var (client, http) = CreateSut();
        var memberId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, $$"""
            {"success":true,"message":"ok","data":{"cardiMemberId":"{{memberId}}","name":"Margaret",
             "age":78,"healthStatus":"green","unreadAlertCount":1,
             "device":{"hasActiveConnection":true},"baseline":{"isLearning":false},
             "recentAlerts":[]},"timestamp":"2026-08-01T00:00:00Z"}
            """);

        var dashboard = await client.GetDashboardAsync(memberId);

        Assert.Equal($"/api/v1/cardimembers/{memberId}/dashboard", http.Requests.Single().Uri!.AbsolutePath);
        Assert.Equal("green", dashboard.HealthStatus);
        Assert.True(dashboard.Device.HasActiveConnection);
        Assert.Null(dashboard.Metrics);
    }

    [Fact]
    public async Task NonSuccess_ThrowsApiException_WithServerMessage()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.NotFound, """
            {"success":false,"message":"CardiMember not found","errors":[],"timestamp":"2026-08-01T00:00:00Z"}
            """);

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.GetDashboardAsync(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("CardiMember not found", ex.Message);
    }

    [Fact]
    public async Task ValidationErrors_AreFlattenedOntoException()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.BadRequest, """
            {"success":false,"message":"Validation failed","errors":[{"field":"Name","message":"Name is required"}],
             "timestamp":"2026-08-01T00:00:00Z"}
            """);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            client.CreateOrganizationAsync(new CreateOrganizationRequest()));

        Assert.Contains(ex.Errors, e => e.Contains("Name is required"));
    }

    [Fact]
    public async Task Unauthorized_SetsSessionExpiredFlag()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.Unauthorized, "");

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.GetOnboardingStatusAsync());

        Assert.True(ex.IsSessionExpired);
    }

    [Fact]
    public async Task TransportFailure_ThrowsApiException_WithFriendlyMessage()
    {
        var (client, http) = CreateSut();
        http.Throws(new HttpRequestException("socket"));

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.GetOnboardingStatusAsync());

        Assert.Contains("No connection", ex.Message);
    }
}
