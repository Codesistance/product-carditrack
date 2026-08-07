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
    public async Task ResendVerification_PostsToAuthRoute()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK,
            """{"success":true,"message":"ok","data":true,"timestamp":"2026-08-04T00:00:00Z"}""");

        await client.ResendVerificationAsync("a@b.com");

        var request = http.Requests.Single();
        Assert.Equal("/api/v1/auth/resend-verification", request.Uri!.AbsolutePath);
        Assert.Contains("a@b.com", request.Body);
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
    public async Task Setup_PostsCombinedPayload_AndUnwrapsBothResponses()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{
             "organization":{"id":"6f9619ff-8b86-d011-b42d-00c04fc964ff","name":"Ada's Family","type":1,"isActive":true},
             "user":{"id":"7f9619ff-8b86-d011-b42d-00c04fc964ff","email":"ada@lovelace.dev","name":"Ada",
              "organizationId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","isActive":true}},
             "timestamp":"2026-08-01T00:00:00Z"}
            """);

        var setup = await client.SetupAsync(new OnboardingSetupRequest
        {
            Organization = new CreateOrganizationRequest { Name = "Ada's Family", Type = OrganizationType.Family },
            User = new OnboardingSetupUserRequest { Email = "ada@lovelace.dev", Name = "Ada" },
        });

        var request = http.Requests.Single();
        Assert.Equal("/api/Onboarding/setup", request.Uri!.AbsolutePath);
        Assert.Contains("\"organization\":", request.Body);
        Assert.Contains("\"user\":", request.Body);
        Assert.Equal("Ada's Family", setup.Organization.Name);
        Assert.Equal(setup.Organization.Id, setup.User.OrganizationId);
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

    // ── M1-13 / M1-14 / M1-15 ───────────────────────────────────────────────────

    private static readonly Guid MemberId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    private static readonly Guid DeviceId = Guid.Parse("7f9619ff-8b86-d011-b42d-00c04fc964ff");

    [Fact]
    public async Task GetCardiMember_UsesVersionedRoute_AndUnwrapsDetail()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{
             "id":"6f9619ff-8b86-d011-b42d-00c04fc964ff","name":"Margaret Doe","dateOfBirth":"1945-06-15",
             "age":80,"relationship":2,"isPrimaryCaregiver":true,"emergencyContactName":"Jane Doe",
             "emergencyContactPhone":"+15551234567","medicalNotes":"Pacemaker fitted 2019",
             "alertSensitivity":2,"monitoringPaused":false,"monitoringSince":"2026-01-15T09:00:00Z",
             "connectedDeviceCount":2,
             "baseline":{"isLearning":true,"daysCaptured":15,"daysRequired":30,"percentComplete":50}},
             "timestamp":"2026-08-07T00:00:00Z"}
            """);

        var detail = await client.GetCardiMemberAsync(MemberId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/v1/cardimembers/{MemberId}", request.Uri!.AbsolutePath);
        Assert.Equal("Margaret Doe", detail.Name);
        Assert.Equal("Pacemaker fitted 2019", detail.MedicalNotes);
        Assert.Equal(2, detail.ConnectedDeviceCount);
        Assert.Equal(15, detail.Baseline.DaysCaptured);
    }

    [Fact]
    public async Task UpdateCardiMember_PutsFormPayload()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{
             "id":"6f9619ff-8b86-d011-b42d-00c04fc964ff","name":"Margaret A. Doe","age":80,
             "baseline":{"isLearning":false,"daysCaptured":30,"daysRequired":30,"percentComplete":100}},
             "timestamp":"2026-08-07T00:00:00Z"}
            """);

        await client.UpdateCardiMemberAsync(MemberId, new UpdateCardiMemberRequest
        {
            Name = "Margaret A. Doe",
            DateOfBirth = new DateOnly(1945, 6, 15),
            RelationshipType = RelationshipType.Parent,
            MedicalNotes = "Now also on lisinopril",
        });

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/api/v1/cardimembers/{MemberId}", request.Uri!.AbsolutePath);
        Assert.Contains("Margaret A. Doe", request.Body);
        Assert.Contains("lisinopril", request.Body);
    }

    [Fact]
    public async Task RemoveCardiMember_DeletesAndAcceptsEmpty204()
    {
        var (client, http) = CreateSut();
        // 204 carries no envelope — the client must not try to read one.
        http.Enqueue(HttpStatusCode.NoContent, "");

        await client.RemoveCardiMemberAsync(MemberId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/v1/cardimembers/{MemberId}", request.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task RemoveCardiMember_ThrowsApiException_OnFailure()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.NotFound,
            """{"success":false,"message":"CardiMember not found","timestamp":"2026-08-07T00:00:00Z"}""");

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.RemoveCardiMemberAsync(MemberId));

        Assert.Equal("CardiMember not found", ex.Message);
    }

    [Fact]
    public async Task PauseMonitoring_PostsDurationAndReason()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"monitoringPaused":true,
             "monitoringPausedUntil":"2026-08-08T21:00:00Z","monitoringPauseReason":"Travelling"},
             "timestamp":"2026-08-07T00:00:00Z"}
            """);

        var state = await client.PauseMonitoringAsync(
            MemberId, new PauseMonitoringRequest { DurationHours = 24, Reason = "Travelling" });

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/v1/cardimembers/{MemberId}/pause", request.Uri!.AbsolutePath);
        Assert.Contains("24", request.Body);
        Assert.True(state.MonitoringPaused);
    }

    [Fact]
    public async Task ResumeMonitoring_DeletesPauseAndUnwrapsEnvelope()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"monitoringPaused":false},
             "timestamp":"2026-08-07T00:00:00Z"}
            """);

        var state = await client.ResumeMonitoringAsync(MemberId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/v1/cardimembers/{MemberId}/pause", request.Uri!.AbsolutePath);
        Assert.False(state.MonitoringPaused);
    }

    [Fact]
    public async Task DisconnectDevice_DeletesDeviceRoute()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.NoContent, "");

        await client.DisconnectDeviceAsync(MemberId, DeviceId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/v1/cardimembers/{MemberId}/devices/{DeviceId}", request.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task SetPrimaryDevice_PostsWithoutBody()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"deviceId":"7f9619ff-8b86-d011-b42d-00c04fc964ff",
             "provider":"fitbit","displayName":"Dad's Fitbit","status":"active","isPrimary":true,
             "scopes":["activity"],"todayUpdateCount":4},"timestamp":"2026-08-07T00:00:00Z"}
            """);

        var device = await client.SetPrimaryDeviceAsync(MemberId, DeviceId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/v1/cardimembers/{MemberId}/devices/{DeviceId}/primary", request.Uri!.AbsolutePath);
        Assert.True(string.IsNullOrEmpty(request.Body));
        Assert.True(device.IsPrimary);
        Assert.Equal(4, device.TodayUpdateCount);
    }

    [Fact]
    public async Task RefreshDeviceConnection_PostsToRefreshRoute()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"deviceId":"7f9619ff-8b86-d011-b42d-00c04fc964ff",
             "provider":"fitbit","displayName":"Dad's Fitbit","status":"active","scopes":[]},
             "timestamp":"2026-08-07T00:00:00Z"}
            """);

        await client.RefreshDeviceConnectionAsync(MemberId, DeviceId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/v1/cardimembers/{MemberId}/devices/{DeviceId}/refresh", request.Uri!.AbsolutePath);
    }
}
