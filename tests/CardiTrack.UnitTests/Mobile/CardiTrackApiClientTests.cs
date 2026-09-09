using System.Net;
using System.Text;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Offline;

namespace CardiTrack.UnitTests.Mobile;

public class CardiTrackApiClientTests
{
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
    public async Task GetAlertPreferences_UnwrapsClusters()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, $$"""
            {"success":true,"message":"ok","data":{"cardiMemberId":"{{MemberId}}",
             "clusters":[{"id":"sleep","title":"Sleep","description":"Bedtime",
             "rules":[{"id":"irregular_sleep","title":"Unusual sleep length",
             "description":"Last night","enabled":true,"isImplemented":true}]}]},
             "timestamp":"2026-08-14T00:00:00Z"}
            """);

        var prefs = await client.GetAlertPreferencesAsync(MemberId);

        Assert.Equal($"/api/v1/cardimembers/{MemberId}/alert-preferences", http.Requests.Single().Uri!.AbsolutePath);
        Assert.Equal(MemberId, prefs.CardiMemberId);
        Assert.Equal("irregular_sleep", prefs.Clusters.Single().Rules.Single().Id);
        Assert.True(prefs.Clusters.Single().Rules.Single().Enabled);
    }

    [Fact]
    public async Task SetAlertRuleEnabled_PatchesRuleId()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"id":"activity_decline","title":"Activity decline",
             "description":"Yesterday","enabled":false,"isImplemented":true},
             "timestamp":"2026-08-14T00:00:00Z"}
            """);

        var rule = await client.SetAlertRuleEnabledAsync(MemberId, "activity_decline", enabled: false);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal(
            $"/api/v1/cardimembers/{MemberId}/alert-preferences/rules/activity_decline",
            request.Uri!.AbsolutePath);
        Assert.Contains("false", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.False(rule.Enabled);
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

    private const string EmptyAlertPage = """
        {"success":true,"message":"ok","data":{"alerts":[],"total":0,"unreadCount":0},
         "timestamp":"2026-08-09T00:00:00Z"}
        """;

    [Fact]
    public async Task GetAlerts_WithoutFilters_HasNoQueryString()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, EmptyAlertPage);

        var page = await client.GetAlertsAsync();

        var request = http.Requests.Single();
        Assert.Equal("/api/v1/alerts", request.Uri!.AbsolutePath);
        Assert.Equal(string.Empty, request.Uri.Query);
        Assert.Empty(page.Alerts);
    }

    [Fact]
    public async Task GetAlerts_SendsFiltersAsQueryParameters()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, EmptyAlertPage);

        await client.GetAlertsAsync(
            severity: "red",
            status: "new",
            from: new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc),
            limit: 25);

        var query = Uri.UnescapeDataString(http.Requests.Single().Uri!.Query);
        Assert.Contains("severity=red", query);
        Assert.Contains("status=new", query);
        // Round-trip format, so the server sees the caller's offset rather than guessing it.
        Assert.Contains("from=2026-08-09T00:00:00.0000000Z", query);
        Assert.Contains("limit=25", query);
    }

    /// <summary>
    /// The member-scoped route rather than a <c>cardiMemberId</c> query on the collection: both
    /// exist on the API and apply the same access check, but the path is what the audit trail
    /// records as whose alerts were read.
    /// </summary>
    [Fact]
    public async Task GetAlerts_ForOneMember_UsesTheMemberScopedRoute()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, EmptyAlertPage);
        var memberId = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6");

        await client.GetAlertsAsync(cardiMemberId: memberId);

        var request = http.Requests.Single();
        Assert.Equal($"/api/v1/cardimembers/{memberId}/alerts", request.Uri!.AbsolutePath);
        Assert.Equal(string.Empty, request.Uri.Query);
    }

    /// <summary>
    /// Narrowing to one member is a second axis, not a replacement for the chips: the Alerts
    /// list keeps All/Unread/Critical/Today usable while a member chip is showing, so both have
    /// to survive onto the wire together.
    /// </summary>
    [Fact]
    public async Task GetAlerts_ForOneMember_KeepsTheOtherFilters()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, EmptyAlertPage);
        var memberId = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6");

        await client.GetAlertsAsync(status: "new", cardiMemberId: memberId);

        var request = http.Requests.Single();
        Assert.Equal($"/api/v1/cardimembers/{memberId}/alerts", request.Uri!.AbsolutePath);
        Assert.Contains("status=new", Uri.UnescapeDataString(request.Uri.Query));
    }

    [Fact]
    public async Task GetAlerts_UnwrapsTheAlertPage()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"alerts":[
              {"alertId":"7f9619ff-8b86-d011-b42d-00c04fc964ff","cardiMemberId":"3fa85f64-5717-4562-b3fc-2c963f66afa6",
               "cardiMemberName":"Margaret Doe","emergencyContactPhone":"+441234567891","type":"Heart Rate",
               "severity":"orange","status":"new","title":"Elevated Heart Rate","message":"Higher than usual.",
               "triggeredAt":"2026-08-09T07:00:00Z","aboutDate":"2026-08-09"}],
              "total":1,"unreadCount":1},"timestamp":"2026-08-09T00:00:00Z"}
            """);

        var page = await client.GetAlertsAsync();

        var alert = Assert.Single(page.Alerts);
        Assert.Equal("orange", alert.Severity);
        Assert.Equal("new", alert.Status);
        Assert.Equal("Margaret Doe", alert.CardiMemberName);
        Assert.Equal("+441234567891", alert.EmergencyContactPhone);
        Assert.Equal(new DateOnly(2026, 8, 9), alert.AboutDate);
        Assert.Equal(1, page.UnreadCount);
    }

    [Fact]
    public async Task GetAlert_GetsTheDetailRoute_AndUnwrapsTheChart()
    {
        var (client, http) = CreateSut();
        var alertId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, $$"""
            {"success":true,"message":"ok","data":{
              "alertId":"{{alertId}}","cardiMemberId":"3fa85f64-5717-4562-b3fc-2c963f66afa6",
              "cardiMemberName":"Margaret Doe","type":"Inactivity","rule":"activity_decline",
              "severity":"yellow","status":"new","title":"Quieter than usual","message":"Fewer steps.",
              "triggeredAt":"2026-08-14T07:00:00Z","aboutDate":"2026-08-13",
              "chart":{"metric":"steps","name":"Activity","unit":"steps","windowLabel":"Last 14 days",
                "value":2500,"baseline":5000,"series":[{"date":"2026-08-13","value":2500}]}
            },"timestamp":"2026-08-14T00:00:00Z"}
            """);

        var detail = await client.GetAlertAsync(alertId);

        Assert.Equal($"/api/v1/alerts/{alertId}", http.Requests.Single().Uri!.AbsolutePath);
        Assert.Equal("activity_decline", detail.Rule);
        Assert.Equal(new DateOnly(2026, 8, 13), detail.AboutDate);
        Assert.Equal("steps", detail.Chart!.Metric);
        Assert.Equal(2500, detail.Chart.Value);
        Assert.Equal("Last 14 days", detail.Chart.WindowLabel);
    }

    [Fact]
    public async Task AcknowledgeAlert_PostsToAcknowledgeRoute_WithNoBody()
    {
        var (client, http) = CreateSut();
        var alertId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, $$"""
            {"success":true,"message":"ok","data":{"alertId":"{{alertId}}","status":"acknowledged",
             "acknowledgedAt":"2026-08-09T08:00:00Z","unreadCount":2},"timestamp":"2026-08-09T00:00:00Z"}
            """);

        var result = await client.AcknowledgeAlertAsync(alertId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/v1/alerts/{alertId}/acknowledge", request.Uri!.AbsolutePath);
        Assert.True(string.IsNullOrEmpty(request.Body));
        Assert.Equal("acknowledged", result.Status);
        Assert.Equal(2, result.UnreadCount);
    }

    [Fact]
    public async Task UnacknowledgeAlert_DeletesTheAcknowledgement_NotTheAlert()
    {
        var (client, http) = CreateSut();
        var alertId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, $$"""
            {"success":true,"message":"ok","data":{"alertId":"{{alertId}}","status":"new",
             "acknowledgedAt":null,"unreadCount":3},"timestamp":"2026-08-14T00:00:00Z"}
            """);

        var result = await client.UnacknowledgeAlertAsync(alertId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        // The /acknowledge suffix is the whole distinction from DeleteAlertAsync, which removes
        // the alert itself — the two differ only by this segment.
        Assert.Equal($"/api/v1/alerts/{alertId}/acknowledge", request.Uri!.AbsolutePath);
        Assert.Equal("new", result.Status);
        Assert.Null(result.AcknowledgedAt);
        Assert.Equal(3, result.UnreadCount);
    }

    // ── Message-only command envelopes ──────────────────────────────────────────
    //
    // Commands that hand nothing back return `{ success, message, timestamp }` with no `data`
    // — BaseApiController.Success(string). The client used to run these through the envelope
    // reader, which rejects a null `data`, so a 200 surfaced to the user as "The server
    // returned an empty response." while the command had in fact succeeded.

    private const string MessageOnlyEnvelope = """
        {"success":true,"message":"Time zone updated.","timestamp":"2026-08-13T10:03:12Z"}
        """;

    private static readonly Guid NotificationId = Guid.Parse("8f9619ff-8b86-d011-b42d-00c04fc964ff");

    [Fact]
    public async Task UpdateTimeZone_AcceptsMessageOnlyEnvelope()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, MessageOnlyEnvelope);

        await client.UpdateTimeZoneAsync("Europe/London");

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/v1/users/me/timezone", request.Uri!.AbsolutePath);
        Assert.Contains("Europe/London", request.Body);
    }

    [Fact]
    public async Task MarkNotificationSeen_AcceptsMessageOnlyEnvelope()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, MessageOnlyEnvelope);

        await client.MarkNotificationSeenAsync(NotificationId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/v1/notifications/{NotificationId}/seen", request.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task DismissNotification_AcceptsMessageOnlyEnvelope_AndSendsAcknowledgement()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, MessageOnlyEnvelope);

        await client.DismissNotificationAsync(NotificationId, acknowledgedConsequence: true);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/v1/notifications/{NotificationId}/dismiss", request.Uri!.AbsolutePath);
        Assert.Contains("true", request.Body);
    }

    [Fact]
    public async Task RemoveNotificationMute_AcceptsMessageOnlyEnvelope()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, MessageOnlyEnvelope);

        await client.RemoveNotificationMuteAsync(NotificationId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/v1/notifications/mutes/{NotificationId}", request.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task ResetNotificationMutes_AcceptsMessageOnlyEnvelope()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, MessageOnlyEnvelope);

        await client.ResetNotificationMutesAsync();

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/notifications/mutes/reset", request.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task UnregisterPushDevice_AcceptsMessageOnlyEnvelope_AndSendsDeviceId()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, MessageOnlyEnvelope);

        await client.UnregisterPushDeviceAsync("device-42");

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/api/v1/notifications/devices", request.Uri!.AbsolutePath);
        Assert.Contains("device-42", request.Body);
    }

    [Fact]
    public async Task AckDelivered_AcceptsMessageOnlyEnvelope_AndSendsAckToken()
    {
        // The background push handler's ack. Throwing on a successful ack reads as a failed
        // delivery and would fire escalation for an alert that did arrive.
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, MessageOnlyEnvelope);

        await client.AckDeliveredAsync(NotificationId, "ack-token-abc");

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/v1/notifications/{NotificationId}/delivered", request.Uri!.AbsolutePath);
        Assert.Contains("ack-token-abc", request.Body);
    }

    [Fact]
    public async Task MessageOnlyCommand_StillThrows_OnNonSuccessStatus()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.BadRequest, """
            {"success":false,"message":"'Mars/Olympus' isn't a time zone we recognise.",
             "timestamp":"2026-08-13T10:03:12Z"}
            """);

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.UpdateTimeZoneAsync("Mars/Olympus"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("'Mars/Olympus' isn't a time zone we recognise.", ex.Message);
    }

    [Fact]
    public async Task MessageOnlyCommand_StillFlagsSessionExpiry()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.Unauthorized, "");

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.ResetNotificationMutesAsync());

        Assert.True(ex.IsSessionExpired);
    }

    [Fact]
    public async Task Get_WritesTheEnvelopeToTheOfflineCache()
    {
        var cache = new MemoryOfflineCache();
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"hasOrganization":true,"hasUserAccount":true,
             "hasCardiMember":true,"currentStep":7,"totalSteps":7},"timestamp":"2026-08-01T00:00:00Z"}
            """);

        var call = client.GetOnboardingStatusAsync();
        await call;

        Assert.False(client.OriginOf(call)!.WasCached);
        Assert.True(cache.Items.ContainsKey("api/Onboarding/status"));
    }

    [Fact]
    public async Task PeekAlerts_ReturnsThePageTheLiveCallSaved_WithoutTouchingTheApi()
    {
        var cache = new MemoryOfflineCache();
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"alerts":[],"total":3,"unreadCount":1},
             "timestamp":"2026-08-01T00:00:00Z"}
            """);
        await client.GetAlertsAsync(severity: "red", cardiMemberId: null);
        var requestsAfterLiveCall = http.Requests.Count;

        // Same arguments, so the same key — the peek must answer the exact question the
        // screen is about to ask, never a neighbouring filter's page.
        var saved = await client.PeekAlertsAsync(severity: "red", cardiMemberId: null);

        Assert.NotNull(saved);
        Assert.Equal(3, saved!.Total);
        Assert.Equal(1, saved.UnreadCount);
        Assert.Equal(requestsAfterLiveCall, http.Requests.Count);
    }

    [Fact]
    public async Task PeekAlerts_ReturnsNull_ForAQueryTheDeviceHasNeverAnswered()
    {
        var cache = new MemoryOfflineCache();
        var (client, http) = CreateSut(cache);

        var saved = await client.PeekAlertsAsync(severity: "red");

        Assert.Null(saved);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task PeekAlerts_ReturnsNull_WhenTheClientHasNoCache()
    {
        var (client, http) = CreateSut(cache: null);

        Assert.Null(await client.PeekAlertsAsync());
        Assert.Empty(http.Requests);
    }

    private const string OnboardingEnvelope =
        """{"success":true,"message":"ok","data":{"hasOrganization":true,"hasUserAccount":true,"hasCardiMember":true,"currentStep":7,"totalSteps":7},"timestamp":"2026-08-01T00:00:00Z"}""";

    [Fact]
    public async Task Get_ServesTheCachedEnvelope_WhenTheCallCannotReachTheApi()
    {
        var cache = new MemoryOfflineCache();
        cache.Items["api/Onboarding/status"] = new OfflineCacheEntry(
            OnboardingEnvelope, DateTimeOffset.UtcNow.AddMinutes(-12));
        var (client, http) = CreateSut(cache);
        http.Throws(new HttpRequestException("offline"));

        var call = client.GetOnboardingStatusAsync();
        var status = await call;

        Assert.True(status.HasCardiMember);
        Assert.True(client.OriginOf(call)!.WasCached);
        Assert.Equal(DateTimeOffset.UtcNow.AddMinutes(-12).ToUnixTimeSeconds(),
            client.OriginOf(call)!.CachedAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Get_DoesNotServeCache_WhenTheCallerCancelled()
    {
        var cache = new MemoryOfflineCache();
        cache.Items["api/Onboarding/status"] = new OfflineCacheEntry(
            """{"success":true,"message":"ok","data":{"hasUserAccount":true},"timestamp":"2026-08-01T00:00:00Z"}""",
            DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        http.Throws(new TaskCanceledException());

        var call = client.GetOnboardingStatusAsync(cts.Token);
        var ex = await Assert.ThrowsAsync<ApiException>(() => call);

        Assert.True(ex.IsNetworkFailure);
        Assert.False(client.OriginOf(call)!.WasCached);
    }

    [Fact]
    public async Task Get_Throws_WhenOfflineAndNothingIsCached()
    {
        var (client, http) = CreateSut(new MemoryOfflineCache());
        http.Throws(new HttpRequestException("offline"));

        var call = client.GetOnboardingStatusAsync();
        var ex = await Assert.ThrowsAsync<ApiException>(() => call);

        Assert.True(ex.IsNetworkFailure);
        Assert.False(client.OriginOf(call)!.WasCached);
        Assert.Equal("No connection. Check your internet and try again.", ex.Message);
    }

    [Fact]
    public async Task Get_DoesNotFallBackToCache_OnHttpError()
    {
        var cache = new MemoryOfflineCache();
        cache.Items["api/Onboarding/status"] = new OfflineCacheEntry(
            """{"success":true,"message":"ok","data":{"hasUserAccount":true},"timestamp":"2026-08-01T00:00:00Z"}""",
            DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.Unauthorized, """
            {"success":false,"message":"expired","timestamp":"2026-08-01T00:00:00Z"}
            """);

        var call = client.GetOnboardingStatusAsync();
        var ex = await Assert.ThrowsAsync<ApiException>(() => call);

        Assert.True(ex.IsSessionExpired);
        Assert.False(client.OriginOf(call)!.WasCached);
    }

    /// <summary>
    /// The reason the origin is per call. Two GETs are in flight on the one client — five screens
    /// refresh together when the app resumes, and a single screen can start two at once — and one
    /// falls back to the cache while the other reaches the API. Read off the client as a whole,
    /// whichever finished last answered for both, which put the offline banner over data that had
    /// just arrived fresh and took it down over data that had not.
    /// </summary>
    [Fact]
    public async Task Origin_IsPerCall_WhenOneGetIsCachedAndAnotherIsLive()
    {
        var cache = new MemoryOfflineCache();
        cache.Items["api/Onboarding/status"] = new OfflineCacheEntry(
            OnboardingEnvelope, DateTimeOffset.UtcNow.AddMinutes(-12));
        var (client, http) = CreateSut(cache);

        // The cached path first, so the live call is the one that finishes last — the ordering
        // that used to leave the cached call reporting itself as fresh.
        http.Throws(new HttpRequestException("offline"));
        var cached = client.GetOnboardingStatusAsync();
        await cached;

        http.Enqueue(HttpStatusCode.OK, OnboardingEnvelope);
        var live = client.GetOnboardingStatusAsync();
        await live;

        Assert.True(client.OriginOf(cached)!.WasCached);
        Assert.False(client.OriginOf(live)!.WasCached);
    }

    [Fact]
    public void OriginOf_IsNull_ForATaskThisClientDidNotProduce()
    {
        var (client, _) = CreateSut();

        Assert.Null(client.OriginOf(Task.FromResult(0)));
    }

    // ── Cache-only peeks ────────────────────────────────────────────────────────
    //
    // Every read screen puts the device's saved answer up before the live call returns. A peek
    // must answer exactly the question its Get twin asks — the same arguments, the same key —
    // and must say when that answer was saved, because the screen has to say so out loud.

    private const string EmptyObjectEnvelope =
        """{"success":true,"message":"ok","data":{},"timestamp":"2026-08-01T00:00:00Z"}""";

    private const string EmptyListEnvelope =
        """{"success":true,"message":"ok","data":[],"timestamp":"2026-08-01T00:00:00Z"}""";

    [Fact]
    public async Task PeekDashboard_ReturnsWhatTheLiveCallSaved_WithoutTouchingTheApi()
    {
        var cache = new MemoryOfflineCache();
        var (client, http) = CreateSut(cache);
        var memberId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"cardiMemberId":"%ID%","cardiMemberName":"Dad"},
             "timestamp":"2026-08-01T00:00:00Z"}
            """.Replace("%ID%", memberId.ToString()));
        await client.GetDashboardAsync(memberId);
        var requestsAfterLiveCall = http.Requests.Count;

        var saved = await client.PeekDashboardAsync(memberId);

        Assert.NotNull(saved);
        Assert.Equal(memberId, saved!.CardiMemberId);
        Assert.Equal(requestsAfterLiveCall, http.Requests.Count);
    }

    [Fact]
    public async Task Peek_FilesTheSnapshotsDate_UnderThePeekTask()
    {
        var cache = new MemoryOfflineCache();
        var savedAt = DateTimeOffset.UtcNow.AddHours(-3);
        cache.Items["api/v1/notifications/summary"] = new OfflineCacheEntry(EmptyObjectEnvelope, savedAt);
        var (client, _) = CreateSut(cache);

        var peek = client.PeekNotificationSummaryAsync();
        await peek;

        var origin = client.OriginOf(peek);
        Assert.NotNull(origin);
        Assert.True(origin!.WasCached);
        Assert.Equal(savedAt, origin.CachedAt);
    }

    [Fact]
    public async Task Peek_FilesAnOriginWithNoDate_WhenThereIsNoSnapshot()
    {
        var (client, http) = CreateSut(new MemoryOfflineCache());

        var peek = client.PeekNotificationSummaryAsync();
        var saved = await peek;

        Assert.Null(saved);
        Assert.Empty(http.Requests);
        var origin = client.OriginOf(peek);
        Assert.NotNull(origin);
        Assert.False(origin!.WasCached);
    }

    [Fact]
    public async Task PeekAlerts_StillReportsItsOrigin_NowThatItSharesThePathBuilder()
    {
        var cache = new MemoryOfflineCache();
        var savedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        cache.Items["api/v1/alerts?severity=red"] = new OfflineCacheEntry(
            """{"success":true,"message":"ok","data":{"alerts":[],"total":0,"unreadCount":0},"timestamp":"2026-08-01T00:00:00Z"}""",
            savedAt);
        var (client, _) = CreateSut(cache);

        var peek = client.PeekAlertsAsync(severity: "red");
        Assert.NotNull(await peek);
        Assert.Equal(savedAt, client.OriginOf(peek)!.CachedAt);
    }

    /// <summary>
    /// Every peek and its live twin, called with the same arguments, must agree on the key. One
    /// case per pair rather than one test per pair: the list is the assertion, and a new GET that
    /// gains a peek is added here or the peek is not covered.
    /// </summary>
    [Fact]
    public async Task EveryPeek_UsesTheSameKeyAsItsLiveTwin()
    {
        var memberId = Guid.NewGuid();
        var alertId = Guid.NewGuid();
        var day = new DateOnly(2026, 8, 1);
        var digestEnvelope = """
            {"success":true,"message":"ok","data":{"cardiMemberId":"%M%","localDate":"2026-08-01","audience":"daybook",
             "text":"Fine.","generatedAtUtc":"2026-08-01T22:00:00Z"},"timestamp":"2026-08-01T00:00:00Z"}
            """.Replace("%M%", memberId.ToString());
        var digestListEnvelope = digestEnvelope.Replace("\"data\":{", "\"data\":[{").Replace("},\"timestamp\"", "}],\"timestamp\"");
        var adviseEnvelope = """
            {"success":true,"message":"ok","data":{"cardiMemberId":"%M%","summary":"s","suggestion":"t",
             "generatedAt":"2026-08-01T22:00:00Z"},"timestamp":"2026-08-01T00:00:00Z"}
            """.Replace("%M%", memberId.ToString());

        var pairs = new (string Name, string Envelope, Func<ICardiTrackApiClient, Task> Live, Func<ICardiTrackApiClient, Task<object?>> Peek)[]
        {
            ("CardiMembers", EmptyListEnvelope, api => api.GetCardiMembersAsync(), async api => await api.PeekCardiMembersAsync()),
            ("CardiMember", EmptyObjectEnvelope, api => api.GetCardiMemberAsync(memberId), async api => await api.PeekCardiMemberAsync(memberId)),
            ("Dashboard", EmptyObjectEnvelope, api => api.GetDashboardAsync(memberId), async api => await api.PeekDashboardAsync(memberId)),
            ("Alert", EmptyObjectEnvelope, api => api.GetAlertAsync(alertId), async api => await api.PeekAlertAsync(alertId)),
            ("Alerts", EmptyObjectEnvelope,
                api => api.GetAlertsAsync("red", "new", cardiMemberId: memberId),
                async api => await api.PeekAlertsAsync("red", "new", cardiMemberId: memberId)),
            ("Digest", digestEnvelope, api => api.GetDigestAsync(memberId), async api => await api.PeekDigestAsync(memberId)),
            ("Advise", adviseEnvelope, api => api.GetAdviseAsync(memberId), async api => await api.PeekAdviseAsync(memberId)),
            ("JournalEntries", digestListEnvelope,
                api => api.GetJournalEntriesAsync(memberId, JournalCadence.Weekbook, 30, "sleep", day, "watch"),
                async api => await api.PeekJournalEntriesAsync(memberId, JournalCadence.Weekbook, 30, "sleep", day, "watch")),
            ("JournalEntry", digestEnvelope,
                api => api.GetJournalEntryAsync(memberId, JournalCadence.Daybook, day),
                async api => await api.PeekJournalEntryAsync(memberId, JournalCadence.Daybook, day)),
            ("Questionnaires", EmptyObjectEnvelope,
                api => api.GetQuestionnairesAsync(memberId, "walk", 2, 10),
                async api => await api.PeekQuestionnairesAsync(memberId, "walk", 2, 10)),
            ("Devices", EmptyObjectEnvelope, api => api.GetDevicesAsync(memberId), async api => await api.PeekDevicesAsync(memberId)),
            ("Notifications", EmptyObjectEnvelope,
                api => api.GetNotificationsAsync("Open", "safety", true, 5),
                async api => await api.PeekNotificationsAsync("Open", "safety", true, 5)),
            ("NotificationSummary", EmptyObjectEnvelope, api => api.GetNotificationSummaryAsync(), async api => await api.PeekNotificationSummaryAsync()),
            ("AlertPreferences", EmptyObjectEnvelope, api => api.GetAlertPreferencesAsync(memberId), async api => await api.PeekAlertPreferencesAsync(memberId)),
            ("MemberAlarms", EmptyListEnvelope, api => api.GetMemberAlarmsAsync(memberId), async api => await api.PeekMemberAlarmsAsync(memberId)),
            ("AlarmCatalogue", EmptyObjectEnvelope, api => api.GetAlarmCatalogueAsync(), async api => await api.PeekAlarmCatalogueAsync()),
            ("JournalSettings", EmptyObjectEnvelope, api => api.GetJournalSettingsAsync(memberId), async api => await api.PeekJournalSettingsAsync(memberId)),
            ("NotificationPreferences", EmptyObjectEnvelope, api => api.GetNotificationPreferencesAsync(), async api => await api.PeekNotificationPreferencesAsync()),
            ("NotificationMutes", EmptyListEnvelope, api => api.GetNotificationMutesAsync(), async api => await api.PeekNotificationMutesAsync()),
        };

        foreach (var (name, envelope, live, peek) in pairs)
        {
            var cache = new MemoryOfflineCache();
            var (client, http) = CreateSut(cache);
            http.Enqueue(HttpStatusCode.OK, envelope);

            await live(client);
            Assert.True(cache.Items.Count == 1, $"{name}: the live call should have saved exactly one entry");
            var requests = http.Requests.Count;

            var saved = await peek(client);

            Assert.True(saved is not null, $"{name}: the peek did not find what the live call saved — the keys differ");
            Assert.True(http.Requests.Count == requests, $"{name}: the peek touched the network");
        }
    }

    // ── Eviction ────────────────────────────────────────────────────────────────
    //
    // A mutation that succeeded makes some saved answers wrong. The client drops the ones it can
    // name, so the next landing's peek cannot put a pre-edit profile or a deleted alarm on the wall.

    [Fact]
    public async Task Get_EvictsTheKey_On404()
    {
        var cache = new MemoryOfflineCache();
        var memberId = Guid.NewGuid();
        cache.Items[$"api/v1/cardimembers/{memberId}/dashboard"] = new OfflineCacheEntry(EmptyObjectEnvelope, DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.NotFound, """{"success":false,"message":"gone","timestamp":"2026-08-01T00:00:00Z"}""");

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.GetDashboardAsync(memberId));

        Assert.True(ex.IsNotFound);
        Assert.DoesNotContain($"api/v1/cardimembers/{memberId}/dashboard", cache.Items.Keys);
    }

    [Fact]
    public async Task UpdateCardiMember_EvictsTheProfileDashboardAndMemberList()
    {
        var cache = new MemoryOfflineCache();
        var memberId = Guid.NewGuid();
        var untouched = $"api/v1/cardimembers/{memberId}/alarms";
        foreach (var key in new[]
                 {
                     $"api/v1/cardimembers/{memberId}",
                     $"api/v1/cardimembers/{memberId}/dashboard",
                     "api/Onboarding/cardimembers",
                     untouched,
                 })
            cache.Items[key] = new OfflineCacheEntry(EmptyObjectEnvelope, DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, EmptyObjectEnvelope);

        await client.UpdateCardiMemberAsync(memberId, new UpdateCardiMemberRequest());

        Assert.Equal([untouched], cache.Items.Keys);
    }

    [Fact]
    public async Task RemoveCardiMember_EvictsEveryKeyItCanName()
    {
        var cache = new MemoryOfflineCache();
        var memberId = Guid.NewGuid();
        var otherMember = Guid.NewGuid();
        var expectedGone = new[]
        {
            $"api/v1/cardimembers/{memberId}",
            $"api/v1/cardimembers/{memberId}/dashboard",
            $"api/v1/cardimembers/{memberId}/devices",
            $"api/v1/cardimembers/{memberId}/alert-preferences",
            $"api/v1/cardimembers/{memberId}/alarms",
            $"api/v1/cardimembers/{memberId}/journal-settings",
            $"api/v1/insights/members/{memberId}/status",
            $"api/v1/insights/members/{memberId}/digest",
            $"api/v1/insights/members/{memberId}/advise",
            $"api/v1/cardimembers/{memberId}/questionnaires?page=1&pageSize=20",
            $"api/v1/cardimembers/{memberId}/alerts",
            "api/Onboarding/cardimembers",
            "api/v1/notifications/summary",
        };
        var kept = $"api/v1/cardimembers/{otherMember}/dashboard";
        foreach (var key in expectedGone.Append(kept))
            cache.Items[key] = new OfflineCacheEntry(EmptyObjectEnvelope, DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.NoContent, "");

        await client.RemoveCardiMemberAsync(memberId);

        Assert.Equal([kept], cache.Items.Keys);
    }

    [Fact]
    public async Task SaveMemberAlarm_EvictsTheMembersAlarmList()
    {
        var cache = new MemoryOfflineCache();
        var memberId = Guid.NewGuid();
        cache.Items[$"api/v1/cardimembers/{memberId}/alarms"] = new OfflineCacheEntry(EmptyListEnvelope, DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, EmptyObjectEnvelope);

        await client.SaveMemberAlarmAsync(memberId, Guid.NewGuid(), new SaveMetricAlarmRequest());

        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task AnswerQuestionnaire_EvictsUsingTheMemberIdFromTheResponse()
    {
        var cache = new MemoryOfflineCache();
        var memberId = Guid.NewGuid();
        foreach (var key in new[]
                 {
                     $"api/v1/cardimembers/{memberId}/questionnaires?page=1&pageSize=20",
                     $"api/v1/cardimembers/{memberId}/dashboard",
                     $"api/v1/cardimembers/{memberId}",
                 })
            cache.Items[key] = new OfflineCacheEntry(EmptyObjectEnvelope, DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"id":"%Q%","cardiMemberId":"%M%"},"timestamp":"2026-08-01T00:00:00Z"}
            """.Replace("%Q%", Guid.NewGuid().ToString()).Replace("%M%", memberId.ToString()));

        await client.AnswerQuestionnaireAsync(Guid.NewGuid(), new AnswerQuestionnaireRequest());

        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task DismissNotification_EvictsTheOpenInboxSummaryAndMutes()
    {
        var cache = new MemoryOfflineCache();
        foreach (var key in new[]
                 {
                     "api/v1/notifications?state=Open",
                     "api/v1/notifications/summary",
                     "api/v1/notifications/mutes",
                 })
            cache.Items[key] = new OfflineCacheEntry(EmptyObjectEnvelope, DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.NoContent, "");

        await client.DismissNotificationAsync(Guid.NewGuid());

        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task UpdateNotificationPreferences_EvictsThePreferences()
    {
        var cache = new MemoryOfflineCache();
        cache.Items["api/v1/notifications/preferences"] = new OfflineCacheEntry(EmptyObjectEnvelope, DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, EmptyObjectEnvelope);

        await client.UpdateNotificationPreferencesAsync(new UpdateNotificationPreferenceRequest());

        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task Mutation_DoesNotEvict_WhenTheServerRefused()
    {
        var cache = new MemoryOfflineCache();
        var memberId = Guid.NewGuid();
        cache.Items[$"api/v1/cardimembers/{memberId}"] = new OfflineCacheEntry(EmptyObjectEnvelope, DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.BadRequest, """{"success":false,"message":"no","timestamp":"2026-08-01T00:00:00Z"}""");

        await Assert.ThrowsAsync<ApiException>(() =>
            client.UpdateCardiMemberAsync(memberId, new UpdateCardiMemberRequest()));

        Assert.Single(cache.Items);
    }

    [Fact]
    public async Task Eviction_IsBestEffort_WhenTheCacheCannotDelete()
    {
        var cache = new MemoryOfflineCache { RemoveThrows = new IOException("disk") };
        var memberId = Guid.NewGuid();
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, EmptyObjectEnvelope);

        // The server has already changed; a cache that cannot forget must not make the save
        // look as though it failed.
        var updated = await client.UpdateCardiMemberAsync(memberId, new UpdateCardiMemberRequest());

        Assert.NotNull(updated);
    }

    // ── Member chat ─────────────────────────────────────────────────────────────
    //
    // sessions/current is the one read whose "nothing there" is a 200 with a null `data` —
    // MemberChatController documents it as "not a 404, since the member itself may well exist".
    // The envelope reader used to reject that as "The server returned an empty response.",
    // which put the chat sheet's error panel over every first-ever open.

    [Fact]
    public async Task GetCurrentMemberChatSession_ReturnsNull_WhenNoActiveSessionExists()
    {
        var (client, http) = CreateSut();
        var memberId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"Here you go!","data":null,"timestamp":"2026-08-20T15:48:00Z"}
            """);

        var history = await client.GetCurrentMemberChatSessionAsync(memberId);

        Assert.Null(history);
        Assert.Equal($"/api/v1/member-chat/members/{memberId}/sessions/current",
            http.Requests.Single().Uri!.AbsolutePath);
    }

    [Fact]
    public async Task GetCurrentMemberChatSession_DoesNotCacheANullSession()
    {
        // The cache reader treats a stored envelope with a null `data` as unreadable and warns
        // on every offline read — nothing worth serving offline, so nothing gets written.
        var cache = new MemoryOfflineCache();
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":null,"timestamp":"2026-08-20T15:48:00Z"}
            """);

        var history = await client.GetCurrentMemberChatSessionAsync(Guid.NewGuid());

        Assert.Null(history);
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task GetCurrentMemberChatSession_ReturnsTurns_WhenASessionExists()
    {
        var (client, http) = CreateSut();
        var memberId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"sessionId":"6f9619ff-8b86-d011-b42d-00c04fc964ff",
             "turns":[{"role":"User","content":"How did Dad sleep?","createdAtUtc":"2026-08-20T15:00:00Z"},
                      {"role":"Assistant","content":"About as usual.","createdAtUtc":"2026-08-20T15:01:00Z"}]},
             "timestamp":"2026-08-20T15:48:00Z"}
            """);

        var history = await client.GetCurrentMemberChatSessionAsync(memberId);

        Assert.NotNull(history);
        Assert.Equal(2, history!.Turns.Count);
        Assert.Equal("User", history.Turns[0].Role);
        Assert.Equal("About as usual.", history.Turns[1].Content);
    }

    /// <summary>
    /// The chips fail silently on the page — a caregiver who never sees them can still type — so
    /// a wrong route or a renamed envelope field would show up as nothing at all rather than as
    /// an error. This is the only place that difference is visible.
    /// </summary>
    [Fact]
    public async Task GetMemberChatSuggestions_PostsToRoute_AndReadsTheEnvelope()
    {
        var (client, http) = CreateSut();
        var memberId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"suggestions":[
              "What's behind the current alert?","How are they doing today?",
              "How did they sleep last night?","How active have they been this week?"]},
             "timestamp":"2026-08-21T09:00:00Z"}
            """);

        var response = await client.GetMemberChatSuggestionsAsync(memberId);

        Assert.Equal(4, response.Suggestions.Count);
        Assert.Equal("What's behind the current alert?", response.Suggestions[0]);
        Assert.Equal($"/api/v1/member-chat/members/{memberId}/suggestions",
            http.Requests.Single().Uri!.AbsolutePath);
    }

    [Fact]
    public async Task GetMemberChatSessions_ReadsTheHistoryList_FromItsRoute()
    {
        var (client, http) = CreateSut();
        var memberId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"sessions":[
              {"sessionId":"6f9619ff-8b86-d011-b42d-00c04fc964ff",
               "startedAtUtc":"2026-08-22T10:00:00Z","lastTurnAtUtc":"2026-08-22T10:12:00Z",
               "theme":"Alerts and heart rate","firstQuestion":"Any alerts today?","questionCount":1},
              {"sessionId":"7f9619ff-8b86-d011-b42d-00c04fc964ff",
               "startedAtUtc":"2026-08-20T15:00:00Z","lastTurnAtUtc":"2026-08-20T15:06:00Z",
               "theme":null,"firstQuestion":"How did Dad sleep?","questionCount":3}]},
             "timestamp":"2026-08-22T11:00:00Z"}
            """);

        var response = await client.GetMemberChatSessionsAsync(memberId);

        Assert.Equal(2, response.Sessions.Count);
        Assert.Equal("Alerts and heart rate", response.Sessions[0].Theme);
        Assert.Equal("Any alerts today?", response.Sessions[0].FirstQuestion);
        Assert.Null(response.Sessions[1].Theme);
        Assert.Equal(3, response.Sessions[1].QuestionCount);
        Assert.Equal($"/api/v1/member-chat/members/{memberId}/sessions",
            http.Requests.Single().Uri!.AbsolutePath);
    }

    [Fact]
    public async Task EndCurrentMemberChatSession_PostsToItsRoute_AndReadsTheEndedId()
    {
        var (client, http) = CreateSut();
        var memberId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"endedSessionId":"6f9619ff-8b86-d011-b42d-00c04fc964ff"},
             "timestamp":"2026-08-24T09:00:00Z"}
            """);

        var response = await client.EndCurrentMemberChatSessionAsync(memberId);

        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), response.EndedSessionId);
        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/v1/member-chat/members/{memberId}/sessions/current/end",
            request.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task ContinueMemberChatSession_PostsToItsRoute_AndReadsTheTurns()
    {
        var (client, http) = CreateSut();
        var memberId = Guid.NewGuid();
        var sessionId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"sessionId":"6f9619ff-8b86-d011-b42d-00c04fc964ff",
             "turns":[{"role":"User","content":"How did Dad sleep?","createdAtUtc":"2026-08-20T15:00:00Z"}]},
             "timestamp":"2026-08-24T09:00:00Z"}
            """);

        var history = await client.ContinueMemberChatSessionAsync(memberId, sessionId);

        Assert.Equal(sessionId, history.SessionId);
        Assert.Single(history.Turns);
        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/v1/member-chat/members/{memberId}/sessions/{sessionId}/continue",
            request.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task GetMemberChatSession_ReadsOnePastConversation_FromItsRoute()
    {
        var (client, http) = CreateSut();
        var memberId = Guid.NewGuid();
        var sessionId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"sessionId":"6f9619ff-8b86-d011-b42d-00c04fc964ff",
             "turns":[{"role":"User","content":"How did Dad sleep?","createdAtUtc":"2026-08-20T15:00:00Z"},
                      {"role":"Assistant","content":"About as usual.","createdAtUtc":"2026-08-20T15:01:00Z"}]},
             "timestamp":"2026-08-22T11:00:00Z"}
            """);

        var history = await client.GetMemberChatSessionAsync(memberId, sessionId);

        Assert.Equal(sessionId, history.SessionId);
        Assert.Equal(2, history.Turns.Count);
        Assert.Equal($"/api/v1/member-chat/members/{memberId}/sessions/{sessionId}",
            http.Requests.Single().Uri!.AbsolutePath);
    }

    [Fact]
    public async Task GetCurrentMemberChatSession_StillThrows_WhenTheBodyIsUnreadable()
    {
        // Tolerating a null `data` must not extend to tolerating garbage — an HTML error page
        // from a proxy is a failure, not an empty conversation.
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, "<!doctype html>upstream had a bad day");

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => client.GetCurrentMemberChatSessionAsync(Guid.NewGuid()));

        Assert.Equal("The server returned an empty response.", ex.Message);
    }

    [Fact]
    public async Task OrdinaryGet_StillRejectsANullData()
    {
        // The opt-in stays an opt-in: for every other endpoint a success envelope with no data
        // is a server fault, exactly as before.
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":null,"timestamp":"2026-08-20T15:48:00Z"}
            """);

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.GetDashboardAsync(Guid.NewGuid()));

        Assert.Equal("The server returned an empty response.", ex.Message);
    }

    [Fact]
    public async Task SendMemberChatMessage_PostsToRoute_AndAsksForTheExtendedTimeout()
    {
        var (client, http) = CreateSut();
        var memberId = Guid.NewGuid();
        TimeSpan? requestedTimeout = null;
        http.Enqueue(request =>
        {
            requestedTimeout = request.Options.TryGetValue(TimeoutHandler.TimeoutOption, out var t)
                ? t : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"success":true,"message":"ok","data":{"sessionId":"6f9619ff-8b86-d011-b42d-00c04fc964ff",
                     "reply":"Steady night.","charts":[],"generatedAt":"2026-08-20T15:50:00Z"},
                     "timestamp":"2026-08-20T15:50:00Z"}
                    """, Encoding.UTF8, "application/json"),
            };
        });

        var response = await client.SendMemberChatMessageAsync(
            memberId, new MemberChatMessageRequest { Message = "How did Dad sleep?" });

        Assert.Equal("Steady night.", response.Reply);
        Assert.Equal($"/api/v1/member-chat/members/{memberId}/messages",
            http.Requests.Single().Uri!.AbsolutePath);
        // The reply is a chain of CPU-served model calls; the client-wide default would hang
        // up on a legitimately slow answer. See CardiTrackApiClient.MemberChatSendTimeout.
        Assert.Equal(TimeSpan.FromSeconds(960), requestedTimeout);
    }

    [Fact]
    public async Task PrepareAssistant_PostsToTheAssistantRoute_AndReadsNothingBack()
    {
        var (client, http) = CreateSut();
        // 202 with a message-only envelope: the API has noted the arrival, and whether it went
        // on to load the model is not something the app can act on either way.
        http.Enqueue(HttpStatusCode.Accepted, """
            {"success":true,"message":"Getting the assistant ready.","timestamp":"2026-08-22T09:00:00Z"}
            """);

        await client.PrepareAssistantAsync();

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/assistant/prepare", request.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task RequestHistoryRepull_PostsTheDayCount_AndUnwrapsThe202Envelope()
    {
        var (client, http) = CreateSut();
        var memberId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.Accepted, """
            {"success":true,"message":"On it!","data":{"repullId":"8c1f5f64-5717-4562-b3fc-2c963f66afa6",
             "status":"pending","days":30,"fromDate":"2026-08-10","toDate":"2026-09-08","daysDone":0,
             "daysWithData":0,"requestedAt":"2026-09-09T12:00:00Z"},"timestamp":"2026-09-09T12:00:00Z"}
            """);

        var repull = await client.RequestHistoryRepullAsync(memberId, deviceId, 30);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/v1/cardimembers/{memberId}/devices/{deviceId}/history-repull", request.Uri!.AbsolutePath);
        Assert.Contains("\"days\":30", request.Body);
        Assert.Equal("pending", repull.Status);
        Assert.Equal(30, repull.Days);
        Assert.Equal(new DateOnly(2026, 8, 10), repull.FromDate);
    }

    private static (CardiTrackApiClient Client, FakeHttpMessageHandler Http) CreateSut(
        IOfflineReadCache? cache = null)
    {
        var http = new FakeHttpMessageHandler();
        var client = new CardiTrackApiClient(
            new HttpClient(http) { BaseAddress = new Uri("https://api.test") }, cache);
        return (client, http);
    }

}
