using CardiTrack.PipelineJobs.Notifications;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The parser hunts rather than assumes: the notification body has no discovery-document schema,
/// so a `healthUserId` property — the form live traffic uses — or a string rooted at `users/{id}`
/// yields that id, wherever in the JSON it sits, and nothing else does.
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

    // The shape live traffic actually carries, reproduced from the aggregator's own logged
    // description of a dropped notification. This is the case that matters most in the file: an
    // earlier fix assumed the body named wearers the way the Subscription resource does, and
    // shipped without any test against the real payload, so it passed while changing nothing.
    [Fact]
    public void FindsTheUserId_InTheLiveNotificationShape()
    {
        var ids = WebhookNotificationParser.ExtractHealthUserIds("""
            [
              {
                "data": {
                  "version": "v4",
                  "clientProvidedSubscriptionName": "carditrack-dev",
                  "healthUserId": "AbC123xyz",
                  "operation": "UPDATE",
                  "dataType": "steps",
                  "intervals": [
                    {
                      "civilDateTimeInterval": { "startTime": "2026-08-13T00:00:00" },
                      "civilIso8601TimeInterval": "2026-08-13/2026-08-14",
                      "physicalTimeInterval": { "startTime": "2026-08-13T00:00:00Z" }
                    }
                  ]
                }
              },
              {
                "data": {
                  "version": "v4",
                  "clientProvidedSubscriptionName": "carditrack-dev",
                  "healthUserId": "AbC123xyz",
                  "operation": "UPDATE",
                  "dataType": "sleep",
                  "intervals": [
                    { "civilDateTimeInterval": { "startTime": "2026-08-13T00:00:00" } }
                  ]
                }
              }
            ]
            """);

        // One wearer, four data types in the batch — one sync, not four.
        Assert.Equal(["AbC123xyz"], ids);
    }

    [Fact]
    public void FindsEveryDistinctUser_AcrossABatch()
    {
        var ids = WebhookNotificationParser.ExtractHealthUserIds("""
            [
              { "data": { "healthUserId": "one", "dataType": "steps" } },
              { "data": { "healthUserId": "two", "dataType": "steps" } },
              { "data": { "healthUserId": "one", "dataType": "sleep" } }
            ]
            """);

        Assert.Equal(2, ids.Count);
        Assert.Contains("one", ids);
        Assert.Contains("two", ids);
    }

    // Nesting has already changed once under this parser. Matching the property wherever it
    // appears — and whatever its casing — is what stops the next envelope change being another
    // silent multi-day outage.
    [Theory]
    [InlineData("""{ "healthUserId": "abc" }""")]
    [InlineData("""{ "data": { "healthUserId": "abc" } }""")]
    [InlineData("""{ "a": { "b": { "c": { "healthUserId": "abc" } } } }""")]
    [InlineData("""{ "HealthUserId": "abc" }""")]
    [InlineData("""{ "healthuserid": "abc" }""")]
    public void FindsTheUserId_WhereverAndHoweverCasedItAppears(string body)
    {
        Assert.Equal(["abc"], WebhookNotificationParser.ExtractHealthUserIds(body));
    }

    [Theory]
    [InlineData("""{ "healthUserId": "" }""")]
    [InlineData("""{ "healthUserId": "   " }""")]
    [InlineData("""{ "healthUserId": null }""")]
    [InlineData("""{ "healthUserId": 42 }""")]
    [InlineData("""{ "healthUserId": { "nested": "abc" } }""")]
    public void IgnoresAHealthUserIdThatIsNotAUsableString(string body)
    {
        Assert.Empty(WebhookNotificationParser.ExtractHealthUserIds(body));
    }

    // A resource rooted at a user still names that user, and that is the only question this
    // parser exists to answer. `users/{id}/dataTypes/{type}` is the documented form of
    // Subscription.dataTypes — retained as a secondary form, though live notifications do not
    // use it.
    [Theory]
    [InlineData("""{ "a": "users/abc/dataTypes/steps" }""", "abc")]
    [InlineData("""{ "a": "users/abc-123/dataTypes/heart-rate" }""", "abc-123")]
    [InlineData("""{ "a": "users/def.456/dataTypes/sleep/extra" }""", "def.456")]
    [InlineData("""{ "a": "users/abc" }""", "abc")]
    public void FindsTheUserId_InResourcesRootedAtAUser(string body, string expected)
    {
        Assert.Equal([expected], WebhookNotificationParser.ExtractHealthUserIds(body));
    }

    // One notification naming several data types for one wearer is one wearer, not several syncs.
    [Fact]
    public void CollapsesTheSameUser_AcrossABatchOfDataTypes()
    {
        var ids = WebhookNotificationParser.ExtractHealthUserIds("""
            [
              { "data": "users/abc/dataTypes/steps" },
              { "data": "users/abc/dataTypes/sleep" },
              { "data": "users/abc/dataTypes/heart-rate" },
              { "data": "users/xyz/dataTypes/steps" }
            ]
            """);

        Assert.Equal(2, ids.Count);
        Assert.Contains("abc", ids);
        Assert.Contains("xyz", ids);
    }

    // Near-misses still must not match — the relaxation is to the trailing path only, never to
    // the `users/` root or to a bare id, which proves nothing about whose data moved.
    [Theory]
    [InlineData("""{ "a": "user/abc" }""")]
    [InlineData("""{ "a": "abc-123" }""")]
    [InlineData("""{ "a": "users/" }""")]
    [InlineData("""{ "a": "users//dataTypes/steps" }""")]
    [InlineData("""{ "a": "notusers/abc" }""")]
    [InlineData("""{ "users": ["abc"] }""")]
    [InlineData("not json at all")]
    [InlineData("42")]
    public void FindsNothing_InNonMatchingBodies(string body)
    {
        Assert.Empty(WebhookNotificationParser.ExtractHealthUserIds(body));
    }

    [Fact]
    public void TopLevelShape_NamesKeysAndTypes_NeverValues()
    {
        var shape = WebhookNotificationParser.TopLevelShape(
            """{ "user": "users/secret-id", "dataType": "heart-rate" }""");

        Assert.Equal("user:String,dataType:String", shape);
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

        Assert.Equal("array[2]:dataType:String+user:String", shape);
        Assert.DoesNotContain("secret", shape);
    }

    // The whole point of deepening the description: the single-level version reported
    // `array[4]:data` for every live notification, which confirmed batching and left the contents
    // of `data` unknown — a second deploy just to see one level further.
    [Fact]
    public void TopLevelShape_DescribesWhatHangsOffANestedKey()
    {
        var shape = WebhookNotificationParser.TopLevelShape("""
            [
              { "data": { "user": "users/secret-id", "dataType": "steps" } }
            ]
            """);

        Assert.Equal("array[1]:data:{user:String,dataType:String}", shape);
        Assert.DoesNotContain("secret-id", shape);
        Assert.DoesNotContain("steps", shape);
    }

    // Depth is bounded to keep nesting from exploding the description — past the limit the keys are still
    // named, their contents are not.
    [Fact]
    public void TopLevelShape_StopsDescendingAtTheDepthLimit()
    {
        var shape = WebhookNotificationParser.TopLevelShape(
            """{ "a": { "b": { "c": { "d": { "e": 1 } } } } }""");

        Assert.Equal("a:{b:{c}}", shape);
        Assert.DoesNotContain("\"e\"", shape);
    }

    // Two objects with the same fields in a different declaration order are the same shape —
    // JSON key order carries no meaning, so this must collapse to one entry, not two.
    [Fact]
    public void TopLevelShape_CollapsesArrayElementShapes_RegardlessOfPropertyOrder()
    {
        var shape = WebhookNotificationParser.TopLevelShape("""
            [
              { "user": "users/a", "dataType": "heart-rate" },
              { "dataType": "steps", "user": "users/b" }
            ]
            """);

        Assert.Equal("array[2]:dataType:String+user:String", shape);
    }

    [Fact]
    public void TopLevelShape_DescribesMixedArrayElementShapes()
    {
        var shape = WebhookNotificationParser.TopLevelShape("""["a", 1, { "x": 1 }]""");

        Assert.Equal("array[3]:Integer|String|x:Integer", shape);
    }

    [Fact]
    public void TopLevelShape_ReportsEmptyArray()
    {
        Assert.Equal("array[0]", WebhookNotificationParser.TopLevelShape("[]"));
    }
}
