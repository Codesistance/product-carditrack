using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;

namespace CardiTrack.IntegrationTests.Validators;

/// <summary>
/// What the two CardiMember forms will accept as a sex. Neither validator had a test of any kind
/// before the M1-04 form began asking, which is part of why the field went unnoticed while every
/// member in the system held the same unusable value.
/// </summary>
public class CardiMemberSexValidatorTests
{
    private readonly CreateCardiMemberValidator _create = new();
    private readonly UpdateCardiMemberValidator _update = new();

    // Old enough to clear the 18–120 rule, so a failure here is always about sex.
    private static readonly DateOnly Dob = new(1955, 6, 15);

    private static CreateCardiMemberRequest CreateRequest(Gender gender) =>
        new() { Name = "Margaret Doe", DateOfBirth = Dob, Gender = gender };

    private static UpdateCardiMemberRequest UpdateRequest(Gender? gender) =>
        new() { Name = "Margaret Doe", DateOfBirth = Dob, Gender = gender };

    [Theory]
    [InlineData(Gender.Male)]
    [InlineData(Gender.Female)]
    public void Create_AcceptsTheTwoSexesTheFormOffers(Gender gender)
    {
        Assert.True(_create.Validate(CreateRequest(gender)).IsValid);
    }

    /// <summary>
    /// Not offered by the picker, but still a member of the enum and still the stored value for
    /// every member who predates the field — so the create path must not reject it outright.
    /// </summary>
    [Fact]
    public void Create_StillAcceptsNotStated()
    {
        Assert.True(_create.Validate(CreateRequest(Gender.PreferNotToSay)).IsValid);
    }

    /// <summary>
    /// 3 was <c>Other</c> until it was retired. A client still sending it is out of date, and the
    /// row it would write is one no clinical path can read — so it is refused rather than stored.
    /// </summary>
    [Fact]
    public void Create_RejectsTheRetiredOtherValue()
    {
        var result = _create.Validate(CreateRequest((Gender)3));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateCardiMemberRequest.Gender));
    }

    [Fact]
    public void Create_RejectsAnUnsetSex()
    {
        // 0 is what an omitted field deserialises to, and is not a member of the enum. Unlike
        // RelationshipType, it is not quietly read as "not stated": sex is asked for outright.
        Assert.False(_create.Validate(CreateRequest(default)).IsValid);
    }

    [Theory]
    [InlineData(Gender.Male)]
    [InlineData(Gender.Female)]
    public void Update_AcceptsASuppliedSex(Gender gender)
    {
        Assert.True(_update.Validate(UpdateRequest(gender)).IsValid);
    }

    /// <summary>
    /// The case that keeps a client without the picker from clearing a stated sex: omitting the
    /// field is valid, and the service reads it as "leave the stored value alone".
    /// </summary>
    [Fact]
    public void Update_AcceptsAnOmittedSex()
    {
        Assert.True(_update.Validate(UpdateRequest(null)).IsValid);
    }

    [Fact]
    public void Update_RejectsTheRetiredOtherValue()
    {
        var result = _update.Validate(UpdateRequest((Gender)3));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.StartsWith("Gender", StringComparison.Ordinal));
    }

    /// <summary>
    /// A supplied 0 is a client bug, not an omission — <see cref="UpdateCardiMemberRequest.Gender"/>
    /// is nullable precisely so that "say nothing" has a way to be expressed properly.
    /// </summary>
    [Fact]
    public void Update_RejectsAnExplicitlyUnsetSex()
    {
        Assert.False(_update.Validate(UpdateRequest(default(Gender))).IsValid);
    }
}
