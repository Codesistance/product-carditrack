using CardiTrack.PipelineJobs.Notifications;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The parser hunts rather than assumes: the notification body has no discovery-document schema,
/// so anything shaped like `users/{id}` anywhere in the JSON counts, and nothing else does.
/// </summary>
public class WebhookNotificationParserTests
{
    [Fact]
    public void FindsUserResourceNames_WhereverTheyLive()
    {
        var ids = WebhookNotificationParser.ExtractHealthUserIds("""
            {
              "user": "users/abc-123",
              "nested": { "userName": "users/def.456" },
              "list": [ { "subject": "users/abc-123" } ]
            }
            """);

        Assert.Equal(2, ids.Count);
        Assert.Contains("abc-123", ids);
        Assert.Contains("def.456", ids);
    }

    // Longer resource paths and near-misses must not match: `users/{id}/dataTypes/...` names a
    // collection under the user, not the user, and a bare id proves nothing.
    [Theory]
    [InlineData("""{ "a": "users/abc/dataTypes/steps" }""")]
    [InlineData("""{ "a": "user/abc" }""")]
    [InlineData("""{ "a": "abc-123" }""")]
    [InlineData("""{ "users": ["abc"] }""")]
    [InlineData("not json at all")]
    [InlineData("42")]
    public void FindsNothing_InNonMatchingBodies(string body)
    {
        Assert.Empty(WebhookNotificationParser.ExtractHealthUserIds(body));
    }

    [Fact]
    public void TopLevelShape_NamesKeysOnly_NeverValues()
    {
        var shape = WebhookNotificationParser.TopLevelShape(
            """{ "user": "users/secret-id", "dataType": "heart-rate" }""");

        Assert.Equal("user,dataType", shape);
        Assert.DoesNotContain("secret-id", shape);
    }

    [Fact]
    public void TopLevelShape_DescribesArrayElements_NeverValues()
    {
        var shape = WebhookNotificationParser.TopLevelShape("""
            [
              { "user": "users/secret-id", "dataType": "heart-rate" },
              { "user": "users/other-secret", "dataType": "steps" }
            ]
            """);

        Assert.Equal("array[2]:user+dataType", shape);
        Assert.DoesNotContain("secret", shape);
    }

    [Fact]
    public void TopLevelShape_DescribesMixedArrayElementShapes()
    {
        var shape = WebhookNotificationParser.TopLevelShape("""["a", 1, { "x": 1 }]""");

        Assert.Equal("array[3]:String|Integer|x", shape);
    }

    [Fact]
    public void TopLevelShape_ReportsEmptyArray()
    {
        Assert.Equal("array[0]", WebhookNotificationParser.TopLevelShape("[]"));
    }
}
