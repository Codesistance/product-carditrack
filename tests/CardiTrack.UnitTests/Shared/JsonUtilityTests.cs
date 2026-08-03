using CardiTrack.Shared.Json;
using Newtonsoft.Json;

namespace CardiTrack.UnitTests.Shared;

public class JsonUtilityTests
{
    private sealed class Widget
    {
        public string? Name { get; set; }
        public int Count { get; set; }
        public List<int>? Values { get; set; }
    }

    private sealed class SnakeDto
    {
        [JsonProperty("access_token")] public string? AccessToken { get; set; }
    }

    [Fact]
    public void Deserialize_CamelCaseJson_MatchesPascalPropertiesCaseInsensitively()
    {
        var widget = JsonUtility.Deserialize<Widget>("""{"name":"probe","count":3}""");

        Assert.Equal("probe", widget.Name);
        Assert.Equal(3, widget.Count);
    }

    [Fact]
    public void Deserialize_SnakeCaseNames_MapViaJsonProperty()
    {
        var dto = JsonUtility.Deserialize<SnakeDto>("""{"access_token":"abc"}""");

        Assert.Equal("abc", dto.AccessToken);
    }

    [Fact]
    public void Deserialize_MalformedJson_ThrowsWithLocation()
    {
        var ex = Assert.Throws<JsonDeserializationException>(
            () => JsonUtility.Deserialize<Widget>("{\"name\": \"a\",\n  count: oops}"));

        Assert.NotEmpty(ex.Errors);
        Assert.Equal(2, ex.Errors[0].LineNumber);
        Assert.Contains("Widget", ex.Message);
    }

    [Fact]
    public void Deserialize_NullLiteral_Throws()
    {
        Assert.Throws<JsonDeserializationException>(() => JsonUtility.Deserialize<Widget>("null"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryDeserialize_EmptyInput_ReportsSingleError(string? json)
    {
        var ok = JsonUtility.TryDeserialize<Widget>(json, out var value, out var errors);

        Assert.False(ok);
        Assert.Null(value);
        Assert.Single(errors);
        Assert.Contains("null or empty", errors[0].Message);
    }

    [Fact]
    public void TryDeserialize_CollectsEveryMemberError_AndKeepsGoodData()
    {
        var ok = JsonUtility.TryDeserialize<Widget>(
            """{"name":"probe","values":[1,"x",3,"y"]}""", out var value, out var errors);

        Assert.False(ok);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Contains("values", e.Path));
        Assert.Equal([1, 3], value!.Values);
    }

    [Fact]
    public void TryDeserialize_DegeneratePayload_TerminatesWithErrors()
    {
        // A payload the reader cannot progress past must not hang — the event cap ends it.
        var ok = JsonUtility.TryDeserialize<Widget>("{\"values\":[1,2 3]}", out _, out var errors);

        Assert.False(ok);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void TryParse_ValidJson_KeepsDatesAsRawStrings()
    {
        var ok = JsonUtility.TryParse("""{"startTime":"2026-08-02T23:10:30.000"}""", out var token, out var errors);

        Assert.True(ok);
        Assert.Empty(errors);
        Assert.Equal("2026-08-02T23:10:30.000", token!.Value<string>("startTime"));
    }

    [Fact]
    public void TryParse_MalformedJson_ReportsLocation()
    {
        var ok = JsonUtility.TryParse("{\"a\": }", out var token, out var errors);

        Assert.False(ok);
        Assert.Null(token);
        Assert.Single(errors);
        Assert.Equal(1, errors[0].LineNumber);
    }

    [Fact]
    public void Parse_MalformedJson_ThrowsWithErrors()
    {
        var ex = Assert.Throws<JsonDeserializationException>(() => JsonUtility.Parse("nope"));

        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public void Deserialize_Failure_ReportsTheFailingPayload()
    {
        var ex = Assert.Throws<JsonDeserializationException>(
            () => JsonUtility.Deserialize<Widget>("{\"count\": oops}"));

        Assert.Equal("{\"count\": oops}", ex.PayloadPreview);
        Assert.Contains("{\"count\": oops}", ex.Message);
    }

    [Fact]
    public void PreviewOf_LongPayload_TruncatesAndReportsTotalLength()
    {
        var json = "{\"name\":\"" + new string('x', 5000) + "\"}";

        var preview = JsonUtility.PreviewOf(json);

        Assert.Equal(1000 + "… (truncated, 5011 chars total)".Length, preview.Length);
        Assert.Contains($"(truncated, {json.Length} chars total)", preview);
    }

    [Fact]
    public void PreviewOf_EmptyInput_SaysSo()
    {
        Assert.Equal("<empty>", JsonUtility.PreviewOf(null));
        Assert.Equal("<empty>", JsonUtility.PreviewOf(""));
    }
}
