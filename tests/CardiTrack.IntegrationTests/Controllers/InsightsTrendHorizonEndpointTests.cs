using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// How the trend endpoint reads its <c>?horizon=</c>. Two properties worth pinning: a caller that
/// sends nothing still gets the read this endpoint has always returned, and a value that is not
/// one of the three named horizons is refused rather than quietly served as one.
/// </summary>
/// <remarks>
/// Every case goes through a request with a real query string rather than by passing a string to
/// the action, and that is the point rather than ceremony. ASP.NET Core binds <c>?horizon=</c> to
/// <c>null</c> for a nullable string, so an omitted parameter and a supplied-empty one arrive at
/// the action identically — a test that called the action with <c>""</c> would pass while the
/// endpoint served the default, which is exactly the gap this file was rewritten to close.
/// </remarks>
public class InsightsTrendHorizonEndpointTests
{
    private readonly IHealthInsightService _insights = Substitute.For<IHealthInsightService>();
    private readonly IDigestQueryService _digests = Substitute.For<IDigestQueryService>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public InsightsTrendHorizonEndpointTests()
    {
        _userContext.IsAuthenticated.Returns(true);
        _userContext.UserId.Returns(_userId);
        _insights.GetTrendAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<TrendHorizon>(), Arg.Any<CancellationToken>())
            .Returns(new TrendInsightResponse
            {
                CardiMemberId = _memberId,
                Narrative = "Steady across the stretch.",
                KeyFindings = [],
                GeneratedAt = DateTimeOffset.UtcNow,
            });
    }

    [Fact]
    public async Task NoHorizonMeansTheRollingRead()
    {
        // The compatibility case: every caller that predates the horizons sends no parameter, and
        // must keep getting exactly what it got before.
        await GetTrend(queryString: "");

        await _insights.Received(1).GetTrendAsync(
            _userId, _memberId, TrendHorizon.Rolling, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnrelatedQueryParameterIsNotAHorizon()
    {
        await GetTrend("?somethingElse=weekly");

        await _insights.Received(1).GetTrendAsync(
            _userId, _memberId, TrendHorizon.Rolling, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("rolling", TrendHorizon.Rolling)]
    [InlineData("weekly", TrendHorizon.Weekly)]
    [InlineData("monthly", TrendHorizon.Monthly)]
    [InlineData("Weekly", TrendHorizon.Weekly)]
    [InlineData("MONTHLY", TrendHorizon.Monthly)]
    public async Task EachNamedHorizonReachesTheService(string supplied, TrendHorizon expected)
    {
        await GetTrend($"?horizon={supplied}");

        await _insights.Received(1).GetTrendAsync(
            _userId, _memberId, expected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASuppliedButEmptyHorizonIsRefused()
    {
        // The case only a real query string can reach. The binder hands the action null for
        // "?horizon=", identically to an omitted parameter, so presence is read off the query —
        // a caller who wrote the key and no value made a mistake and is told so.
        var result = await GetTrend("?horizon=");

        AssertBadRequest(result);
    }

    [Theory]
    // The numeric form is what a name comparison exists to refuse: Enum.TryParse accepts it and
    // Enum.IsDefined then agrees, so "2" would quietly have meant Weekly — a caller's typo picking
    // a horizon for them.
    [InlineData("?horizon=2")]
    [InlineData("?horizon=0")]
    [InlineData("?horizon=99")]
    [InlineData("?horizon=yearly")]
    [InlineData("?horizon=daily")]
    [InlineData("?horizon=%20%20")]
    public async Task AnythingElseIsRefusedRatherThanServedAsRolling(string queryString)
    {
        AssertBadRequest(await GetTrend(queryString));
    }

    private void AssertBadRequest(ActionResult<ApiResponse<TrendInsightResponse>> result)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeActionResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, status.StatusCode);

        // And nothing was read on the member's behalf for a request that made no sense.
        _insights.DidNotReceive().GetTrendAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<TrendHorizon>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Invokes the action behind a request carrying <paramref name="queryString"/>, binding the
    /// <c>horizon</c> parameter the way the framework would: absent when the key is missing or its
    /// value is empty.
    /// </summary>
    private Task<ActionResult<ApiResponse<TrendInsightResponse>>> GetTrend(string queryString)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(queryString);

        var controller = new InsightsController(
            _userContext, Substitute.For<ILogger<InsightsController>>(), _insights, _digests)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        // What ASP.NET Core's binder produces for a nullable string: null when the key is absent,
        // and null again when it is present but empty. The action has to tell those apart from the
        // query itself, and passing the bound value here is what keeps this test honest about it.
        var bound = httpContext.Request.Query.TryGetValue("horizon", out var raw)
            && !StringValues.IsNullOrEmpty(raw)
                ? raw.ToString()
                : null;

        return controller.GetTrend(_memberId, bound, CancellationToken.None);
    }
}
