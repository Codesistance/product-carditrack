using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Notifications;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class CardiMemberServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IDeviceConnectionRepository _devices = Substitute.For<IDeviceConnectionRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IRealtimeAssessmentRepository _realtimeAssessments = Substitute.For<IRealtimeAssessmentRepository>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly IProfilePhotoProcessor _photoProcessor = Substitute.For<IProfilePhotoProcessor>();
    private readonly IProfilePhotoStorage _photoStorage = Substitute.For<IProfilePhotoStorage>();

    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    public CardiMemberServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.DeviceConnections.Returns(_devices);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.RealtimeAssessments.Returns(_realtimeAssessments);
        _alerts.GetUnresolvedByCardiMemberAsync(Arg.Any<Guid>()).Returns([]);
        _realtimeAssessments.GetLatestAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((RealtimeAssessment?)null);

        // Reversible stand-in for AES so tests can assert that notes are stored encrypted
        // and read back in the clear without pulling in a real key.
        _encryption.Encrypt(Arg.Any<string>()).Returns(c => $"enc({c.Arg<string>()})");
        _encryption.Decrypt(Arg.Any<string>()).Returns(c =>
        {
            var value = c.Arg<string>() ?? string.Empty;
            return value.StartsWith("enc(") && value.EndsWith(')')
                ? value[4..^1]
                // AES-GCM rejects anything that isn't its own ciphertext; legacy plaintext
                // notes land here.
                : throw new FormatException("not ciphertext");
        });
    }


    private CardiMemberService CreateSut() => new(
        _unitOfWork, _access, _encryption, new NoOpNotificationGapResolver(), _photoProcessor,
        _photoStorage);

    private static CreateCardiMemberRequest BuildRequest() => new()
    {
        FirstName = "Margaret",
        LastName = "Doe",
        DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-78)),
        Gender = Gender.Female,
        Email = "margaret@example.com",
        Phone = "+441234567890",
        EmergencyContactName = "Jane Doe",
        EmergencyContactPhone = "+441234567891",
        MedicalNotes = "Pacemaker fitted 2019",
        RelationshipType = RelationshipType.Parent,
        IsPrimaryCaregiver = true,
    };

    [Fact]
    public async Task Create_CommitsOnce_WhenBothSavesSucceed()
    {
        await CreateSut().CreateCardiMemberAsync(_organizationId, _userId, BuildRequest());

        await _unitOfWork.Received(1).BeginTransactionAsync();
        await _unitOfWork.Received(1).CommitTransactionAsync();
        await _unitOfWork.DidNotReceive().RollbackTransactionAsync();
    }

    [Fact]
    public async Task Create_RollsBack_WhenTheCaregiverLinkCannotBeSaved()
    {
        // The member and the caregiver's link to it are one creation. A failure after the first
        // save must not leave a member row that no caregiver can reach — and the id is handed to
        // the audit middleware only on success, so an orphan would also be unnamed there.
        _unitOfWork.SaveChangesAsync().Returns(
            Task.FromResult(1),
            Task.FromException<int>(new InvalidOperationException("link save failed")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSut().CreateCardiMemberAsync(_organizationId, _userId, BuildRequest()));

        await _unitOfWork.Received(1).BeginTransactionAsync();
        await _unitOfWork.Received(1).RollbackTransactionAsync();
        await _unitOfWork.DidNotReceive().CommitTransactionAsync();
        // The failed graph must not ride along on this unit of work's next save.
        _unitOfWork.Received(1).ClearTracking();
    }

    [Fact]
    public async Task Create_WithPhoto_DiscardsTheObject_WhenTheTransactionCannotEvenStart()
    {
        // The one path where rollback is a no-op rather than an undo: nothing was written, but
        // the photo was already uploaded, and the caller must still see the real failure.
        SetupPhotoPipeline("members/x/orphan.jpg");
        _unitOfWork.BeginTransactionAsync().Returns(Task.FromException(new IOException("no connection")));
        var request = BuildRequest();
        request.PhotoBase64 = PhotoBase64;

        await Assert.ThrowsAsync<IOException>(
            () => CreateSut().CreateCardiMemberAsync(_organizationId, _userId, request));

        await _photoStorage.Received(1).DeleteAsync("members/x/orphan.jpg", Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
        await _unitOfWork.DidNotReceive().CommitTransactionAsync();
    }

    [Fact]
    public async Task Create_FromALegacyClientSendingOnlyName_SplitsItLikeTheMigration()
    {
        CardiMember? savedMember = null;
        await _members.AddAsync(Arg.Do<CardiMember>(m => savedMember = m));
        // Built without FirstName/LastName, as an old build's payload deserialises: omitted.
        var full = BuildRequest();
        var request = new CreateCardiMemberRequest
        {
            Name = "Mary Ann Smith",
            DateOfBirth = full.DateOfBirth,
            Gender = full.Gender,
            RelationshipType = full.RelationshipType,
            IsPrimaryCaregiver = full.IsPrimaryCaregiver,
        };

        await CreateSut().CreateCardiMemberAsync(_organizationId, _userId, request);

        Assert.Equal("Mary", savedMember!.FirstName);
        Assert.Equal("Ann Smith", savedMember.LastName);
    }

    [Fact]
    public async Task Create_TrimsTheNamesAndStoresABlankLastNameAsNone()
    {
        CardiMember? savedMember = null;
        await _members.AddAsync(Arg.Do<CardiMember>(m => savedMember = m));
        var request = BuildRequest();
        request.FirstName = "  Arthur ";
        request.LastName = "   ";

        var response = await CreateSut().CreateCardiMemberAsync(_organizationId, _userId, request);

        Assert.Equal("Arthur", savedMember!.FirstName);
        Assert.Null(savedMember.LastName);
        Assert.Equal("Arthur", response.FirstName);
        Assert.Null(response.LastName);
        Assert.Equal("Arthur", response.Name);
    }

    [Fact]
    public async Task Create_ResponseCarriesBothPartsAndTheJoinedFullName()
    {
        var response = await CreateSut().CreateCardiMemberAsync(_organizationId, _userId, BuildRequest());

        Assert.Equal("Margaret", response.FirstName);
        Assert.Equal("Doe", response.LastName);
        Assert.Equal("Margaret Doe", response.Name);
    }

    [Fact]
    public async Task Create_PersistsMemberAndCaregiverLink()
    {
        CardiMember? savedMember = null;
        UserCardiMember? savedLink = null;
        await _members.AddAsync(Arg.Do<CardiMember>(m => savedMember = m));
        await _links.AddAsync(Arg.Do<UserCardiMember>(l => savedLink = l));

        await CreateSut().CreateCardiMemberAsync(_organizationId, _userId, BuildRequest());

        Assert.NotNull(savedMember);
        Assert.Equal(_organizationId, savedMember!.OrganizationId);
        Assert.Equal("Margaret", savedMember.FirstName);
        Assert.Equal("Doe", savedMember.LastName);
        Assert.Equal(Gender.Female, savedMember.Gender);
        // Medical notes are PHI — what reaches the repository must be ciphertext.
        Assert.Equal("enc(Pacemaker fitted 2019)", savedMember.MedicalNotes);
        Assert.True(savedMember.IsActive);

        Assert.NotNull(savedLink);
        Assert.Equal(_userId, savedLink!.UserId);
        Assert.Equal(savedMember.Id, savedLink.CardiMemberId);
        Assert.Equal(RelationshipType.Parent, savedLink.RelationshipType);
        Assert.True(savedLink.IsPrimaryCaregiver);
        Assert.True(savedLink.CanViewHealthData);
        Assert.True(savedLink.ReceiveAlerts);

        await _unitOfWork.Received(2).SaveChangesAsync();
    }

    [Theory]
    [InlineData("Pacemaker fitted 2019", true)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public async Task Create_DatesTheBackground_OnlyWhenTheFormSuppliedOne(string? notes, bool dated)
    {
        CardiMember? savedMember = null;
        await _members.AddAsync(Arg.Do<CardiMember>(m => savedMember = m));

        var request = BuildRequest();
        request.MedicalNotes = notes;

        await CreateSut().CreateCardiMemberAsync(_organizationId, _userId, request);

        Assert.NotNull(savedMember);
        Assert.Equal(dated, savedMember!.MedicalNotesReviewedAtUtc is not null);
    }

    [Fact]
    public async Task Create_ReturnsMappedResponse_WithComputedAge()
    {
        var response = await CreateSut().CreateCardiMemberAsync(_organizationId, _userId, BuildRequest());

        Assert.Equal("Margaret Doe", response.Name);
        Assert.Equal(78, response.Age);
        Assert.Equal(RelationshipType.Parent, response.Relationship);
        Assert.True(response.IsPrimaryCaregiver);
        Assert.True(response.IsActive);
    }

    [Fact]
    public async Task Create_DoesNotCountBirthdayNotYetReachedThisYear()
    {
        var request = BuildRequest();
        request.DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-30).AddDays(1));

        var response = await CreateSut().CreateCardiMemberAsync(_organizationId, _userId, request);

        Assert.Equal(29, response.Age);
    }

    [Fact]
    public async Task GetById_ReturnsNull_WhenMemberMissing()
    {
        var id = Guid.NewGuid();
        _members.GetByIdAsync(id).Returns((CardiMember?)null);

        Assert.Null(await CreateSut().GetByIdAsync(id));
    }

    [Fact]
    public async Task GetById_MapsFirstRelationship()
    {
        var member = new CardiMember
        {
            OrganizationId = _organizationId,
            FirstName = "Margaret",
            LastName = "Doe",
            DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-78)),
            IsActive = true,
        };
        _members.GetByIdAsync(member.Id).Returns(member);
        _links.GetByCardiMemberIdAsync(member.Id).Returns(
        [
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = member.Id,
                RelationshipType = RelationshipType.Grandparent,
                IsPrimaryCaregiver = true,
            },
        ]);

        var response = await CreateSut().GetByIdAsync(member.Id);

        Assert.NotNull(response);
        Assert.Equal(member.Id, response!.Id);
        Assert.Equal(RelationshipType.Grandparent, response.Relationship);
        Assert.True(response.IsPrimaryCaregiver);
        Assert.Equal(78, response.Age);
    }

    [Fact]
    public async Task GetById_DefaultsRelationship_WhenNoLinksExist()
    {
        var member = new CardiMember { OrganizationId = _organizationId, FirstName = "Margaret", LastName = "Doe" };
        _members.GetByIdAsync(member.Id).Returns(member);
        _links.GetByCardiMemberIdAsync(member.Id).Returns([]);

        var response = await CreateSut().GetByIdAsync(member.Id);

        Assert.Equal(RelationshipType.Other, response!.Relationship);
        Assert.False(response.IsPrimaryCaregiver);
    }

    [Fact]
    public async Task GetForUserInOrganization_ListsOnlyTheMembersTheCallerHasAGrantOn()
    {
        var mine = new CardiMember { OrganizationId = _organizationId, FirstName = "Margaret", LastName = "Doe" };
        var theirs = new CardiMember { OrganizationId = _organizationId, FirstName = "Arthur", LastName = "Doe" };
        _members.GetByOrganizationIdAsync(_organizationId).Returns([mine, theirs]);
        _links.GetByCardiMemberIdAsync(mine.Id).Returns(
        [
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = mine.Id,
                RelationshipType = RelationshipType.Parent,
                IsPrimaryCaregiver = true,
                IsActive = true,
            },
        ]);
        // Somebody else's grant on the same family's other member.
        _links.GetByCardiMemberIdAsync(theirs.Id).Returns(
        [
            new UserCardiMember
            {
                UserId = Guid.NewGuid(),
                CardiMemberId = theirs.Id,
                RelationshipType = RelationshipType.Parent,
                IsActive = true,
            },
        ]);

        var responses = await CreateSut().GetForUserInOrganizationAsync(_userId, _organizationId);

        // Being in a family is not being allowed to see everybody in it. A caregiver invited to
        // watch one person gets a link to that person, and this list used to answer from the
        // organization alone — the same set while a family held one caregiver, a way to see the
        // whole household once it holds several.
        var only = Assert.Single(responses);
        Assert.Equal("Margaret Doe", only.Name);
        Assert.Equal(RelationshipType.Parent, only.Relationship);
        Assert.True(only.IsPrimaryCaregiver);
    }

    [Fact]
    public async Task GetForUserInOrganization_ReportsTheCallersOwnRelationship_NotAnothersGrant()
    {
        var member = new CardiMember { OrganizationId = _organizationId, FirstName = "Margaret", LastName = "Doe" };
        _members.GetByOrganizationIdAsync(_organizationId).Returns([member]);
        _links.GetByCardiMemberIdAsync(member.Id).Returns(
        [
            // First in the list, and not the caller: the daughter who set the member up.
            new UserCardiMember
            {
                UserId = Guid.NewGuid(),
                CardiMemberId = member.Id,
                RelationshipType = RelationshipType.Parent,
                IsPrimaryCaregiver = true,
                IsActive = true,
            },
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = member.Id,
                RelationshipType = RelationshipType.Grandparent,
                IsPrimaryCaregiver = false,
                IsActive = true,
            },
        ]);

        var only = Assert.Single(
            await CreateSut().GetForUserInOrganizationAsync(_userId, _organizationId));

        // It used to take whichever link came back first, which was the only one there was. Now
        // that would tell a grandchild they are the member's parent and the primary caregiver.
        Assert.Equal(RelationshipType.Grandparent, only.Relationship);
        Assert.False(only.IsPrimaryCaregiver);
    }

    [Fact]
    public async Task GetForUserInOrganization_IgnoresARevokedGrant()
    {
        var member = new CardiMember { OrganizationId = _organizationId, FirstName = "Margaret", LastName = "Doe" };
        _members.GetByOrganizationIdAsync(_organizationId).Returns([member]);
        _links.GetByCardiMemberIdAsync(member.Id).Returns(
        [
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = member.Id,
                RelationshipType = RelationshipType.Parent,
                IsActive = false,
            },
        ]);

        Assert.Empty(await CreateSut().GetForUserInOrganizationAsync(_userId, _organizationId));
    }

    [Fact]
    public async Task GetForUserInOrganization_SaysWhenEachMembersDeviceLastSent()
    {
        // The Family tab's "last heard from" line: the newest across the member's active
        // connections when the member carries no stamp of its own — the detail screen's rule.
        var watched = new CardiMember { OrganizationId = _organizationId, FirstName = "Margaret", LastName = "Doe" };
        var unconnected = new CardiMember { OrganizationId = _organizationId, FirstName = "Arthur", LastName = "Doe" };
        _members.GetByOrganizationIdAsync(_organizationId).Returns([watched, unconnected]);
        foreach (var member in new[] { watched, unconnected })
        {
            _links.GetByCardiMemberIdAsync(member.Id).Returns(
            [
                new UserCardiMember { UserId = _userId, CardiMemberId = member.Id, IsActive = true },
            ]);
        }

        var newest = new DateTime(2026, 9, 25, 11, 40, 0, DateTimeKind.Utc);
        _devices.GetActiveByCardiMemberIdAsync(watched.Id).Returns(
        [
            new DeviceConnection { CardiMemberId = watched.Id, LastSyncDate = newest.AddHours(-3) },
            new DeviceConnection { CardiMemberId = watched.Id, LastSyncDate = newest },
        ]);
        _devices.GetActiveByCardiMemberIdAsync(unconnected.Id).Returns([]);

        var responses = await CreateSut().GetForUserInOrganizationAsync(_userId, _organizationId);

        var margaret = responses.Single(r => r.Name == "Margaret Doe");
        Assert.Equal(newest, margaret.LastSyncedAt);
        Assert.Equal(2, margaret.ConnectedDeviceCount);

        var arthur = responses.Single(r => r.Name == "Arthur Doe");
        Assert.Null(arthur.LastSyncedAt);
        Assert.Equal(0, arthur.ConnectedDeviceCount);
    }

    /// <summary>
    /// The member's own LastSyncDate wins even when a connection carries a later one — the detail
    /// screen's rule (BuildDetailAsync), so a card and the page it opens name the same time.
    /// </summary>
    [Fact]
    public async Task GetForUserInOrganization_PrefersTheMembersOwnStamp_OverANewerConnection()
    {
        var own = new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);
        var member = new CardiMember { OrganizationId = _organizationId, FirstName = "Margaret", LastName = "Doe", LastSyncDate = own };
        _members.GetByOrganizationIdAsync(_organizationId).Returns([member]);
        _links.GetByCardiMemberIdAsync(member.Id).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = member.Id, IsActive = true },
        ]);
        _devices.GetActiveByCardiMemberIdAsync(member.Id).Returns(
        [
            new DeviceConnection { CardiMemberId = member.Id, LastSyncDate = own.AddHours(2) },
        ]);

        var only = Assert.Single(await CreateSut().GetForUserInOrganizationAsync(_userId, _organizationId));

        Assert.Equal(own, only.LastSyncedAt);
    }

    /// <summary>
    /// Connected but never heard from: a device, and no time — which the Family tab reads as
    /// "waiting for the first sync" rather than "no device connected".
    /// </summary>
    [Fact]
    public async Task GetForUserInOrganization_ConnectedButNeverSynced_CountsTheDeviceWithNoTime()
    {
        var member = new CardiMember { OrganizationId = _organizationId, FirstName = "Margaret", LastName = "Doe" };
        _members.GetByOrganizationIdAsync(_organizationId).Returns([member]);
        _links.GetByCardiMemberIdAsync(member.Id).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = member.Id, IsActive = true },
        ]);
        _devices.GetActiveByCardiMemberIdAsync(member.Id).Returns(
        [
            new DeviceConnection { CardiMemberId = member.Id, LastSyncDate = null },
        ]);

        var only = Assert.Single(await CreateSut().GetForUserInOrganizationAsync(_userId, _organizationId));

        Assert.Null(only.LastSyncedAt);
        Assert.Equal(1, only.ConnectedDeviceCount);
    }

    // ── M1-13 detail ────────────────────────────────────────────────────────────

    private CardiMember SeedMember(
        bool isActive = true,
        string? encryptedNotes = "enc(Pacemaker fitted 2019)",
        DateTime? pausedUntil = null)
    {
        var member = new CardiMember
        {
            OrganizationId = _organizationId,
            FirstName = "Margaret",
            LastName = "Doe",
            DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-78)),
            EmergencyContactName = "Jane Doe",
            EmergencyContactPhone = "+441234567891",
            MedicalNotes = encryptedNotes,
            MonitoringPausedUntil = pausedUntil,
            IsActive = isActive,
        };
        _members.GetByIdAsync(member.Id).Returns(member);
        _links.GetByUserIdAsync(_userId).Returns(
        [
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = member.Id,
                RelationshipType = RelationshipType.Parent,
                IsPrimaryCaregiver = true,
                IsActive = true,
            },
        ]);
        _links.GetByCardiMemberIdAsync(member.Id).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = member.Id, IsActive = true },
        ]);
        _devices.GetActiveByCardiMemberIdAsync(member.Id).Returns([]);
        _devices.GetByCardiMemberIdAsync(member.Id).Returns([]);
        _activityLogs
            .GetByCardiMemberAndDateRangeAsync(member.Id, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);
        _baselines.GetLatestByCardiMemberAsync(member.Id, Arg.Any<int>()).Returns((PatternBaseline?)null);
        return member;
    }

    /// <summary>
    /// A journal entry draws the series that ended with the period it accounts for. Asked for a
    /// day, the series runs the thirty days up to it and reads the logs of exactly those days —
    /// even for a member with nothing in today's window, whose metrics then carry no current
    /// reading but still the series.
    /// </summary>
    [Fact]
    public async Task GetDetail_EndsTheSeriesOnTheDayAskedFor()
    {
        var member = SeedMember();
        var monthEnd = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-45);
        _activityLogs
            .GetByCardiMemberAndDateRangeAsync(member.Id, monthEnd.AddDays(-29), monthEnd)
            .Returns([new ActivityLog { CardiMemberId = member.Id, Date = monthEnd, Steps = 4100 }]);

        var detail = await CreateSut().GetDetailAsync(_userId, member.Id, seriesEndsOn: monthEnd);

        var series = detail.Metrics!.Steps.Series;
        Assert.Equal(30, series.Count);
        Assert.Equal(monthEnd.AddDays(-29), series[0].Date);
        Assert.Equal(monthEnd, series[^1].Date);
        Assert.Equal(4100m, series[^1].Value);
        Assert.Null(detail.Metrics.Steps.Value);
    }

    /// <summary>
    /// Only the series moves. The latest reading and everything built on it are today's,
    /// because the profile is about now whichever period its charts are drawn for.
    /// </summary>
    [Fact]
    public async Task GetDetail_KeepsTheLatestReadingCurrentWhenTheSeriesIsHistoric()
    {
        var member = SeedMember();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthEnd = today.AddDays(-45);
        _activityLogs
            .GetByCardiMemberAndDateRangeAsync(member.Id, today.AddDays(-29), today)
            .Returns([new ActivityLog { CardiMemberId = member.Id, Date = today.AddDays(-1), RestingHeartRate = 61 }]);
        _activityLogs
            .GetByCardiMemberAndDateRangeAsync(member.Id, monthEnd.AddDays(-29), monthEnd)
            .Returns([new ActivityLog { CardiMemberId = member.Id, Date = monthEnd, RestingHeartRate = 74 }]);

        var detail = await CreateSut().GetDetailAsync(_userId, member.Id, seriesEndsOn: monthEnd);

        var heartRate = detail.Metrics!.RestingHeartRate;
        Assert.Equal(61m, heartRate.Value);
        Assert.Equal(monthEnd, heartRate.Series[^1].Date);
        Assert.Equal(74m, heartRate.Series[^1].Value);
    }

    /// <summary>
    /// The status is a statement about the member now. A member with nothing in today's window
    /// reads the same whether or not an old month's series was asked for.
    /// </summary>
    [Fact]
    public async Task GetDetail_JudgesHealthStatusOnTodaysWindowNotTheHistoricSeries()
    {
        var member = SeedMember();
        var monthEnd = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-45);
        _activityLogs
            .GetByCardiMemberAndDateRangeAsync(member.Id, monthEnd.AddDays(-29), monthEnd)
            .Returns([new ActivityLog { CardiMemberId = member.Id, Date = monthEnd, Steps = 4100 }]);

        var plain = await CreateSut().GetDetailAsync(_userId, member.Id);
        var dated = await CreateSut().GetDetailAsync(_userId, member.Id, seriesEndsOn: monthEnd);

        Assert.Null(plain.Metrics);
        Assert.NotNull(dated.Metrics);
        Assert.Equal(plain.HealthStatus, dated.HealthStatus);
    }

    /// <summary>
    /// The query binder accepts any date, including one with no thirty days before it. That is
    /// not a request for a window; it gets today's series rather than a 500.
    /// </summary>
    [Fact]
    public async Task GetDetail_IgnoresASeriesEndTooEarlyToHaveAWindow()
    {
        var member = SeedMember();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _activityLogs
            .GetByCardiMemberAndDateRangeAsync(member.Id, today.AddDays(-29), today)
            .Returns([new ActivityLog { CardiMemberId = member.Id, Date = today, Steps = 900 }]);

        var detail = await CreateSut().GetDetailAsync(_userId, member.Id, seriesEndsOn: DateOnly.MinValue);

        Assert.Equal(today, detail.Metrics!.Steps.Series[^1].Date);
    }

    [Fact]
    public async Task GetDetail_TreatsAFutureSeriesEndAsToday()
    {
        var member = SeedMember();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _activityLogs
            .GetByCardiMemberAndDateRangeAsync(member.Id, today.AddDays(-29), today)
            .Returns([new ActivityLog { CardiMemberId = member.Id, Date = today, Steps = 900 }]);

        var detail = await CreateSut().GetDetailAsync(_userId, member.Id, seriesEndsOn: today.AddDays(10));

        Assert.Equal(today, detail.Metrics!.Steps.Series[^1].Date);
    }

    // The detail screen reads "today" on the member's anchor clock, as the dashboard does —
    // ingestion stores rows under that local date. Zones without daylight saving, so the offsets
    // hold whatever the date.

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();

    private CardiMemberService CreateSutAt(DateTimeOffset utcNow) => new(
        _unitOfWork, _access, _encryption, new NoOpNotificationGapResolver(), _photoProcessor,
        _photoStorage, new FakeTimeProvider(utcNow));

    private void AnchorToCaregiverZone(CardiMember member, string timeZoneId)
    {
        _unitOfWork.Users.Returns(_users);
        _users.GetByIdAsync(_userId).Returns(new User { Id = _userId, TimeZoneId = timeZoneId });
        _baselines.GetLatestByCardiMemberAsync(member.Id, BaselineProgress.PeriodDays).Returns(new PatternBaseline
        {
            CardiMemberId = member.Id,
            PeriodDays = BaselineProgress.PeriodDays,
            AvgSteps = 8000,
        });
    }

    [Fact]
    public async Task GetDetail_EastOfUtc_ReadsTheMembersDateBeforeTheUtcDateRolls()
    {
        // 06:00 on the 25th in Brisbane (UTC+10) is still the 24th in UTC.
        var member = SeedMember();
        AnchorToCaregiverZone(member, "Australia/Brisbane");
        var localToday = new DateOnly(2026, 9, 25);
        _activityLogs
            .GetByCardiMemberAndDateRangeAsync(member.Id, localToday.AddDays(-29), localToday)
            .Returns([new ActivityLog { CardiMemberId = member.Id, Date = localToday, Steps = 300, SleepMinutes = 450 }]);

        var detail = await CreateSutAt(new DateTimeOffset(2026, 9, 24, 20, 0, 0, TimeSpan.Zero))
            .GetDetailAsync(_userId, member.Id);

        Assert.NotNull(detail.Metrics);
        Assert.Equal(7.5m, detail.Metrics.Sleep.Value);
        Assert.Equal(localToday, detail.Metrics.Sleep.Series[^1].Date);
        Assert.Null(detail.Metrics.Steps.ChangePercent);
    }

    [Fact]
    public async Task GetDetail_WestOfUtc_KeepsTheEveningAsTheDayInProgress()
    {
        // 20:00 on the 25th in Phoenix (UTC-7) is already the 26th in UTC.
        var member = SeedMember();
        AnchorToCaregiverZone(member, "America/Phoenix");
        var localToday = new DateOnly(2026, 9, 25);
        _activityLogs
            .GetByCardiMemberAndDateRangeAsync(member.Id, localToday.AddDays(-29), localToday)
            .Returns([new ActivityLog { CardiMemberId = member.Id, Date = localToday, Steps = 4000 }]);

        var detail = await CreateSutAt(new DateTimeOffset(2026, 9, 26, 3, 0, 0, TimeSpan.Zero))
            .GetDetailAsync(_userId, member.Id);

        Assert.NotNull(detail.Metrics);
        var steps = detail.Metrics.Steps;
        Assert.Equal(4000m, steps.Value);
        Assert.Null(steps.ChangePercent);
        Assert.Equal("unknown", steps.Status);
        Assert.Equal(localToday, steps.Series[^1].Date);
    }

    [Fact]
    public async Task GetDetail_EastOfUtc_TurnsTheAgeOverAtTheMembersMidnight()
    {
        // 06:00 on the 25th in Brisbane is the member's birthday; UTC is still the 24th.
        var member = SeedMember();
        member.DateOfBirth = new DateOnly(1948, 9, 25);
        AnchorToCaregiverZone(member, "Australia/Brisbane");

        var detail = await CreateSutAt(new DateTimeOffset(2026, 9, 24, 20, 0, 0, TimeSpan.Zero))
            .GetDetailAsync(_userId, member.Id);

        Assert.Equal(78, detail.Age);
    }

    /// <summary>
    /// A journal entry is dated on a finished local day. East of UTC that day can be UTC's today,
    /// which used to hand it today's window — whose latest reading was then the journal day's,
    /// not the member's current one. It is a closed period: its series ends on it, and the latest
    /// reading stays the member's own today.
    /// </summary>
    [Fact]
    public async Task GetDetail_EastOfUtc_DrawsLocalYesterdaysJournalWindow()
    {
        var member = SeedMember();
        AnchorToCaregiverZone(member, "Australia/Brisbane");
        var localToday = new DateOnly(2026, 9, 25);
        var localYesterday = localToday.AddDays(-1);
        _activityLogs
            .GetByCardiMemberAndDateRangeAsync(member.Id, localToday.AddDays(-29), localToday)
            .Returns([new ActivityLog { CardiMemberId = member.Id, Date = localToday, Steps = 300 }]);
        _activityLogs
            .GetByCardiMemberAndDateRangeAsync(member.Id, localYesterday.AddDays(-29), localYesterday)
            .Returns([new ActivityLog { CardiMemberId = member.Id, Date = localYesterday, Steps = 6100 }]);

        var detail = await CreateSutAt(new DateTimeOffset(2026, 9, 24, 20, 0, 0, TimeSpan.Zero))
            .GetDetailAsync(_userId, member.Id, seriesEndsOn: localYesterday);

        Assert.NotNull(detail.Metrics);
        Assert.Equal(300m, detail.Metrics.Steps.Value);
        Assert.Equal(localYesterday, detail.Metrics.Steps.Series[^1].Date);
        Assert.Equal(6100m, detail.Metrics.Steps.Series[^1].Value);
    }

    [Fact]
    public async Task GetDetail_DecryptsMedicalNotes()
    {
        var member = SeedMember();

        var detail = await CreateSut().GetDetailAsync(_userId, member.Id);

        Assert.Equal("Pacemaker fitted 2019", detail.MedicalNotes);
        Assert.Equal("Jane Doe", detail.EmergencyContactName);
        Assert.Equal(RelationshipType.Parent, detail.Relationship);
        Assert.Equal(78, detail.Age);
    }

    [Fact]
    public async Task GetDetail_ReturnsLegacyPlaintextNotes_WhenStoredBeforeEncryption()
    {
        // Rows written before medical notes were encrypted must still be readable rather
        // than failing the whole screen.
        var member = SeedMember(encryptedNotes: "Written before encryption");

        var detail = await CreateSut().GetDetailAsync(_userId, member.Id);

        Assert.Equal("Written before encryption", detail.MedicalNotes);
    }

    [Fact]
    public async Task GetDetail_RequiresViewAccess()
    {
        var member = SeedMember();
        _access.RequireViewAccessAsync(_userId, member.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new KeyNotFoundException("CardiMember not found")));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => CreateSut().GetDetailAsync(_userId, member.Id));
    }

    [Fact]
    public async Task GetDetail_Throws_WhenMemberRemoved()
    {
        var member = SeedMember(isActive: false);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => CreateSut().GetDetailAsync(_userId, member.Id));
    }

    [Fact]
    public async Task GetDetail_ReturnsNullLastSynced_WhenMemberHasNoDevices()
    {
        // Zero connected devices is a supported state (a member added but not yet paired).
        // LastSyncedAt falls through to Max over an empty sequence, which yields null for a
        // nullable selector rather than throwing — pinned here because it reads like a bug.
        var member = SeedMember();
        _devices.GetActiveByCardiMemberIdAsync(member.Id).Returns([]);

        var detail = await CreateSut().GetDetailAsync(_userId, member.Id);

        Assert.Null(detail.LastSyncedAt);
        Assert.Equal(0, detail.ConnectedDeviceCount);
        Assert.Equal("red", detail.DataFreshness);
        Assert.Contains("Margaret", detail.DataFreshnessMessage);
    }

    [Fact]
    public async Task GetDetail_ReportsGreenFreshness_WhenAssessmentCoversLatestSync()
    {
        var synced = DateTime.UtcNow.AddMinutes(-20);
        var member = SeedMember();
        member.LastSyncDate = synced;
        _realtimeAssessments.GetLatestAsync(member.Id, Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessment
            {
                CardiMemberId = member.Id,
                GeneratedAtUtc = synced.AddMinutes(5),
            });

        var detail = await CreateSut().GetDetailAsync(_userId, member.Id);

        Assert.Equal(synced, detail.LastSyncedAt);
        Assert.Equal("green", detail.DataFreshness);
        Assert.Equal("Data processed", detail.DataFreshnessMessage);
    }

    [Fact]
    public async Task GetDetail_ReportsElapsedPauseAsNotPaused()
    {
        var member = SeedMember(pausedUntil: DateTime.UtcNow.AddHours(-1));

        var detail = await CreateSut().GetDetailAsync(_userId, member.Id);

        Assert.False(detail.MonitoringPaused);
        Assert.Null(detail.MonitoringPausedUntil);
    }

    // ── M1-14 update ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_EncryptsMedicalNotesAndSavesFields()
    {
        var member = SeedMember();

        await CreateSut().UpdateAsync(_userId, member.Id, new UpdateCardiMemberRequest
        {
            FirstName = "Maggie",
            LastName = "Doe-Smith",
            DateOfBirth = member.DateOfBirth,
            RelationshipType = RelationshipType.Parent,
            MedicalNotes = "Now also on lisinopril",
            EmergencyContactName = "John Doe",
            EmergencyContactPhone = "+441234567892",
            AlertSensitivity = AlertSensitivity.High,
        });

        Assert.Equal("Maggie", member.FirstName);
        Assert.Equal("Doe-Smith", member.LastName);
        Assert.Equal("enc(Now also on lisinopril)", member.MedicalNotes);
        Assert.Equal("John Doe", member.EmergencyContactName);
        Assert.Equal(AlertSensitivity.High, member.AlertSensitivity);
    }

    [Fact]
    public async Task Update_ClearsMedicalNotes_WhenEmptied()
    {
        var member = SeedMember();

        await CreateSut().UpdateAsync(_userId, member.Id, new UpdateCardiMemberRequest
        {
            FirstName = member.FirstName,
            LastName = member.LastName,
            DateOfBirth = member.DateOfBirth,
            RelationshipType = RelationshipType.Parent,
            MedicalNotes = "   ",
        });

        Assert.Null(member.MedicalNotes);
    }

    // ── health background review date ───────────────────────────────────────────

    [Fact]
    public async Task Update_DatesTheBackground_WhenTheNotesActuallyChange()
    {
        var member = SeedMember();
        member.MedicalNotesReviewedAtUtc = null;

        await CreateSut().UpdateAsync(_userId, member.Id, new UpdateCardiMemberRequest
        {
            FirstName = member.FirstName,
            LastName = member.LastName,
            DateOfBirth = member.DateOfBirth,
            RelationshipType = RelationshipType.Parent,
            MedicalNotes = "Pacemaker fitted 2019. Now also on lisinopril",
        });

        Assert.NotNull(member.MedicalNotesReviewedAtUtc);
    }

    /// <summary>
    /// The one this date exists for. Every client save carries the whole form, so a caregiver
    /// editing an emergency contact resends the notes untouched — and a date that moved for that
    /// would be certifying a background nobody had looked at since it was written.
    /// </summary>
    [Fact]
    public async Task Update_LeavesTheDateAlone_WhenAnUnchangedFormEchoesTheNotesBack()
    {
        var member = SeedMember();
        var reviewedLongAgo = DateTime.UtcNow.AddYears(-1);
        member.MedicalNotesReviewedAtUtc = reviewedLongAgo;

        await CreateSut().UpdateAsync(_userId, member.Id, new UpdateCardiMemberRequest
        {
            FirstName = member.FirstName,
            LastName = member.LastName,
            DateOfBirth = member.DateOfBirth,
            RelationshipType = RelationshipType.Parent,
            // Exactly what SeedMember has on file, the way a form that never showed the field
            // sends it back.
            MedicalNotes = "Pacemaker fitted 2019",
            EmergencyContactName = "Someone Else",
        });

        Assert.Equal(reviewedLongAgo, member.MedicalNotesReviewedAtUtc);
        Assert.Equal("Someone Else", member.EmergencyContactName);
    }

    /// <summary>
    /// Legacy rows read back as their own stored plaintext (see <c>Reveal</c>), so the comparison
    /// has to hold for them too — otherwise every unrelated edit to a pre-encryption member
    /// re-dates a background nobody read.
    /// </summary>
    [Fact]
    public async Task Update_LeavesTheDateAlone_WhenLegacyPlaintextNotesComeBackUnchanged()
    {
        var member = SeedMember(encryptedNotes: "Written before encryption");
        var reviewedLongAgo = DateTime.UtcNow.AddYears(-1);
        member.MedicalNotesReviewedAtUtc = reviewedLongAgo;

        await CreateSut().UpdateAsync(_userId, member.Id, new UpdateCardiMemberRequest
        {
            FirstName = member.FirstName,
            LastName = member.LastName,
            DateOfBirth = member.DateOfBirth,
            RelationshipType = RelationshipType.Parent,
            MedicalNotes = "Written before encryption",
        });

        Assert.Equal(reviewedLongAgo, member.MedicalNotesReviewedAtUtc);
        // Still re-stored encrypted, which is what every write to a legacy row does.
        Assert.Equal("enc(Written before encryption)", member.MedicalNotes);
    }

    [Fact]
    public async Task Update_ClearsTheDate_WhenTheNotesAreEmptied()
    {
        var member = SeedMember();
        member.MedicalNotesReviewedAtUtc = DateTime.UtcNow.AddDays(-3);

        await CreateSut().UpdateAsync(_userId, member.Id, new UpdateCardiMemberRequest
        {
            FirstName = member.FirstName,
            LastName = member.LastName,
            DateOfBirth = member.DateOfBirth,
            RelationshipType = RelationshipType.Parent,
            MedicalNotes = "   ",
        });

        Assert.Null(member.MedicalNotesReviewedAtUtc);
    }

    [Fact]
    public async Task ConfirmMedicalNotes_DatesTheBackgroundWithoutTouchingTheNotes()
    {
        var member = SeedMember();
        member.MedicalNotesReviewedAtUtc = DateTime.UtcNow.AddYears(-1);

        var detail = await CreateSut().ConfirmMedicalNotesAsync(_userId, member.Id);

        Assert.Equal("enc(Pacemaker fitted 2019)", member.MedicalNotes);
        Assert.NotNull(member.MedicalNotesReviewedAtUtc);
        Assert.True(member.MedicalNotesReviewedAtUtc > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(member.MedicalNotesReviewedAtUtc, detail.MedicalNotesReviewedAtUtc);
    }

    /// <summary>
    /// Confirming changes no text, so it is the one write that could touch a pre-encryption row
    /// and leave its PHI in the clear — every other path re-stores the notes encrypted as a side
    /// effect of saving what was typed.
    /// </summary>
    [Fact]
    public async Task ConfirmMedicalNotes_MigratesLegacyPlaintextNotesForward()
    {
        var member = SeedMember(encryptedNotes: "Written before encryption");

        await CreateSut().ConfirmMedicalNotesAsync(_userId, member.Id);

        Assert.Equal("enc(Written before encryption)", member.MedicalNotes);
    }

    /// <summary>
    /// And only those rows. Re-encrypting sound ciphertext would put a fresh nonce on the column
    /// every time somebody said "still accurate", for no gain.
    /// </summary>
    [Fact]
    public async Task ConfirmMedicalNotes_DoesNotReEncryptNotesThatAreAlreadyCiphertext()
    {
        var member = SeedMember();

        await CreateSut().ConfirmMedicalNotesAsync(_userId, member.Id);

        Assert.Equal("enc(Pacemaker fitted 2019)", member.MedicalNotes);
    }

    [Fact]
    public async Task ConfirmMedicalNotes_RefusesWhenThereIsNoBackgroundToConfirm()
    {
        var member = SeedMember(encryptedNotes: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSut().ConfirmMedicalNotesAsync(_userId, member.Id));

        Assert.Null(member.MedicalNotesReviewedAtUtc);
    }

    [Fact]
    public async Task Update_RecordsSex_WhenTheFormSuppliedOne()
    {
        // The correction path for the members created while M1-04 still hardcoded the value.
        var member = SeedMember();
        member.Gender = Gender.PreferNotToSay;

        await CreateSut().UpdateAsync(_userId, member.Id, new UpdateCardiMemberRequest
        {
            FirstName = member.FirstName,
            LastName = member.LastName,
            DateOfBirth = member.DateOfBirth,
            Gender = Gender.Male,
        });

        Assert.Equal(Gender.Male, member.Gender);
    }

    [Fact]
    public async Task Update_LeavesSexAlone_WhenTheFormDidNotSendOne()
    {
        // The whole reason this one field is nullable on an otherwise full-replacement request.
        // A client without the picker — an older build, or any caller editing a phone number —
        // must not silently undo a stated sex and take the reference range with it.
        var member = SeedMember();
        member.Gender = Gender.Female;

        await CreateSut().UpdateAsync(_userId, member.Id, new UpdateCardiMemberRequest
        {
            FirstName = member.FirstName,
            LastName = member.LastName,
            DateOfBirth = member.DateOfBirth,
            Gender = null,
        });

        Assert.Equal(Gender.Female, member.Gender);
    }

    [Fact]
    public async Task Update_RequiresManageAccess()
    {
        var member = SeedMember();
        _access.RequireManageAccessAsync(_userId, member.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new KeyNotFoundException("CardiMember not found")));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => CreateSut().UpdateAsync(
            _userId, member.Id, new UpdateCardiMemberRequest { FirstName = "Nope", DateOfBirth = member.DateOfBirth }));

        Assert.Equal("Margaret Doe", member.FullName);
    }

    // ── M1-13 pause / resume ────────────────────────────────────────────────────

    [Fact]
    public async Task Pause_SetsBoundedWindowAndReason()
    {
        var member = SeedMember();

        var state = await CreateSut().PauseMonitoringAsync(
            _userId, member.Id, new PauseMonitoringRequest { DurationHours = 24, Reason = " Travelling " });

        Assert.True(state.MonitoringPaused);
        Assert.Equal("Travelling", state.MonitoringPauseReason);
        Assert.NotNull(member.MonitoringPausedUntil);
        Assert.InRange(
            member.MonitoringPausedUntil!.Value,
            DateTime.UtcNow.AddHours(23).AddMinutes(58),
            DateTime.UtcNow.AddHours(24).AddMinutes(2));
    }

    [Fact]
    public async Task Resume_ClearsPauseWindowAndReason()
    {
        var member = SeedMember(pausedUntil: DateTime.UtcNow.AddHours(12));
        member.MonitoringPauseReason = "Travelling";

        var state = await CreateSut().ResumeMonitoringAsync(_userId, member.Id);

        Assert.False(state.MonitoringPaused);
        Assert.Null(member.MonitoringPausedUntil);
        Assert.Null(member.MonitoringPauseReason);
    }

    [Fact]
    public async Task Pause_RequiresManageAccess()
    {
        var member = SeedMember();
        _access.RequireManageAccessAsync(_userId, member.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new KeyNotFoundException("CardiMember not found")));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => CreateSut().PauseMonitoringAsync(
            _userId, member.Id, new PauseMonitoringRequest { DurationHours = 24 }));

        Assert.Null(member.MonitoringPausedUntil);
    }

    // ── M1-13 removal ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_DeactivatesMemberLinksAndDevices_AndDiscardsTokens()
    {
        var member = SeedMember();
        var link = new UserCardiMember { UserId = _userId, CardiMemberId = member.Id, IsActive = true };
        var connection = new DeviceConnection
        {
            CardiMemberId = member.Id,
            IsActive = true,
            ConnectionStatus = ConnectionStatus.Connected,
            AccessToken = "enc(access)",
            RefreshToken = "enc(refresh)",
            TokenExpiry = DateTime.UtcNow.AddHours(1),
        };
        _links.GetByCardiMemberIdAsync(member.Id).Returns([link]);
        _devices.GetByCardiMemberIdAsync(member.Id).Returns([connection]);

        await CreateSut().RemoveAsync(_userId, member.Id);

        Assert.False(member.IsActive);
        Assert.False(link.IsActive);
        Assert.False(connection.IsActive);
        Assert.Equal(ConnectionStatus.Disconnected, connection.ConnectionStatus);
        // Tokens for a removed member are of no further use and must not be retained.
        Assert.Null(connection.AccessToken);
        Assert.Null(connection.RefreshToken);
        Assert.Null(connection.TokenExpiry);
    }

    // ── Profile photos ──────────────────────────────────────────────────────────

    private const string PhotoBase64 = "aGVsbG8="; // any valid base64 — the processor is faked
    private static readonly byte[] ProcessedJpeg = [0xFF, 0xD8, 0xFF, 0x01];

    private void SetupPhotoPipeline(string uploadedObjectName = "members/x/new.jpg")
    {
        _photoProcessor.Process(Arg.Any<ReadOnlyMemory<byte>>()).Returns(ProcessedJpeg);
        _photoStorage.UploadAsync(Arg.Any<Guid>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(uploadedObjectName);
    }

    [Fact]
    public async Task Create_WithPhoto_ProcessesUploadsAndStoresObjectName()
    {
        CardiMember? savedMember = null;
        await _members.AddAsync(Arg.Do<CardiMember>(m => savedMember = m));
        SetupPhotoPipeline();
        var request = BuildRequest();
        request.PhotoBase64 = PhotoBase64;

        await CreateSut().CreateCardiMemberAsync(_organizationId, _userId, request);

        Assert.Equal("members/x/new.jpg", savedMember!.PhotoObjectName);
        // Uploaded under the member's own id, and only the processed bytes — never the upload.
        await _photoStorage.Received(1).UploadAsync(
            savedMember.Id,
            Arg.Is<ReadOnlyMemory<byte>>(m => m.ToArray().SequenceEqual(ProcessedJpeg)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithPhoto_DeletesTheUploadedObject_WhenTheCreateFails()
    {
        // The upload happens before the member row exists. If the row never lands, the object
        // must not be left behind for nothing to point at.
        SetupPhotoPipeline("members/x/orphan.jpg");
        _unitOfWork.SaveChangesAsync().Returns(
            Task.FromResult(1),
            Task.FromException<int>(new InvalidOperationException("link save failed")));
        var request = BuildRequest();
        request.PhotoBase64 = PhotoBase64;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSut().CreateCardiMemberAsync(_organizationId, _userId, request));

        await _photoStorage.Received(1).DeleteAsync("members/x/orphan.jpg", Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).RollbackTransactionAsync();
    }

    [Fact]
    public async Task Create_WithPhoto_StillRethrowsTheCreateFailure_WhenTheDeleteFailsToo()
    {
        SetupPhotoPipeline("members/x/orphan.jpg");
        _unitOfWork.SaveChangesAsync().Returns(
            Task.FromResult(1),
            Task.FromException<int>(new InvalidOperationException("link save failed")));
        _photoStorage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("bucket unreachable")));
        var request = BuildRequest();
        request.PhotoBase64 = PhotoBase64;

        // The creation failure is what the caller must see — not the clean-up's.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSut().CreateCardiMemberAsync(_organizationId, _userId, request));
    }

    [Fact]
    public async Task Create_WithPhoto_KeepsTheObject_WhenTheCommitItselfFails()
    {
        // A commit can fail after the server has accepted it. The member may exist and own the
        // photo, so nothing here may delete it — the orphan sweep decides later, from the rows.
        SetupPhotoPipeline("members/x/maybe-committed.jpg");
        CardiMember? savedMember = null;
        await _members.AddAsync(Arg.Do<CardiMember>(m => savedMember = m));
        _unitOfWork.CommitTransactionAsync().Returns(Task.FromException(new TimeoutException("commit ack lost")));
        var request = BuildRequest();
        request.PhotoBase64 = PhotoBase64;

        // The indeterminate outcome takes its own shape, carrying the id the row would have —
        // so the audit entry can still name the member and a reconciler can look for it.
        var outcome = await Assert.ThrowsAsync<CardiMemberCreationOutcomeUnknownException>(
            () => CreateSut().CreateCardiMemberAsync(_organizationId, _userId, request));

        Assert.Equal(savedMember!.Id, outcome.CardiMemberId);
        Assert.IsType<TimeoutException>(outcome.InnerException);
        await _photoStorage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
        await _unitOfWork.Received(1).RollbackTransactionAsync();
    }

    [Fact]
    public async Task Create_RethrowsTheOriginalFailure_WhenItHappensBeforeTheCommit()
    {
        // Only the indeterminate outcome is wrapped. A pre-commit failure is fully rolled back
        // and the caller sees the exception that actually happened.
        _unitOfWork.SaveChangesAsync().Returns(
            Task.FromResult(1),
            Task.FromException<int>(new InvalidOperationException("link save failed")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSut().CreateCardiMemberAsync(_organizationId, _userId, BuildRequest()));

        Assert.Equal("link save failed", ex.Message);
        await _unitOfWork.DidNotReceive().CommitTransactionAsync();
    }

    [Fact]
    public async Task Create_WithPhoto_StillDiscardsTheObject_WhenTheRollbackAlsoFails()
    {
        SetupPhotoPipeline("members/x/orphan.jpg");
        _unitOfWork.SaveChangesAsync().Returns(
            Task.FromResult(1),
            Task.FromException<int>(new InvalidOperationException("link save failed")));
        _unitOfWork.RollbackTransactionAsync().Returns(Task.FromException(new IOException("connection dropped")));
        var request = BuildRequest();
        request.PhotoBase64 = PhotoBase64;

        // The rollback's failure must neither replace the original exception nor skip the clean-up.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSut().CreateCardiMemberAsync(_organizationId, _userId, request));

        await _photoStorage.Received(1).DeleteAsync("members/x/orphan.jpg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithoutPhoto_NeverTouchesPhotoStorage()
    {
        await CreateSut().CreateCardiMemberAsync(_organizationId, _userId, BuildRequest());

        await _photoStorage.DidNotReceiveWithAnyArgs().UploadAsync(default, default, default);
        _photoProcessor.DidNotReceiveWithAnyArgs().Process(default);
    }

    [Fact]
    public async Task Create_WhenPhotoProcessingFails_CreatesNoMember()
    {
        _photoProcessor.Process(Arg.Any<ReadOnlyMemory<byte>>())
            .Returns(_ => throw new InvalidProfilePhotoException("Photos must be JPEG or PNG images."));
        var request = BuildRequest();
        request.PhotoBase64 = PhotoBase64;

        await Assert.ThrowsAsync<InvalidProfilePhotoException>(
            () => CreateSut().CreateCardiMemberAsync(_organizationId, _userId, request));

        // The refusal must land before the insert: no member, no link, no save at all.
        await _members.DidNotReceiveWithAnyArgs().AddAsync(default!);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Create_WithInvalidBase64_IsRefusedAsAnInvalidPhoto()
    {
        var request = BuildRequest();
        request.PhotoBase64 = "not-base64!!!";

        await Assert.ThrowsAsync<InvalidProfilePhotoException>(
            () => CreateSut().CreateCardiMemberAsync(_organizationId, _userId, request));

        await _members.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    private UpdateCardiMemberRequest BuildUpdateRequest(CardiMember member) => new()
    {
        FirstName = member.FirstName,
        LastName = member.LastName,
        DateOfBirth = member.DateOfBirth,
        RelationshipType = RelationshipType.Parent,
    };

    [Fact]
    public async Task Update_WithPhoto_UploadsNewThenDeletesOldOnlyAfterSave()
    {
        var member = SeedMember();
        member.PhotoObjectName = "members/x/old.jpg";
        SetupPhotoPipeline();
        var request = BuildUpdateRequest(member);
        request.PhotoBase64 = PhotoBase64;

        await CreateSut().UpdateAsync(_userId, member.Id, request);

        Assert.Equal("members/x/new.jpg", member.PhotoObjectName);
        // The old blob may only die once the row pointing away from it is durable — deleting
        // first would leave a failed save referencing an object that no longer exists.
        Received.InOrder(() =>
        {
            _unitOfWork.SaveChangesAsync();
            _photoStorage.DeleteAsync("members/x/old.jpg", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Update_WithFirstPhoto_DeletesNothing()
    {
        var member = SeedMember();
        SetupPhotoPipeline();
        var request = BuildUpdateRequest(member);
        request.PhotoBase64 = PhotoBase64;

        await CreateSut().UpdateAsync(_userId, member.Id, request);

        Assert.Equal("members/x/new.jpg", member.PhotoObjectName);
        await _photoStorage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    [Fact]
    public async Task Update_WhenOldBlobDeleteFails_StillSucceeds()
    {
        var member = SeedMember();
        member.PhotoObjectName = "members/x/old.jpg";
        SetupPhotoPipeline();
        _photoStorage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("bucket outage")));
        var request = BuildUpdateRequest(member);
        request.PhotoBase64 = PhotoBase64;

        var detail = await CreateSut().UpdateAsync(_userId, member.Id, request);

        // The caregiver's save already landed; an orphaned blob is a cleanup problem, not theirs.
        Assert.Equal("members/x/new.jpg", member.PhotoObjectName);
        Assert.NotNull(detail);
    }

    [Fact]
    public async Task Update_WhenPhotoProcessingFails_ChangesNothing()
    {
        var member = SeedMember();
        member.PhotoObjectName = "members/x/old.jpg";
        _photoProcessor.Process(Arg.Any<ReadOnlyMemory<byte>>())
            .Returns(_ => throw new InvalidProfilePhotoException("Photos must be JPEG or PNG images."));
        var request = BuildUpdateRequest(member);
        request.FirstName = "Should Not Stick";
        request.PhotoBase64 = PhotoBase64;

        await Assert.ThrowsAsync<InvalidProfilePhotoException>(
            () => CreateSut().UpdateAsync(_userId, member.Id, request));

        Assert.Equal("Margaret Doe", member.FullName);
        Assert.Equal("members/x/old.jpg", member.PhotoObjectName);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Update_RemovePhoto_ClearsAndDeletesAfterSave()
    {
        var member = SeedMember();
        member.PhotoObjectName = "members/x/old.jpg";
        var request = BuildUpdateRequest(member);
        request.RemovePhoto = true;

        await CreateSut().UpdateAsync(_userId, member.Id, request);

        Assert.Null(member.PhotoObjectName);
        Received.InOrder(() =>
        {
            _unitOfWork.SaveChangesAsync();
            _photoStorage.DeleteAsync("members/x/old.jpg", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Update_RemovePhoto_WithNoStoredPhoto_DeletesNothing()
    {
        var member = SeedMember();
        var request = BuildUpdateRequest(member);
        request.RemovePhoto = true;

        await CreateSut().UpdateAsync(_userId, member.Id, request);

        Assert.Null(member.PhotoObjectName);
        await _photoStorage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    [Fact]
    public async Task Update_WithNeitherPhotoNorRemove_LeavesThePhotoAlone()
    {
        // The Gender precedent applied to the photo: an edit that never mentioned the photo
        // must not clear it.
        var member = SeedMember();
        member.PhotoObjectName = "members/x/keep.jpg";

        await CreateSut().UpdateAsync(_userId, member.Id, BuildUpdateRequest(member));

        Assert.Equal("members/x/keep.jpg", member.PhotoObjectName);
        await _photoStorage.DidNotReceiveWithAnyArgs().UploadAsync(default, default, default);
        await _photoStorage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    [Fact]
    public async Task Update_WhenPhotoAndRemoveBothArrive_TheNewPhotoWins()
    {
        // The validator rejects the combination; if one slips through anyway the service must
        // pick deterministically, and "the photo they just chose" is the defensible reading.
        var member = SeedMember();
        member.PhotoObjectName = "members/x/old.jpg";
        SetupPhotoPipeline();
        var request = BuildUpdateRequest(member);
        request.PhotoBase64 = PhotoBase64;
        request.RemovePhoto = true;

        await CreateSut().UpdateAsync(_userId, member.Id, request);

        Assert.Equal("members/x/new.jpg", member.PhotoObjectName);
    }

    [Fact]
    public async Task Remove_ClearsPhotoAndDeletesBlobAfterSave()
    {
        // Tier 1 data must not outlive the membership, even though the removal is a soft delete.
        var member = SeedMember();
        member.PhotoObjectName = "members/x/old.jpg";

        await CreateSut().RemoveAsync(_userId, member.Id);

        Assert.Null(member.PhotoObjectName);
        Received.InOrder(() =>
        {
            _unitOfWork.SaveChangesAsync();
            _photoStorage.DeleteAsync("members/x/old.jpg", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Remove_WhenBlobDeleteFails_StillDeactivatesTheMember()
    {
        var member = SeedMember();
        member.PhotoObjectName = "members/x/old.jpg";
        _photoStorage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("bucket outage")));

        await CreateSut().RemoveAsync(_userId, member.Id);

        Assert.False(member.IsActive);
        Assert.Null(member.PhotoObjectName);
    }

    /// <summary>
    /// Removing a member ends their device grants at the provider too — before the tokens are
    /// cleared, or the grant outlives the membership it was given for.
    /// </summary>
    [Fact]
    public async Task Remove_QueuesEachDeviceGrantForRevocation_WhileTheTokenIsStillThere()
    {
        var member = SeedMember();
        var connection = new DeviceConnection
        {
            Id = Guid.NewGuid(),
            CardiMemberId = member.Id,
            DeviceType = DeviceType.Fitbit,
            IsActive = true,
            RefreshToken = "enc(refresh)",
            HealthUserId = "ACCOUNT_A",
        };
        _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(member.Id).Returns([connection]);

        await CreateSut().RemoveAsync(_userId, member.Id);

        await _unitOfWork.PendingGrantRevocations.Received(1).AddAsync(Arg.Is<PendingGrantRevocation>(r =>
            r.DeviceConnectionId == connection.Id
            && r.Token == "enc(refresh)"
            && r.HealthUserId == "ACCOUNT_A"
            && r.CardiMemberId == member.Id));
        Assert.Null(connection.RefreshToken);
    }

    /// <summary>
    /// Copilot review round 10 on #1290: removal reads the member's devices under the same device
    /// lock a connect takes to store a grant, so a connection cannot commit after the read and
    /// leave its grant unqueued for a removed member.
    /// </summary>
    [Fact]
    public async Task Remove_ReadsTheMembersDevicesUnderTheDeviceLock_AndCommitsThem()
    {
        var member = SeedMember();
        var devices = _unitOfWork.DeviceConnections;

        await CreateSut().RemoveAsync(_userId, member.Id);

        Received.InOrder(() =>
        {
            _unitOfWork.BeginTransactionAsync();
            devices.LockMemberDevicesAsync(member.Id, Arg.Any<CancellationToken>());
            devices.GetByCardiMemberIdAsync(member.Id);
            _unitOfWork.SaveChangesAsync();
            _unitOfWork.CommitTransactionAsync();
        });
    }

    [Fact]
    public async Task Remove_WhenTheSaveFails_RollsBackAndDeletesNoPhoto()
    {
        var member = SeedMember();
        member.PhotoObjectName = "members/x/old.jpg";
        _unitOfWork.SaveChangesAsync().Returns(Task.FromException<int>(new InvalidOperationException("db down")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().RemoveAsync(_userId, member.Id));

        await _unitOfWork.DidNotReceive().CommitTransactionAsync();
        await _unitOfWork.Received(1).RollbackTransactionAsync();
        await _photoStorage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    [Fact]
    public async Task Remove_WithoutPhoto_DeletesNothing()
    {
        var member = SeedMember();

        await CreateSut().RemoveAsync(_userId, member.Id);

        await _photoStorage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    [Fact]
    public async Task GetDetail_ResolvesSignedPhotoUrl_WhenMemberHasPhoto()
    {
        var member = SeedMember();
        member.PhotoObjectName = "members/x/photo.jpg";
        _photoStorage.GetReadUrlAsync("members/x/photo.jpg", Arg.Any<CancellationToken>())
            .Returns("https://signed.example/photo");

        var detail = await CreateSut().GetDetailAsync(_userId, member.Id);

        Assert.Equal("https://signed.example/photo", detail.PhotoUrl);
    }

    [Fact]
    public async Task GetDetail_LeavesPhotoUrlNull_WhenMemberHasNoPhoto()
    {
        var member = SeedMember();

        var detail = await CreateSut().GetDetailAsync(_userId, member.Id);

        Assert.Null(detail.PhotoUrl);
        // No object name means nothing to sign — the storage adapter isn't even consulted.
        await _photoStorage.DidNotReceiveWithAnyArgs().GetReadUrlAsync(default!, default);
    }

    [Fact]
    public async Task Remove_RequiresManageAccess()
    {
        var member = SeedMember();
        _access.RequireManageAccessAsync(_userId, member.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new KeyNotFoundException("CardiMember not found")));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => CreateSut().RemoveAsync(_userId, member.Id));

        Assert.True(member.IsActive);
    }
}
