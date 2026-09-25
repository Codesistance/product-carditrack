using System.Linq.Expressions;
using System.Text.Json;
using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// <c>GET /api/v1/notifications/summary</c> through the controller and the real
/// <see cref="NotificationService"/>, pinning the wire shape of the <c>memberSetup</c> checklist the
/// mobile "Complete the picture" ring is built against — and that the fields already there are
/// untouched by it.
/// </summary>
public class NotificationsSummaryEndpointTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly INotificationSnapshotQueries _snapshots = Substitute.For<INotificationSnapshotQueries>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public NotificationsSummaryEndpointTests()
    {
        _unitOfWork.Notifications
            .GetTopForDashboardAsync(_userId, Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Notification>());

        _unitOfWork.CardiMembers
            .FindAsync(Arg.Any<Expression<Func<CardiMember, bool>>>())
            .Returns([new CardiMember { Id = _memberId, FirstName = "Pop", LastName = "Example" }]);

        _snapshots.BuildContextsForUserAsync(_userId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([
                new NudgeContext
                {
                    UtcNow = Now,
                    OrganizationId = Guid.NewGuid(),
                    IsOwner = true,
                    User = new NudgeUserSnapshot
                    {
                        Id = _userId,
                        TimeZoneId = "UTC",
                        Locale = "en-GB",
                        CreatedDate = Now.AddDays(-60)
                    },
                    Member = new NudgeMemberSnapshot
                    {
                        Id = _memberId,
                        CreatedDate = Now.AddDays(-60),
                        HasMedicalNotes = true,
                        MedicalNotesReviewedAtUtc = Now.AddDays(-5),
                        HasEmergencyContact = false,
                        EverHadConnection = false,
                        DaysCaptured = 0,
                        HasEstablishedBaseline = false,
                        HasUnacknowledgedRedAlert = false
                    }
                }
            ]);
    }

    private NotificationsController CreateSut(bool authenticated = true)
    {
        _userContext.IsAuthenticated.Returns(authenticated);
        _userContext.UserId.Returns(authenticated ? _userId : Guid.Empty);

        return new NotificationsController(
            _userContext,
            Substitute.For<ILogger<NotificationsController>>(),
            new NotificationService(_unitOfWork, new NoOpGapResolver(), _snapshots),
            Substitute.For<IDeviceTokenService>(),
            Substitute.For<INotificationPreferenceService>(),
            Substitute.For<IAckDeliveryService>(),
            Substitute.For<IAckTokenService>());
    }

    [Fact]
    public async Task Summary_CarriesEachMembersSetupChecklist_InTheShapeTheAppReads()
    {
        var result = await CreateSut().GetSummary(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);

        // The web defaults ASP.NET Core serializes with — camelCase names, enums as integers.
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var data = json.RootElement.GetProperty("data");

        // The existing fields are still there, and still what they were.
        Assert.Equal(0, data.GetProperty("unseenCount").GetInt32());
        Assert.Equal(0, data.GetProperty("openCount").GetInt32());
        Assert.Equal(0, data.GetProperty("safetyBanners").GetArrayLength());
        Assert.Equal(0, data.GetProperty("dashboardCards").GetArrayLength());

        var member = Assert.Single(data.GetProperty("memberSetup").EnumerateArray().ToList());
        Assert.Equal(_memberId, member.GetProperty("cardiMemberId").GetGuid());
        Assert.Equal("Pop", member.GetProperty("cardiMemberFirstName").GetString());
        Assert.True(member.GetProperty("isOwner").GetBoolean());

        // No device: sleep and rhythm checks do not apply. Emergency contact and time zone are
        // open; the medical notes were confirmed five days ago.
        Assert.Equal(1, member.GetProperty("done").GetInt32());
        Assert.Equal(3, member.GetProperty("total").GetInt32());

        var steps = member.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "emergency-contact", "time-zone", "medical-information" },
            steps.Select(s => s.GetProperty("key").GetString()));

        var next = steps[0];
        Assert.Equal("Emergency contact", next.GetProperty("title").GetString());
        Assert.False(next.GetProperty("done").GetBoolean());
        Assert.Equal(
            $"carditrack://cardimembers/{_memberId}/edit#emergencyContact",
            next.GetProperty("actionDeepLink").GetString());

        Assert.True(steps[2].GetProperty("done").GetBoolean());
        Assert.Equal(
            $"carditrack://cardimembers/{_memberId}/edit#medicalNotes",
            steps[2].GetProperty("actionDeepLink").GetString());
    }

    [Fact]
    public async Task Summary_RefusesASignedOutCaller_WithoutBuildingAChecklist()
    {
        var result = await CreateSut(authenticated: false).GetSummary(CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
        Assert.Empty(_snapshots.ReceivedCalls());
    }

    private sealed class NoOpGapResolver : INotificationGapResolver
    {
        public Task ResolveForCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResolveForUserAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;

        public Task WithdrawForCardiMemberAsync(
            Guid cardiMemberId, Domain.Enums.NotificationResolutionReason reason, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
