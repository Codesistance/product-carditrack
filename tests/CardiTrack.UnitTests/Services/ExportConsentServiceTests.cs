using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Reports;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class ExportConsentServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IExportConsentRepository _consents = Substitute.For<IExportConsentRepository>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();
    private readonly List<ExportConsent> _rows = [];
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public ExportConsentServiceTests()
    {
        _unitOfWork.ExportConsents.Returns(_consents);
        _consents.AddAsync(Arg.Do<ExportConsent>(c => _rows.Add(c)))
            .Returns(Task.CompletedTask);
        _consents.GetForOwnerAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.ArgAt<Guid>(0);
                var owner = ci.ArgAt<Guid>(1);
                return Task.FromResult(_rows.FirstOrDefault(c => c.Id == id && c.OwnerUserId == owner));
            });
        _consents.GetActiveStandingAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var owner = ci.ArgAt<Guid>(0);
                var now = ci.ArgAt<DateTime>(1);
                var sha = ci.ArgAt<string>(2);
                var grant = _rows
                    .Where(c =>
                        c.OwnerUserId == owner
                        && c.ReusedFromConsentId is null
                        && c.RevokedAt is null
                        && c.RememberUntil is { } until
                        && until > now
                        && c.PolicySha256 == sha)
                    .OrderByDescending(c => c.CreatedDate)
                    .FirstOrDefault();
                return Task.FromResult(grant);
            });
        _consents.ListForOwnerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var owner = ci.ArgAt<Guid>(0);
                IReadOnlyList<ExportConsent> list =
                [
                    .. _rows.Where(c => c.OwnerUserId == owner).OrderByDescending(c => c.CreatedDate)
                ];
                return Task.FromResult(list);
            });
        _consents.TryConsumeAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.ArgAt<Guid>(0);
                var owner = ci.ArgAt<Guid>(1);
                var reportId = ci.ArgAt<Guid>(2);
                var now = ci.ArgAt<DateTime>(3);
                var row = _rows.FirstOrDefault(c =>
                    c.Id == id
                    && c.OwnerUserId == owner
                    && c.ConsumedAt is null
                    && c.RevokedAt is null
                    && c.ExpiresAt > now
                    && (c.ReusedFromConsentId is null
                        || _rows.Any(g =>
                            g.Id == c.ReusedFromConsentId
                            && g.OwnerUserId == owner
                            && g.ReusedFromConsentId is null
                            && g.RevokedAt is null
                            && g.RememberUntil is { } until
                            && until > now)));
                if (row is null)
                    return Task.FromResult(false);
                row.ConsumedAt = now;
                row.ReportId = reportId;
                return Task.FromResult(true);
            });
        _consents.TryRevokeAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.ArgAt<Guid>(0);
                var owner = ci.ArgAt<Guid>(1);
                var now = ci.ArgAt<DateTime>(2);
                var row = _rows.FirstOrDefault(c =>
                    c.Id == id
                    && c.OwnerUserId == owner
                    && c.ReusedFromConsentId is null
                    && c.RevokedAt is null
                    && c.RememberUntil is { } until
                    && until > now);
                if (row is null)
                    return Task.FromResult(false);
                row.RevokedAt = now;
                return Task.FromResult(true);
            });
        _consents.RevokeActiveStandingAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var owner = ci.ArgAt<Guid>(0);
                var now = ci.ArgAt<DateTime>(1);
                foreach (var row in _rows.Where(c =>
                             c.OwnerUserId == owner
                             && c.ReusedFromConsentId is null
                             && c.RevokedAt is null
                             && c.RememberUntil is not null))
                {
                    row.RevokedAt = now;
                }

                return Task.CompletedTask;
            });
        _consents.TryLockStandingGrantAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.ArgAt<Guid>(0);
                var owner = ci.ArgAt<Guid>(1);
                var now = ci.ArgAt<DateTime>(2);
                var sha = ci.ArgAt<string>(3);
                var grant = _rows.FirstOrDefault(c =>
                    c.Id == id
                    && c.OwnerUserId == owner
                    && c.ReusedFromConsentId is null
                    && c.RevokedAt is null
                    && c.RememberUntil is { } until
                    && until > now
                    && c.PolicySha256 == sha);
                if (grant is null)
                    return Task.FromResult(false);
                grant.UpdatedDate = now;
                return Task.FromResult(true);
            });
    }

    private ExportConsentService CreateSut() => new(_unitOfWork, _access);

    private RecordExportConsentRequest RecordRequest(
        bool accepted = true,
        bool includeJournals = false,
        ExportConsentRememberFor rememberFor = ExportConsentRememberFor.ThisExport) => new()
    {
        CardiMemberIds = [_memberId],
        DateRangeFrom = new DateOnly(2026, 2, 7),
        DateRangeTo = new DateOnly(2026, 3, 9),
        Format = ReportFormat.Pdf,
        IncludeJournals = includeJournals,
        Method = ExportConsentMethod.Password,
        AcceptedResponsibility = accepted,
        RememberFor = rememberFor
    };

    private GenerateReportRequest GenerateRequest(bool includeJournals = false) => new()
    {
        CardiMemberIds = [_memberId],
        DateRangeFrom = new DateOnly(2026, 2, 7),
        DateRangeTo = new DateOnly(2026, 3, 9),
        Format = ReportFormat.Pdf,
        IncludeJournals = includeJournals,
        ConsentToken = "unused"
    };

    [Fact]
    public async Task RecordAsync_RefusesWhenResponsibilityWasNotAccepted()
    {
        await Assert.ThrowsAsync<ExportConsentException>(() =>
            CreateSut().RecordAsync(_userId, RecordRequest(accepted: false)));

        await _consents.DidNotReceive().AddAsync(Arg.Any<ExportConsent>());
    }

    [Fact]
    public async Task RecordAsync_ChecksAccess_ThenMintsATokenBoundToTheSnapshot()
    {
        var recorded = await CreateSut().RecordAsync(_userId, RecordRequest());

        await _access.Received(1).RequireViewAccessAsync(
            _userId, Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == _memberId),
            Arg.Any<CancellationToken>());

        var row = Assert.Single(_rows);
        Assert.Equal(_userId, row.OwnerUserId);
        Assert.Equal(ExportConsentPolicy.Version, row.PolicyVersion);
        Assert.Equal(ExportConsentPolicy.Sha256Hex, row.PolicySha256);
        Assert.Equal(ExportConsentPolicy.Fingerprint(GenerateRequest()), row.RequestFingerprint);
        Assert.Equal(row.Id.ToString("N"), recorded.ConsentToken);
        Assert.True(row.ExpiresAt > DateTime.UtcNow);
        Assert.Null(row.ConsumedAt);
    }

    [Fact]
    public async Task ConsumeAsync_StampsTheReport_AndRefusesASecondUse()
    {
        var recorded = await CreateSut().RecordAsync(_userId, RecordRequest());
        var reportId = Guid.NewGuid();
        var sut = CreateSut();

        await sut.ConsumeAsync(_userId, recorded.ConsentToken, GenerateRequest(), reportId);

        var row = Assert.Single(_rows);
        Assert.NotNull(row.ConsumedAt);
        Assert.Equal(reportId, row.ReportId);

        var reuse = await Assert.ThrowsAsync<ExportConsentException>(() =>
            sut.ConsumeAsync(_userId, recorded.ConsentToken, GenerateRequest(), Guid.NewGuid()));
        Assert.Contains("already used", reuse.Message);
    }

    [Fact]
    public async Task ConsumeAsync_RefusesAMismatchedSnapshot()
    {
        var recorded = await CreateSut().RecordAsync(_userId, RecordRequest());

        var mismatch = await Assert.ThrowsAsync<ExportConsentException>(() =>
            CreateSut().ConsumeAsync(
                _userId, recorded.ConsentToken, GenerateRequest(includeJournals: true), Guid.NewGuid()));

        Assert.Contains("no longer matches", mismatch.Message);
        Assert.Null(Assert.Single(_rows).ConsumedAt);
    }

    [Fact]
    public async Task RecordAsync_RemembersForAWeek_WithoutSpendingTheStandingGrant()
    {
        var recorded = await CreateSut().RecordAsync(
            _userId, RecordRequest(rememberFor: ExportConsentRememberFor.OneWeek));

        var row = Assert.Single(_rows);
        Assert.Equal(ExportConsentRememberFor.OneWeek, row.RememberFor);
        Assert.NotNull(row.RememberUntil);
        Assert.True(row.RememberUntil > DateTime.UtcNow.AddDays(6));
        Assert.True(row.RememberUntil <= DateTime.UtcNow.AddDays(8));
        Assert.Equal(recorded.RememberUntil!.Value.UtcDateTime, row.RememberUntil.Value, TimeSpan.FromSeconds(1));
        Assert.Null(row.RevokedAt);
        Assert.Null(row.ReusedFromConsentId);
    }

    [Fact]
    public async Task RecordAsync_ANewStandingGrant_StopsThePreviousOne()
    {
        await CreateSut().RecordAsync(_userId, RecordRequest(rememberFor: ExportConsentRememberFor.OneMonth));
        var first = Assert.Single(_rows);

        await CreateSut().RecordAsync(_userId, RecordRequest(rememberFor: ExportConsentRememberFor.OneWeek));

        Assert.Equal(2, _rows.Count);
        Assert.NotNull(first.RevokedAt);
        Assert.Null(_rows[1].RevokedAt);
        Assert.Equal(ExportConsentRememberFor.OneWeek, _rows[1].RememberFor);
    }

    [Fact]
    public async Task RecordAsync_ANewStandingGrant_ReplacesAnExpiredOne()
    {
        await CreateSut().RecordAsync(_userId, RecordRequest(rememberFor: ExportConsentRememberFor.OneWeek));
        var expired = Assert.Single(_rows);
        expired.RememberUntil = DateTime.UtcNow.AddDays(-1);

        await CreateSut().RecordAsync(_userId, RecordRequest(rememberFor: ExportConsentRememberFor.OneMonth));

        Assert.Equal(2, _rows.Count);
        Assert.NotNull(expired.RevokedAt);
        Assert.Null(_rows[1].RevokedAt);
        Assert.Equal(ExportConsentRememberFor.OneMonth, _rows[1].RememberFor);
    }

    [Fact]
    public async Task ReuseAsync_MintsAChild_BoundToTheNewSnapshot_AndNamesTheReuse()
    {
        await CreateSut().RecordAsync(_userId, RecordRequest(rememberFor: ExportConsentRememberFor.TwoWeeks));
        var grant = Assert.Single(_rows);
        grant.ConsumedAt = DateTime.UtcNow;

        var reused = await CreateSut().ReuseAsync(_userId, grant.Id, GenerateRequest(includeJournals: true));

        Assert.True(reused.Reused);
        Assert.Equal(grant.Id, reused.ReusedFromConsentId);
        Assert.Contains("We're using the confirmation you gave on", reused.ReuseNotice);
        Assert.Contains("You can stop this in Settings", reused.ReuseNotice);

        Assert.Equal(2, _rows.Count);
        var child = _rows[1];
        Assert.Equal(grant.Id, child.ReusedFromConsentId);
        Assert.Equal(grant.Method, child.Method);
        Assert.Equal(ExportConsentPolicy.Fingerprint(GenerateRequest(includeJournals: true)), child.RequestFingerprint);
        Assert.NotEqual(grant.Id.ToString("N"), reused.ConsentToken);
    }

    [Fact]
    public async Task ReuseAsync_ThrowsWhenThereIsNothingToReuse()
    {
        await CreateSut().RecordAsync(_userId, RecordRequest());
        var thisExport = Assert.Single(_rows);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().ReuseAsync(_userId, thisExport.Id, GenerateRequest()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().ReuseAsync(_userId, Guid.NewGuid(), GenerateRequest()));
    }

    [Fact]
    public async Task ReuseAsync_ThrowsWhenThePolicyTextHasChanged()
    {
        await CreateSut().RecordAsync(_userId, RecordRequest(rememberFor: ExportConsentRememberFor.OneWeek));
        var grant = Assert.Single(_rows);
        grant.PolicySha256 = new string('0', 64);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().ReuseAsync(_userId, grant.Id, GenerateRequest()));

        Assert.Single(_rows);
    }

    [Fact]
    public async Task ReuseAsync_ThrowsWhenTheIdIsAReuseChild()
    {
        await CreateSut().RecordAsync(_userId, RecordRequest(rememberFor: ExportConsentRememberFor.OneWeek));
        var grant = Assert.Single(_rows);
        var reused = await CreateSut().ReuseAsync(_userId, grant.Id, GenerateRequest());
        var childId = Guid.Parse(reused.ConsentToken);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().ReuseAsync(_userId, childId, GenerateRequest()));
    }

    [Fact]
    public async Task ConsumeAsync_RefusesAReuse_OnceTheGrantWasStopped()
    {
        await CreateSut().RecordAsync(_userId, RecordRequest(rememberFor: ExportConsentRememberFor.OneWeek));
        var grant = Assert.Single(_rows);
        var reused = await CreateSut().ReuseAsync(_userId, grant.Id, GenerateRequest());
        grant.RevokedAt = DateTime.UtcNow;

        var stopped = await Assert.ThrowsAsync<ExportConsentException>(() =>
            CreateSut().ConsumeAsync(_userId, reused.ConsentToken, GenerateRequest(), Guid.NewGuid()));

        Assert.Contains("no longer in force", stopped.Message);
        Assert.Null(_rows[1].ConsumedAt);
    }

    [Fact]
    public async Task ListAsync_MarksAStandingGrantRevocable_AndAReuseAsReused()
    {
        await CreateSut().RecordAsync(_userId, RecordRequest(rememberFor: ExportConsentRememberFor.OneMonth));
        var standing = Assert.Single(_rows);
        await CreateSut().ReuseAsync(_userId, standing.Id, GenerateRequest());

        var history = await CreateSut().ListAsync(_userId);

        Assert.Equal(2, history.Count);
        var reuse = Assert.Single(history, h => h.Reused);
        Assert.False(reuse.CanRevoke);
        Assert.False(reuse.CanReuse);
        Assert.Contains("Reused an earlier confirmation", reuse.Summary);

        var grant = Assert.Single(history, h => h.CanRevoke);
        Assert.True(grant.CanReuse);
        Assert.Contains("In force until", grant.Summary);
    }

    [Fact]
    public async Task RevokeAsync_StopsTheStandingGrant()
    {
        await CreateSut().RecordAsync(_userId, RecordRequest(rememberFor: ExportConsentRememberFor.OneWeek));
        var grant = Assert.Single(_rows);

        await CreateSut().RevokeAsync(_userId, grant.Id);

        Assert.NotNull(grant.RevokedAt);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().ReuseAsync(_userId, grant.Id, GenerateRequest()));
    }

    [Fact]
    public async Task RevokeAsync_UnknownIsNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().RevokeAsync(_userId, Guid.NewGuid()));
    }

    [Fact]
    public async Task ConsumeAsync_RefusesTheOriginalToken_OnceTheGrantWasStopped()
    {
        var recorded = await CreateSut().RecordAsync(
            _userId, RecordRequest(rememberFor: ExportConsentRememberFor.OneWeek));
        var grant = Assert.Single(_rows);

        await CreateSut().RevokeAsync(_userId, grant.Id);

        var stopped = await Assert.ThrowsAsync<ExportConsentException>(() =>
            CreateSut().ConsumeAsync(_userId, recorded.ConsentToken, GenerateRequest(), Guid.NewGuid()));

        Assert.Contains("no longer in force", stopped.Message);
        Assert.Null(grant.ConsumedAt);
    }

    [Fact]
    public async Task ConsumeAsync_RefusesAnExpiredToken()
    {
        var recorded = await CreateSut().RecordAsync(_userId, RecordRequest());
        Assert.Single(_rows).ExpiresAt = DateTime.UtcNow.AddSeconds(-1);

        var expired = await Assert.ThrowsAsync<ExportConsentException>(() =>
            CreateSut().ConsumeAsync(_userId, recorded.ConsentToken, GenerateRequest(), Guid.NewGuid()));

        Assert.Contains("expired", expired.Message);
    }

    [Fact]
    public async Task ConsumeAsync_RefusesAnotherUsersToken()
    {
        var recorded = await CreateSut().RecordAsync(_userId, RecordRequest());

        await Assert.ThrowsAsync<ExportConsentException>(() =>
            CreateSut().ConsumeAsync(
                Guid.NewGuid(), recorded.ConsentToken, GenerateRequest(), Guid.NewGuid()));
    }

    [Fact]
    public async Task ConsumeAsync_RefusesGarbage()
    {
        await Assert.ThrowsAsync<ExportConsentException>(() =>
            CreateSut().ConsumeAsync(_userId, "not-a-token", GenerateRequest(), Guid.NewGuid()));
    }
}
