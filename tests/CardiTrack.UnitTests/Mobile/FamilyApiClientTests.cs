using System.Net;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Offline;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The family half of the client: routes, verbs, bodies, and which saved answers a mutation
/// throws away. The shapes are the server's own DTOs, so the envelope tests stay thin.
/// </summary>
public class FamilyApiClientTests
{
    private const string EmptyListEnvelope =
        """{"success":true,"message":"ok","data":[],"timestamp":"2026-09-22T00:00:00Z"}""";

    private const string MessageOnlyEnvelope =
        """{"success":true,"message":"done","timestamp":"2026-09-22T00:00:00Z"}""";

    [Fact]
    public async Task GetMyFamilies_ReadsTheMineRoute_AndCachesIt()
    {
        var cache = new MemoryOfflineCache();
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":[{"organizationId":"6f9619ff-8b86-d011-b42d-00c04fc964ff",
             "name":"The Does","role":"admin","isHomeFamily":true,"memberCount":2,"watchedMemberNames":["Margaret Doe"]}],
             "timestamp":"2026-09-22T00:00:00Z"}
            """);

        var families = await client.GetMyFamiliesAsync();

        Assert.Equal("/api/v1/families/mine", http.Requests.Single().Uri!.AbsolutePath);
        Assert.Single(families);
        Assert.Equal("admin", families[0].Role);
        Assert.True(cache.Items.ContainsKey("api/v1/families/mine"));

        var peeked = await client.PeekMyFamiliesAsync();
        Assert.NotNull(peeked);
        Assert.Equal("The Does", peeked![0].Name);
    }

    [Fact]
    public async Task RequestToJoin_PostsTheFamilyId_AndForgetsTheSavedAskList()
    {
        var cache = new MemoryOfflineCache();
        cache.Items["api/v1/families/join-requests/mine"] = new OfflineCacheEntry("[]", DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK,
            """{"success":true,"message":"asked","data":{"requestId":null,"expiresAt":null},"timestamp":"2026-09-22T00:00:00Z"}""");

        var receipt = await client.RequestToJoinFamilyAsync("KTR7M2Q9");

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/families/join-requests", request.Uri!.AbsolutePath);
        Assert.Contains("KTR7M2Q9", request.Body);
        // The deliberately uninformative receipt still unwraps.
        Assert.Null(receipt.RequestId);
        Assert.False(cache.Items.ContainsKey("api/v1/families/join-requests/mine"));
    }

    [Fact]
    public async Task Approve_PostsTheDecision_AndEvictsQueueRosterAndFamilies()
    {
        var org = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var member = Guid.NewGuid();
        var cache = new MemoryOfflineCache();
        foreach (var key in new[] { $"api/v1/families/{org}/join-requests", $"api/v1/families/{org}/members", "api/v1/families/mine" })
            cache.Items[key] = new OfflineCacheEntry("[]", DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, MessageOnlyEnvelope);

        await client.ApproveJoinRequestAsync(org, requestId, new ApproveJoinRequest
        {
            CardiMemberIds = [member],
            Role = "member",
            ReceiveAlerts = true,
        });

        var request = http.Requests.Single();
        Assert.Equal($"/api/v1/families/{org}/join-requests/{requestId}/approve", request.Uri!.AbsolutePath);
        Assert.Contains(member.ToString(), request.Body);
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task Leave_DeletesMe_AndForgetsTheMembersThatFamilyLetMeSee()
    {
        var org = Guid.NewGuid();
        var cache = new MemoryOfflineCache();
        cache.Items["api/Onboarding/cardimembers"] = new OfflineCacheEntry("[]", DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, MessageOnlyEnvelope);

        await client.LeaveFamilyAsync(org);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/v1/families/{org}/members/me", request.Uri!.AbsolutePath);
        Assert.False(cache.Items.ContainsKey("api/Onboarding/cardimembers"));
    }

    [Fact]
    public async Task TransferAdmin_PutsTheSuccessor_AndReturnsTheRoster()
    {
        var org = Guid.NewGuid();
        var successor = Guid.NewGuid();
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, $$"""
            {"success":true,"message":"ok","data":[{"userId":"{{successor}}","name":"Tom Doe","email":"tom@example.com",
             "role":"admin","isYou":false,"joinedDate":"2026-09-01T00:00:00Z"}],"timestamp":"2026-09-22T00:00:00Z"}
            """);

        var roster = await client.TransferFamilyAdminAsync(org, successor);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/api/v1/families/{org}/admin", request.Uri!.AbsolutePath);
        Assert.Contains(successor.ToString(), request.Body);
        Assert.Equal("admin", roster.Single().Role);
    }

    [Fact]
    public async Task ViewInvite_EscapesTheToken_AndNeverCaches()
    {
        var cache = new MemoryOfflineCache();
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"memberFirstName":"Margaret","inviterFirstName":"Jane",
             "expiresAt":"2026-09-29T00:00:00Z"},"timestamp":"2026-09-22T00:00:00Z"}
            """);

        var view = await client.ViewCaregiverInviteAsync("abc_-123/x");

        Assert.Equal("/api/v1/caregiver-invites/abc_-123%2Fx", http.Requests.Single().Uri!.AbsolutePath);
        Assert.Equal("Margaret", view.MemberFirstName);
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task AcceptInvite_Posts_AndForgetsTheFamilyAndMemberLists()
    {
        var cache = new MemoryOfflineCache();
        cache.Items["api/v1/families/mine"] = new OfflineCacheEntry("[]", DateTimeOffset.UtcNow);
        cache.Items["api/Onboarding/cardimembers"] = new OfflineCacheEntry("[]", DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, """
            {"success":true,"message":"ok","data":{"cardiMemberId":"6f9619ff-8b86-d011-b42d-00c04fc964ff",
             "organizationId":"7f9619ff-8b86-d011-b42d-00c04fc964ff","role":"member","canViewHealthData":true,
             "receiveAlerts":true,"alreadyHadAccess":false},"timestamp":"2026-09-22T00:00:00Z"}
            """);

        var redemption = await client.AcceptCaregiverInviteAsync("token-token-token-1");

        Assert.Equal("/api/v1/caregiver-invites/token-token-token-1/accept", http.Requests.Single().Uri!.AbsolutePath);
        Assert.False(redemption.AlreadyHadAccess);
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task CreateInvite_PostsTheGrants_AndTheListIsRefetchedAfter()
    {
        var member = Guid.NewGuid();
        var cache = new MemoryOfflineCache();
        cache.Items[$"api/v1/cardimembers/{member}/caregiver-invites"] = new OfflineCacheEntry("[]", DateTimeOffset.UtcNow);
        var (client, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.Created, $$"""
            {"success":true,"message":"ok","data":{"inviteId":"{{Guid.NewGuid()}}","cardiMemberId":"{{member}}",
             "role":"member","canViewHealthData":true,"receiveAlerts":false,"status":"pending",
             "url":"https://api.test/join?t=abc","expiresAt":"2026-09-29T00:00:00Z"},"timestamp":"2026-09-22T00:00:00Z"}
            """);

        var invite = await client.CreateCaregiverInviteAsync(member, new CreateCaregiverInviteRequest
        {
            CanViewHealthData = true,
            ReceiveAlerts = false,
        });

        Assert.Equal($"/api/v1/cardimembers/{member}/caregiver-invites", http.Requests.Single().Uri!.AbsolutePath);
        Assert.Contains("\"receiveAlerts\":false", http.Requests.Single().Body);
        Assert.Equal("https://api.test/join?t=abc", invite.Url);
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task AcknowledgeWithAnAnswer_SendsTheBody_AndCloseHitsItsOwnRoute()
    {
        var alertId = Guid.NewGuid();
        var (client, http) = CreateSut();
        var envelope = $$"""
            {"success":true,"message":"ok","data":{"alertId":"{{alertId}}","status":"acknowledged",
             "acknowledgedAt":"2026-09-22T08:00:00Z","unreadCount":0,"familyNotified":1},"timestamp":"2026-09-22T00:00:00Z"}
            """;
        http.Enqueue(HttpStatusCode.OK, envelope).Enqueue(HttpStatusCode.OK, envelope);

        await client.AcknowledgeAlertAsync(alertId, new AlertAnswerRequest { ResponseCode = "calling", Note = "Ringing now" });
        await client.CloseAlertAsync(alertId, new AlertAnswerRequest { ResponseCode = "expected" });

        Assert.Equal($"/api/v1/alerts/{alertId}/acknowledge", http.Requests[0].Uri!.AbsolutePath);
        Assert.Contains("\"responseCode\":\"calling\"", http.Requests[0].Body);
        Assert.Contains("Ringing now", http.Requests[0].Body);
        Assert.Equal($"/api/v1/alerts/{alertId}/close", http.Requests[1].Uri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, http.Requests[1].Method);
    }

    [Fact]
    public async Task ARefusedAsk_SurfacesTheServersSentenceWithItsStatus()
    {
        var org = Guid.NewGuid();
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.UnprocessableEntity,
            """{"success":false,"message":"This family's plan covers 1 person, and there is already 1.","timestamp":"2026-09-22T00:00:00Z"}""");

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            client.ApproveJoinRequestAsync(org, Guid.NewGuid(), new ApproveJoinRequest()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, ex.StatusCode);
        Assert.Contains("plan covers 1 person", ex.Message);
    }

    private static (CardiTrackApiClient Client, FakeHttpMessageHandler Http) CreateSut(IOfflineReadCache? cache = null)
    {
        var http = new FakeHttpMessageHandler();
        var client = new CardiTrackApiClient(
            new HttpClient(http) { BaseAddress = new Uri("https://api.test") }, cache);
        return (client, http);
    }
}
