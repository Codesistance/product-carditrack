using System.Text.Json;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Shared;

/// <summary>
/// Relationship is an optional field on both CardiMember forms. The enum starts at 1, so an
/// omitted relationship would otherwise deserialise to <c>0</c> — not a member of the enum, and
/// rejected downstream as invalid. These lock the "not stated means Other" contract that keeps
/// an unanswered picker from failing the request.
/// </summary>
public class CardiMemberRequestDefaultsTests
{
    private const string CreateWithoutRelationship = """
        {"name":"Ada Lovelace","dateOfBirth":"1948-03-15","gender":1}
        """;

    private const string UpdateWithoutRelationship = """
        {"name":"Ada Lovelace","dateOfBirth":"1948-03-15"}
        """;

    private static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void CreateRequest_DefaultsRelationshipToOther_WhenNotConstructedWithOne()
    {
        Assert.Equal(RelationshipType.Other, new CreateCardiMemberRequest().RelationshipType);
    }

    [Fact]
    public void UpdateRequest_DefaultsRelationshipToOther_WhenNotConstructedWithOne()
    {
        Assert.Equal(RelationshipType.Other, new UpdateCardiMemberRequest().RelationshipType);
    }

    [Fact]
    public void CreateRequest_DefaultsRelationshipToOther_WhenTheFieldIsOmittedFromTheBody()
    {
        var request = JsonSerializer.Deserialize<CreateCardiMemberRequest>(
            CreateWithoutRelationship, Options);

        Assert.Equal(RelationshipType.Other, request!.RelationshipType);
    }

    [Fact]
    public void UpdateRequest_DefaultsRelationshipToOther_WhenTheFieldIsOmittedFromTheBody()
    {
        var request = JsonSerializer.Deserialize<UpdateCardiMemberRequest>(
            UpdateWithoutRelationship, Options);

        Assert.Equal(RelationshipType.Other, request!.RelationshipType);
    }

    /// <summary>
    /// A stated relationship must still win — the default is a fallback, not an override.
    /// </summary>
    [Fact]
    public void UpdateRequest_KeepsAStatedRelationship()
    {
        var request = JsonSerializer.Deserialize<UpdateCardiMemberRequest>(
            """
            {"name":"Ada Lovelace","dateOfBirth":"1948-03-15","relationshipType":2}
            """,
            Options);

        Assert.Equal(RelationshipType.Parent, request!.RelationshipType);
    }
}
