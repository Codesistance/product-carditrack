using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Notifications;
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
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();

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
        _alerts.GetByCardiMemberAsync(Arg.Any<Guid>(), Arg.Any<bool>()).Returns([]);

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

    private CardiMemberService CreateSut() => new(_unitOfWork, _access, _encryption, new NoOpNotificationGapResolver());

    private static CreateCardiMemberRequest BuildRequest() => new()
    {
        Name = "Margaret Doe",
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
    public async Task Create_PersistsMemberAndCaregiverLink()
    {
        CardiMember? savedMember = null;
        UserCardiMember? savedLink = null;
        await _members.AddAsync(Arg.Do<CardiMember>(m => savedMember = m));
        await _links.AddAsync(Arg.Do<UserCardiMember>(l => savedLink = l));

        await CreateSut().CreateCardiMemberAsync(_organizationId, _userId, BuildRequest());

        Assert.NotNull(savedMember);
        Assert.Equal(_organizationId, savedMember!.OrganizationId);
        Assert.Equal("Margaret Doe", savedMember.Name);
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
            Name = "Margaret Doe",
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
        var member = new CardiMember { OrganizationId = _organizationId, Name = "Margaret Doe" };
        _members.GetByIdAsync(member.Id).Returns(member);
        _links.GetByCardiMemberIdAsync(member.Id).Returns([]);

        var response = await CreateSut().GetByIdAsync(member.Id);

        Assert.Equal(RelationshipType.Other, response!.Relationship);
        Assert.False(response.IsPrimaryCaregiver);
    }

    [Fact]
    public async Task GetByOrganizationId_MapsEachMemberWithItsRelationship()
    {
        var first = new CardiMember { OrganizationId = _organizationId, Name = "Margaret Doe" };
        var second = new CardiMember { OrganizationId = _organizationId, Name = "Arthur Doe" };
        _members.GetByOrganizationIdAsync(_organizationId).Returns([first, second]);
        _links.GetByCardiMemberIdAsync(first.Id).Returns(
        [
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = first.Id,
                RelationshipType = RelationshipType.Parent,
                IsPrimaryCaregiver = true,
            },
        ]);
        _links.GetByCardiMemberIdAsync(second.Id).Returns([]);

        var responses = await CreateSut().GetByOrganizationIdAsync(_organizationId);

        Assert.Equal(2, responses.Count);
        Assert.Equal(RelationshipType.Parent, responses[0].Relationship);
        Assert.Equal(RelationshipType.Other, responses[1].Relationship);
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
            Name = "Margaret Doe",
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
            Name = "Margaret A. Doe",
            DateOfBirth = member.DateOfBirth,
            RelationshipType = RelationshipType.Parent,
            MedicalNotes = "Now also on lisinopril",
            EmergencyContactName = "John Doe",
            EmergencyContactPhone = "+441234567892",
            AlertSensitivity = AlertSensitivity.High,
        });

        Assert.Equal("Margaret A. Doe", member.Name);
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
            Name = member.Name,
            DateOfBirth = member.DateOfBirth,
            RelationshipType = RelationshipType.Parent,
            MedicalNotes = "   ",
        });

        Assert.Null(member.MedicalNotes);
    }

    [Fact]
    public async Task Update_RecordsSex_WhenTheFormSuppliedOne()
    {
        // The correction path for the members created while M1-04 still hardcoded the value.
        var member = SeedMember();
        member.Gender = Gender.PreferNotToSay;

        await CreateSut().UpdateAsync(_userId, member.Id, new UpdateCardiMemberRequest
        {
            Name = member.Name,
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
            Name = member.Name,
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
            _userId, member.Id, new UpdateCardiMemberRequest { Name = "Nope", DateOfBirth = member.DateOfBirth }));

        Assert.Equal("Margaret Doe", member.Name);
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
