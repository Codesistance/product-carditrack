using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;

namespace CardiTrack.IntegrationTests.Validators;

/// <summary>
/// The first/last name rules on both CardiMember forms, and the legacy path an app build from
/// before the split still takes: sending one <c>Name</c>, which the request splits into the two
/// parts so it meets exactly the same rules.
/// </summary>
public class CardiMemberNameValidatorTests
{
    private readonly CreateCardiMemberValidator _create = new();
    private readonly UpdateCardiMemberValidator _update = new();

    private static readonly DateOnly Dob = new(1955, 6, 15);

    /// <summary>Sends <paramref name="first"/> as given, explicit null included.</summary>
    private static CreateCardiMemberRequest Create(string? first, string? last = null, string? legacy = null) =>
        new() { FirstName = first!, LastName = last, Name = legacy, DateOfBirth = Dob, Gender = Gender.Female };

    private static UpdateCardiMemberRequest Update(string? first, string? last = null, string? legacy = null) =>
        new() { FirstName = first!, LastName = last, Name = legacy, DateOfBirth = Dob };

    /// <summary>What an app build from before the split sends: no firstName/lastName properties at all.</summary>
    private static CreateCardiMemberRequest LegacyCreate(string? legacy) =>
        new() { Name = legacy, DateOfBirth = Dob, Gender = Gender.Female };

    private static UpdateCardiMemberRequest LegacyUpdate(string? legacy) =>
        new() { Name = legacy, DateOfBirth = Dob };

    [Fact]
    public void AcceptsAFirstNameAlone_ForSomeoneKnownByOneName()
    {
        Assert.True(_create.Validate(Create("Arthur")).IsValid);
        Assert.True(_update.Validate(Update("Arthur")).IsValid);
    }

    [Fact]
    public void AcceptsBothParts()
    {
        Assert.True(_create.Validate(Create("Arthur", "Doe")).IsValid);
        Assert.True(_update.Validate(Update("Arthur", "Doe")).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsAMissingFirstName_NamingTheField(string? first)
    {
        var create = _create.Validate(Create(first, "Doe"));
        var update = _update.Validate(Update(first, "Doe"));

        Assert.Contains(create.Errors, e => e.PropertyName == nameof(CreateCardiMemberRequest.FirstName));
        Assert.Contains(update.Errors, e => e.PropertyName == nameof(UpdateCardiMemberRequest.FirstName));
    }

    [Fact]
    public void RejectsOverlongParts()
    {
        var tooLong = new string('a', 101);

        Assert.Contains(_create.Validate(Create(tooLong)).Errors, e => e.PropertyName == nameof(CreateCardiMemberRequest.FirstName));
        Assert.Contains(_create.Validate(Create("Arthur", tooLong)).Errors, e => e.PropertyName == nameof(CreateCardiMemberRequest.LastName));
        Assert.Contains(_update.Validate(Update(tooLong)).Errors, e => e.PropertyName == nameof(UpdateCardiMemberRequest.FirstName));
        Assert.Contains(_update.Validate(Update("Arthur", tooLong)).Errors, e => e.PropertyName == nameof(UpdateCardiMemberRequest.LastName));
    }

    [Fact]
    public void ALegacyClientSendingOnlyName_IsSplitAndValidated()
    {
        var create = LegacyCreate("Mary Ann Smith");
        var update = LegacyUpdate("Mary Ann Smith");

        Assert.Equal(("Mary", "Ann Smith"), (create.FirstName, create.LastName));
        Assert.Equal(("Mary", "Ann Smith"), (update.FirstName, update.LastName));
        Assert.True(_create.Validate(create).IsValid);
        Assert.True(_update.Validate(update).IsValid);
    }

    [Fact]
    public void ALegacyClientSendingABlankName_IsRefusedAsAMissingFirstName()
    {
        var result = _create.Validate(LegacyCreate("  "));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateCardiMemberRequest.FirstName));
    }

    /// <summary>
    /// A blank first name that was sent is a blank first name — refused — not an invitation to
    /// take one from the legacy field instead.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ASentButBlankFirstName_IsRefused_EvenWithALegacyNameBesideIt(string? first)
    {
        var create = _create.Validate(Create(first, legacy: "Mary Ann Smith"));
        var update = _update.Validate(Update(first, legacy: "Mary Ann Smith"));

        Assert.Contains(create.Errors, e => e.PropertyName == nameof(CreateCardiMemberRequest.FirstName));
        Assert.Contains(update.Errors, e => e.PropertyName == nameof(UpdateCardiMemberRequest.FirstName));
    }

    /// <summary>
    /// A current client sends all three — the parts, plus the joined name for an API from before
    /// the split. The parts win; the two are never mixed.
    /// </summary>
    [Fact]
    public void WhenFirstNameIsSent_TheLegacyNameIsIgnoredForBothParts()
    {
        var request = Create("Mary Ann", last: null, legacy: "Someone Else");

        Assert.Equal("Mary Ann", request.FirstName);
        Assert.Null(request.LastName);
    }

    /// <summary>
    /// The distinction the fallback turns on, through the serializer the API actually uses: an
    /// omitted firstName falls back to the legacy name; an explicit null does not.
    /// </summary>
    [Fact]
    public void OnTheWire_OmittedFirstNameFallsBack_ButExplicitNullIsRefused()
    {
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        var omitted = System.Text.Json.JsonSerializer.Deserialize<CreateCardiMemberRequest>(
            """{"name":"Mary Ann Smith","dateOfBirth":"1955-06-15","gender":2}""", web)!;
        var explicitNull = System.Text.Json.JsonSerializer.Deserialize<CreateCardiMemberRequest>(
            """{"firstName":null,"name":"Mary Ann Smith","dateOfBirth":"1955-06-15","gender":2}""", web)!;

        Assert.Equal(("Mary", "Ann Smith"), (omitted.FirstName, omitted.LastName));
        Assert.True(_create.Validate(omitted).IsValid);
        Assert.Contains(_create.Validate(explicitNull).Errors, e => e.PropertyName == nameof(CreateCardiMemberRequest.FirstName));
    }
}
