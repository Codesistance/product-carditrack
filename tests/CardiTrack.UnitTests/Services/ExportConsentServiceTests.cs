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
        _consents.TryConsumeAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.ArgAt<Guid>(0);
                var owner = ci.ArgAt<Guid>(1);
                var reportId = ci.ArgAt<Guid>(2);
                var now = ci.ArgAt<DateTime>(3);
                var row = _rows.FirstOrDefault(c =>
                    c.Id == id && c.OwnerUserId == owner && c.ConsumedAt is null && c.ExpiresAt > now);
                if (row is null)
                    return Task.FromResult(false);
                row.ConsumedAt = now;
                row.ReportId = reportId;
                return Task.FromResult(true);
            });
    }

    private ExportConsentService CreateSut() => new(_unitOfWork, _access);

    private RecordExportConsentRequest RecordRequest(
        bool accepted = true,
        bool includeJournals = false) => new()
    {
        CardiMemberIds = [_memberId],
        DateRangeFrom = new DateOnly(2026, 2, 7),
        DateRangeTo = new DateOnly(2026, 3, 9),
        Format = ReportFormat.Pdf,
        IncludeJournals = includeJournals,
        Method = ExportConsentMethod.Password,
        AcceptedResponsibility = accepted
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
