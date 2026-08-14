using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The read side of the generated summaries. Two things worth pinning: it is relationship-scoped
/// like every health-data read, and now that summaries are recomputed rather than written once a
/// day, a history read is bounded rather than "however many exist".
/// </summary>
public class DigestQueryServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IDigestRepository _digests = Substitute.For<IDigestRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public DigestQueryServiceTests()
    {
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.Digests.Returns(_digests);

        _links.GetByUserIdAsync(_userId).Returns([
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = _memberId,
                IsActive = true,
                CanViewHealthData = true,
            },
        ]);
        _links.GetByUserIdAsync(_outsiderId).Returns([]);
    }

    private DigestQueryService CreateSut() => new(_unitOfWork, new CardiMemberAccessService(_unitOfWork));

    [Fact]
    public async Task GetDigest_ReturnsTheMembersLatestSummary_HeadlineIncluded()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = new DateOnly(2026, 8, 12),
                Audience = DigestAudience.Family,
                Headline = "A settled night",
                Text = "A quiet, steady day.",
                GeneratedAtUtc = new DateTime(2026, 8, 12, 7, 0, 0, DateTimeKind.Utc),
            });

        var result = await CreateSut().GetDigestAsync(_userId, _memberId, localDate: null);

        Assert.NotNull(result);
        Assert.Equal("A settled night", result.Headline);
        Assert.Equal("A quiet, steady day.", result.Text);
    }

    [Fact]
    public async Task GetDigest_CarriesTheSuggestionGeneratedWithTheSummary()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = new DateOnly(2026, 8, 12),
                Audience = DigestAudience.Family,
                Text = "A quiet, steady day.",
                Suggestion = "Ask how they slept when you call tonight",
                GeneratedAtUtc = new DateTime(2026, 8, 12, 7, 0, 0, DateTimeKind.Utc),
            });

        var result = await CreateSut().GetDigestAsync(_userId, _memberId, localDate: null);

        Assert.NotNull(result);
        Assert.Equal("Ask how they slept when you call tonight", result.Suggestion);
    }

    /// <summary>
    /// A summary written before suggestions existed has none, and one whose suggestion did not
    /// survive validation has none either. Both reach the apps as null so the section is hidden,
    /// rather than as an empty string they would have to decide how to render.
    /// </summary>
    [Fact]
    public async Task GetDigest_LeavesSuggestionNull_WhenTheSummaryHasNone()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = new DateOnly(2026, 8, 12),
                Audience = DigestAudience.Family,
                Text = "A quiet, steady day.",
                GeneratedAtUtc = new DateTime(2026, 8, 12, 7, 0, 0, DateTimeKind.Utc),
            });

        var result = await CreateSut().GetDigestAsync(_userId, _memberId, localDate: null);

        Assert.NotNull(result);
        Assert.Null(result.Suggestion);
    }

    /// <summary>
    /// The hyphenated wire vocabulary (watch / check-in / concerning / act-now), not a bare
    /// enum-name lowercase — the same words the AI prompt itself asks the model for.
    /// </summary>
    [Theory]
    [InlineData(DigestUrgency.Watch, "watch")]
    [InlineData(DigestUrgency.CheckIn, "check-in")]
    [InlineData(DigestUrgency.Concerning, "concerning")]
    [InlineData(DigestUrgency.ActNow, "act-now")]
    public async Task GetDigest_CarriesTheUrgencyTier_AsItsHyphenatedWireValue(
        DigestUrgency urgency, string expectedWireValue)
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = new DateOnly(2026, 8, 12),
                Audience = DigestAudience.Family,
                Text = "A quiet, steady day.",
                Urgency = urgency,
                GeneratedAtUtc = new DateTime(2026, 8, 12, 7, 0, 0, DateTimeKind.Utc),
            });

        var result = await CreateSut().GetDigestAsync(_userId, _memberId, localDate: null);

        Assert.NotNull(result);
        Assert.Equal(expectedWireValue, result.Urgency);
    }

    [Fact]
    public async Task GetDigest_LeavesUrgencyNull_WhenTheSummaryHasNone()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = new DateOnly(2026, 8, 12),
                Audience = DigestAudience.Family,
                Text = "A quiet, steady day.",
                GeneratedAtUtc = new DateTime(2026, 8, 12, 7, 0, 0, DateTimeKind.Utc),
            });

        var result = await CreateSut().GetDigestAsync(_userId, _memberId, localDate: null);

        Assert.NotNull(result);
        Assert.Null(result.Urgency);
    }

    /// <summary>
    /// A day now holds every recomputation, so asking for one names the day and takes the current
    /// summary of it — not whichever row the store happened to return first.
    /// </summary>
    [Fact]
    public async Task GetDigest_ForADate_AsksForTheLatestOfThatDay()
    {
        var date = new DateOnly(2026, 8, 11);

        await CreateSut().GetDigestAsync(_userId, _memberId, date);

        await _digests.Received(1).GetLatestByDateAsync(
            _memberId, date, DigestAudience.Family, Arg.Any<CancellationToken>());
        await _digests.DidNotReceive().GetLatestAsync(
            Arg.Any<Guid>(), Arg.Any<DigestAudience>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDigest_ReturnsNull_WhenNoneHasBeenGeneratedYet()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns((DigestEntry?)null);

        Assert.Null(await CreateSut().GetDigestAsync(_userId, _memberId, localDate: null));
    }

    // Recomputation makes an uncapped history read a genuinely unbounded one.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(5000, DigestQueryService.MaxHistoryLimit)]
    public async Task GetHistory_ClampsTheLimit(int requested, int expected)
    {
        await CreateSut().GetHistoryAsync(_userId, _memberId, requested);

        await _digests.Received(1).GetHistoryAsync(
            _memberId, DigestAudience.Family, expected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Throws_ForAUserNotLinkedToTheMember()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().GetHistoryAsync(_outsiderId, _memberId, 10));

        await _digests.DidNotReceive().GetHistoryAsync(
            Arg.Any<Guid>(), Arg.Any<DigestAudience>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
