using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class CardiMemberServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();

    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    public CardiMemberServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.UserCardiMembers.Returns(_links);
    }

    private CardiMemberService CreateSut() => new(_unitOfWork);

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
        Assert.Equal("Pacemaker fitted 2019", savedMember.MedicalNotes);
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
}
