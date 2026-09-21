using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// How the trend endpoint reads its <c>?horizon=</c>, exercised through the action rather than
/// against the enum. Two properties worth pinning: a caller that sends nothing still gets the read
/// this endpoint has always returned, and a value that is not one of the three named horizons is
/// refused rather than quietly served as one.
/// </summary>
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
        await CreateSut().GetTrend(_memberId, horizon: null, CancellationToken.None);

        await _insights.Received(1).GetTrendAsync(
            _userId, _memberId, TrendHorizon.Rolling, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("rolling", TrendHorizon.Rolling)]
    [InlineData("weekly", TrendHorizon.Weekly)]
    [InlineData("monthly", TrendHorizon.Monthly)]
    [InlineData("Weekly", TrendHorizon.Weekly)]
    [InlineData("  MONTHLY  ", TrendHorizon.Monthly)]
    public async Task EachNamedHorizonReachesTheService(string supplied, TrendHorizon expected)
    {
        await CreateSut().GetTrend(_memberId, supplied, CancellationToken.None);

        await _insights.Received(1).GetTrendAsync(
            _userId, _memberId, expected, Arg.Any<CancellationToken>());
    }

    [Theory]
    // The numeric form is what a name comparison exists to refuse: Enum.TryParse accepts it and
    // Enum.IsDefined then agrees, so "2" would quietly have meant Weekly — a caller's typo picking
    // a horizon for them.
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("99")]
    // Supplied-but-empty is a malformed request, not a request for the default. Absent means the
    // caller did not ask; "?horizon=" means they meant to ask and sent nothing.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yearly")]
    [InlineData("daily")]
    public async Task AnythingElseIsRefusedRatherThanServedAsRolling(string supplied)
    {
        var result = await CreateSut().GetTrend(_memberId, supplied, CancellationToken.None);

        var status = Assert.IsAssignableFrom<IStatusCodeActionResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, status.StatusCode);

        // And nothing was read on the member's behalf for a request that made no sense.
        await _insights.DidNotReceive().GetTrendAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<TrendHorizon>(), Arg.Any<CancellationToken>());
    }

    private InsightsController CreateSut() =>
        new(_userContext, Substitute.For<ILogger<InsightsController>>(), _insights, _digests);
}
