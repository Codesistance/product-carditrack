using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Wearer-side device invitations. Two audiences share this service and only one of them has an
/// account, so most of what is asserted here is about the anonymous half telling a stranger holding
/// a link as little as possible.
/// </summary>
public class DeviceConnectionInviteServiceTests
{
    private const string BaseUrl = "https://api.example.test";

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IDeviceConnectionService _connections = Substitute.For<IDeviceConnectionService>();
    private readonly IAuditLogRepository _auditLogs = Substitute.For<IAuditLogRepository>();
    private readonly MovableClock _time = new(new DateTimeOffset(2026, 9, 18, 9, 0, 0, TimeSpan.Zero));

    /// <summary>The rows the in-memory repository substitute is standing in for.</summary>
    private readonly List<DeviceConnectionInvite> _stored = [];

    public DeviceConnectionInviteServiceTests()
    {
        SetupCaregiverLink(active: true);

        _unitOfWork.CardiMembers.GetByIdAsync(_memberId).Returns(
            new CardiMember { Id = _memberId, Name = "Margaret Hale", IsActive = true });
        _unitOfWork.Users.GetByIdAsync(_userId).Returns(
            new User { Id = _userId, Name = "John Thornton" });

        // A hand-rolled in-memory store rather than a mock per call: nearly every assertion here
        // turns on a row's state after a transition, and stubbing each read individually would
        // describe the implementation instead of the behaviour.
        _unitOfWork.DeviceConnectionInvites
            .When(r => r.AddAsync(Arg.Any<DeviceConnectionInvite>()))
            .Do(c => _stored.Add(c.Arg<DeviceConnectionInvite>()));

        _unitOfWork.DeviceConnectionInvites
            .GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => _stored.FirstOrDefault(i => i.TokenHash == c.ArgAt<string>(0)));

        _unitOfWork.DeviceConnectionInvites
            .GetByIdAsync(Arg.Any<Guid>())
            .Returns(c => _stored.FirstOrDefault(i => i.Id == c.ArgAt<Guid>(0)));

        _unitOfWork.DeviceConnectionInvites
            .TryMarkOpenedAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(c => Transition(
                c.ArgAt<Guid>(0), [DeviceInviteStatus.Pending], DeviceInviteStatus.Opened,
                c.ArgAt<DateTime>(1), null, opened: true));

        _unitOfWork.DeviceConnectionInvites
            .TryResolveAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<DeviceInviteStatus>>(),
                Arg.Any<DeviceInviteStatus>(), Arg.Any<DateTime>(), Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(c => Transition(
                c.ArgAt<Guid>(0),
                c.ArgAt<IReadOnlyCollection<DeviceInviteStatus>>(1),
                c.ArgAt<DeviceInviteStatus>(2),
                c.ArgAt<DateTime>(3),
                c.ArgAt<Guid?>(4),
                opened: false));

        _unitOfWork.DeviceConnectionInvites
            .RevokeLiveAsync(Arg.Any<Guid>(), Arg.Any<DeviceType>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                var live = _stored
                    .Where(i => i.CardiMemberId == c.ArgAt<Guid>(0)
                                && i.DeviceType == c.ArgAt<DeviceType>(1)
                                && IsLive(i.Status))
                    .ToList();

                foreach (var invite in live)
                {
                    invite.Status = DeviceInviteStatus.Revoked;
                    invite.ResolvedAt = c.ArgAt<DateTime>(2);
                }

                return live.Count;
            });

        _connections.InitiateWearerConnectionAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DeviceType>(),
                Arg.Any<CancellationToken>())
            .Returns("https://accounts.google.com/o/oauth2/v2/auth?state=abc");

        // The state the bounce comes back with names the invitation it was minted for. The default
        // is whichever invitation is live, which is what every happy-path test wants.
        _connections.PeekWearerInviteIdAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => _stored.LastOrDefault(i => IsLive(i.Status))?.Id);
    }

    private static bool IsLive(DeviceInviteStatus status) =>
        status is DeviceInviteStatus.Pending or DeviceInviteStatus.Opened;

    /// <summary>The database-side conditional update, as the repository performs it.</summary>
    private bool Transition(
        Guid inviteId,
        IReadOnlyCollection<DeviceInviteStatus> from,
        DeviceInviteStatus to,
        DateTime at,
        Guid? deviceConnectionId,
        bool opened)
    {
        var invite = _stored.FirstOrDefault(i => i.Id == inviteId);
        if (invite is null || !from.Contains(invite.Status))
            return false;

        invite.Status = to;
        if (opened)
            invite.OpenedAt = at;
        else
            invite.ResolvedAt = at;

        invite.DeviceConnectionId = deviceConnectionId ?? invite.DeviceConnectionId;
        return true;
    }

    private void SetupCaregiverLink(bool active) =>
        _unitOfWork.UserCardiMembers.GetByUserIdAsync(_userId).Returns(
        [
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = _memberId,
                IsActive = active,
            }
        ]);

    private DeviceConnectionInviteService CreateSut(Action<DeviceInviteOptions>? configure = null)
    {
        var options = new DeviceInviteOptions
        {
            PublicBaseUrl = string.Empty,
            LinkLifetimeMinutes = 1440,
            QrLifetimeMinutes = 15,
            RetentionDays = 30,
        };
        configure?.Invoke(options);

        return new DeviceConnectionInviteService(
            _unitOfWork,
            _connections,
            _auditLogs,
            Options.Create(options),
            NullLogger<DeviceConnectionInviteService>.Instance,
            _time);
    }

    private static CreateDeviceInviteRequest Request(string channel = "link") =>
        new() { Provider = "fitbit", Channel = channel };

    /// <summary>The token out of a created invitation's URL — the only place it is ever returned.</summary>
    private static string TokenFrom(DeviceInviteResponse invite) =>
        Uri.UnescapeDataString(invite.Url!.Split("t=")[1]);

    [Fact]
    public async Task Create_ReturnsTheUrlOnce_AndNeverAgain()
    {
        var sut = CreateSut();

        var created = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);
        Assert.NotNull(created.Url);

        var read = await sut.GetAsync(_userId, _memberId, created.InviteId);

        // The URL is a live credential. A status endpoint that kept re-issuing it would turn the
        // caregiver's waiting screen — which polls — into a repeated chance to leak one.
        Assert.Null(read.Url);
        Assert.Equal("pending", read.Status);
    }

    [Fact]
    public async Task Create_StoresOnlyAHashOfTheToken()
    {
        var sut = CreateSut();

        var created = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);
        var token = TokenFrom(created);

        var stored = Assert.Single(_stored);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Equal(InviteTokens.HashOrNull(token), stored.TokenHash);
        // A leaked database must not hand over live invitations.
        Assert.DoesNotContain(token, stored.TokenHash);
    }

    [Theory]
    [InlineData("qr", 15)]
    [InlineData("link", 1440)]
    public async Task Create_GivesEachChannelItsOwnLifetime(string channel, int expectedMinutes)
    {
        var sut = CreateSut();

        var created = await sut.CreateAsync(_userId, _memberId, Request(channel), BaseUrl);

        // A code on a screen and a message in an inbox are waiting on different things.
        Assert.Equal(
            _time.GetUtcNow().UtcDateTime.AddMinutes(expectedMinutes),
            created.ExpiresAt);
    }

    [Fact]
    public async Task Create_SupersedesAnEarlierLiveInvite()
    {
        var sut = CreateSut();

        var first = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);
        var firstToken = TokenFrom(first);

        await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);

        // Asking for a new link is how a caregiver takes back one they sent to the wrong person.
        Assert.Null(await sut.ViewAsync(firstToken));
        Assert.Equal(
            DeviceInviteStatus.Revoked,
            _stored.Single(i => i.Id == first.InviteId).Status);
    }

    [Fact]
    public async Task Create_RefusesAChannelItDoesNotKnow()
    {
        var sut = CreateSut();

        var ex = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => sut.CreateAsync(_userId, _memberId, Request("carrier_pigeon"), BaseUrl));

        Assert.Equal(DeviceConnectionException.UnsupportedInviteChannel, ex.Code);
    }

    [Fact]
    public async Task Create_PrefersConfiguredBaseUrl_OverTheRequestHost()
    {
        var sut = CreateSut(o => o.PublicBaseUrl = "https://api.carditrack.test/");

        var created = await sut.CreateAsync(
            _userId, _memberId, Request(), "https://attacker.example.com");

        // A forged Host header must not decide where an invitation the caregiver forwards in good
        // faith actually points.
        Assert.StartsWith("https://api.carditrack.test/connect?t=", created.Url);
        Assert.DoesNotContain("attacker.example.com", created.Url);
    }

    [Fact]
    public async Task Create_RefusesACaregiverWithoutAnActiveLink()
    {
        SetupCaregiverLink(active: false);
        var sut = CreateSut();

        // 404, not 403: a 403 would confirm the member exists.
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.CreateAsync(_userId, _memberId, Request(), BaseUrl));
    }

    [Fact]
    public async Task View_ShowsTwoFirstNamesAndNothingElse()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);

        var view = await sut.ViewAsync(TokenFrom(created));

        Assert.NotNull(view);
        Assert.Equal("Margaret", view.MemberFirstName);
        Assert.Equal("John", view.CaregiverFirstName);
        Assert.Equal("Fitbit", view.DeviceDisplayName);
        // Surnames identify people. The page has to be recognisable, which a first name achieves;
        // it does not have to be identifying, and whoever holds this link may not be the wearer.
        Assert.DoesNotContain("Hale", view.MemberFirstName);
        Assert.DoesNotContain("Thornton", view.CaregiverFirstName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public async Task View_RefusesAnythingThatIsNotALiveToken(string token)
    {
        var sut = CreateSut();
        await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);

        Assert.Null(await sut.ViewAsync(token));
    }

    [Fact]
    public async Task View_RefusesAnExpiredInvite()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request("qr"), BaseUrl);

        _time.Advance(TimeSpan.FromMinutes(16));

        Assert.Null(await sut.ViewAsync(TokenFrom(created)));
    }

    [Fact]
    public async Task View_RefusesOnceTheMemberIsDeactivated()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);

        _unitOfWork.CardiMembers.GetByIdAsync(_memberId).Returns(
            new CardiMember { Id = _memberId, Name = "Margaret Hale", IsActive = false });

        // A consent screen that cannot say who is asking on whose behalf is not informed consent.
        Assert.Null(await sut.ViewAsync(TokenFrom(created)));
    }

    [Fact]
    public async Task Start_MarksOpened_AndReturnsTheProviderUrl()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);

        var url = await sut.StartAsync(TokenFrom(created));

        Assert.StartsWith("https://accounts.google.com/", url);

        var read = await sut.GetAsync(_userId, _memberId, created.InviteId);
        Assert.Equal("opened", read.Status);
        Assert.NotNull(read.OpenedAt);
    }

    [Fact]
    public async Task Start_KeepsTheFirstOpenedTime_WhenTheWearerReloads()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);
        var token = TokenFrom(created);

        await sut.StartAsync(token);
        var firstOpened = (await sut.GetAsync(_userId, _memberId, created.InviteId)).OpenedAt;

        _time.Advance(TimeSpan.FromMinutes(5));
        await sut.StartAsync(token);

        // "When did they first look at this" is the question the caregiver's screen is asking, and
        // a pull-to-refresh must not keep moving the answer.
        Assert.Equal(firstOpened, (await sut.GetAsync(_userId, _memberId, created.InviteId)).OpenedAt);
    }

    [Fact]
    public async Task Start_RefusesAnInviteThatHasBeenDeclined()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);
        var token = TokenFrom(created);

        Assert.True(await sut.DeclineAsync(token));

        Assert.Null(await sut.StartAsync(token));
        await _connections.DidNotReceiveWithAnyArgs()
            .InitiateWearerConnectionAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Decline_ReportsFalse_ForATokenThatNamesNothing()
    {
        var sut = CreateSut();

        // Same answer as a real invitation that has already been declined, so the endpoint cannot
        // be used to tell a live token from an invented one.
        Assert.False(await sut.DeclineAsync("not-a-real-token"));
    }

    [Fact]
    public async Task Complete_ClosesTheInvite_AndRecordsTheConnection()
    {
        var deviceId = Guid.NewGuid();
        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);
        await sut.StartAsync(TokenFrom(created));

        _connections.CompleteWearerConnectionAsync(
                "fitbit", "state_1", "auth_code", Arg.Any<CancellationToken>())
            .Returns(new WearerConnectionCompletion(
                new DeviceResponse { DeviceId = deviceId, DisplayName = "Fitbit" },
                created.InviteId));

        var outcome = await sut.CompleteFromCallbackAsync("fitbit", "state_1", "auth_code", null);

        Assert.Equal(WearerConnectionResult.Completed, outcome.Result);
        Assert.Equal("Fitbit", outcome.DeviceDisplayName);

        var read = await sut.GetAsync(_userId, _memberId, created.InviteId);
        Assert.Equal("completed", read.Status);
        // "The invitation you sent on Tuesday is why this device is connected", answerable later
        // without inferring it from timestamps.
        Assert.Equal(deviceId, read.DeviceId);
    }

    [Fact]
    public async Task Complete_RefusesOnceTheCaregiverHasRevoked_EvenMidConsent()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);
        await sut.StartAsync(TokenFrom(created));

        // The state stays valid for fifteen minutes and knows nothing about the invitation row, so
        // the wearer can be standing on the provider's consent screen when the caregiver cancels.
        _connections.PeekWearerInviteIdAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(created.InviteId);
        await sut.RevokeAsync(_userId, _memberId, created.InviteId);

        var outcome = await sut.CompleteFromCallbackAsync("fitbit", "state_1", "auth_code", null);

        Assert.Equal(WearerConnectionResult.Failed, outcome.Result);
        Assert.False(outcome.CanRetry);

        // And nothing was stored. The withdrawal has to win outright, not merely fail to update the
        // invitation's status after the connection has already been created.
        await _connections.DidNotReceiveWithAnyArgs()
            .CompleteWearerConnectionAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Complete_RefusesOnceTheInviteHasExpired()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request("qr"), BaseUrl);
        await sut.StartAsync(TokenFrom(created));

        _connections.PeekWearerInviteIdAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(created.InviteId);
        _time.Advance(TimeSpan.FromMinutes(16));

        var outcome = await sut.CompleteFromCallbackAsync("fitbit", "state_1", "auth_code", null);

        Assert.Equal(WearerConnectionResult.Failed, outcome.Result);
        await _connections.DidNotReceiveWithAnyArgs()
            .CompleteWearerConnectionAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Complete_OffersNoRetry_WhenTheInviteIsNotLive()
    {
        var sut = CreateSut();
        await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);

        _connections.CompleteWearerConnectionAsync(
                "fitbit", "state_1", "auth_code", Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new DeviceConnectionException(
                DeviceConnectionException.InviteNotLive, "That link is no longer available."));

        var outcome = await sut.CompleteFromCallbackAsync("fitbit", "state_1", "auth_code", null);

        Assert.Equal(WearerConnectionResult.Failed, outcome.Result);
        // A dead invitation is as unretryable as a spent state — a button would lead nowhere.
        Assert.False(outcome.CanRetry);
    }

    [Fact]
    public async Task Complete_LeavesTheInviteLive_WhenTheWearerRefusedAtTheProvider()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);
        var token = TokenFrom(created);
        await sut.StartAsync(token);

        var outcome = await sut.CompleteFromCallbackAsync("fitbit", "state_1", null, "access_denied");

        Assert.Equal(WearerConnectionResult.Denied, outcome.Result);
        Assert.True(outcome.CanRetry);
        // Sending the wearer back to the caregiver for a fresh link over a mis-tap would be a poor
        // way to treat the one person in this flow who never asked to be in it.
        Assert.NotNull(await sut.ViewAsync(token));
    }

    [Fact]
    public async Task Complete_ReportsFailureWithoutRetry_WhenTheStateIsAlreadySpent()
    {
        var sut = CreateSut();
        await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);

        _connections.CompleteWearerConnectionAsync(
                "fitbit", "state_1", "auth_code", Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new DeviceConnectionException(
                DeviceConnectionException.InvalidStateToken, "Invalid or expired state token."));

        var outcome = await sut.CompleteFromCallbackAsync("fitbit", "state_1", "auth_code", null);

        Assert.Equal(WearerConnectionResult.Failed, outcome.Result);
        // There is nothing left to retry with — offering a button would lead to a dead end.
        Assert.False(outcome.CanRetry);
    }

    [Fact]
    public async Task Complete_ReportsFailure_WhenTheCaregiverLostAccessMeanwhile()
    {
        var sut = CreateSut();
        await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);

        _connections.CompleteWearerConnectionAsync(
                "fitbit", "state_1", "auth_code", Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new KeyNotFoundException("CardiMember not found"));

        var outcome = await sut.CompleteFromCallbackAsync("fitbit", "state_1", "auth_code", null);

        // An invitation must not outlive the authority that issued it: this is the moment health
        // data would start flowing to somebody already cut off.
        Assert.Equal(WearerConnectionResult.Failed, outcome.Result);
        Assert.False(outcome.CanRetry);
    }

    [Fact]
    public async Task Revoke_ReportsTheRealOutcome_WhenTheWearerGotThereFirst()
    {
        var deviceId = Guid.NewGuid();
        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);

        _connections.CompleteWearerConnectionAsync(
                "fitbit", "state_1", "auth_code", Arg.Any<CancellationToken>())
            .Returns(new WearerConnectionCompletion(
                new DeviceResponse { DeviceId = deviceId, DisplayName = "Fitbit" },
                created.InviteId));

        await sut.CompleteFromCallbackAsync("fitbit", "state_1", "auth_code", null);

        var revoked = await sut.RevokeAsync(_userId, _memberId, created.InviteId);

        // The caregiver's intent — stop this being usable — is satisfied either way. Telling them
        // the cancel failed would send them looking for a problem that is not there.
        Assert.Equal("completed", revoked.Status);
        Assert.Equal(deviceId, revoked.DeviceId);
    }

    [Fact]
    public async Task Get_RefusesAnInviteBelongingToAnotherMember()
    {
        var otherMemberId = Guid.NewGuid();
        _unitOfWork.CardiMembers.GetByIdAsync(otherMemberId).Returns(
            new CardiMember { Id = otherMemberId, Name = "Someone Else", IsActive = true });
        _unitOfWork.UserCardiMembers.GetByUserIdAsync(_userId).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = _memberId, IsActive = true },
            new UserCardiMember { UserId = _userId, CardiMemberId = otherMemberId, IsActive = true },
        ]);

        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request(), BaseUrl);

        // The route's member id has to match the invite's own, or an id guessed off one member
        // reads an invitation belonging to another.
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.GetAsync(_userId, otherMemberId, created.InviteId));
    }

    [Fact]
    public async Task Status_ReadsExpired_OnceTheDeadlinePasses()
    {
        var sut = CreateSut();
        var created = await sut.CreateAsync(_userId, _memberId, Request("qr"), BaseUrl);

        _time.Advance(TimeSpan.FromMinutes(16));

        // Reported as a status rather than left for the client to derive: the waiting screen should
        // not be re-deriving expiry from a device clock that may not agree with ours.
        Assert.Equal("expired", (await sut.GetAsync(_userId, _memberId, created.InviteId)).Status);
    }

    /// <summary>
    /// A clock the test moves by hand. Everything interesting about an invitation is a deadline, so
    /// the alternative is either sleeping through a real fifteen minutes or configuring lifetimes
    /// down to milliseconds and asserting against timings the product would never use.
    /// </summary>
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
