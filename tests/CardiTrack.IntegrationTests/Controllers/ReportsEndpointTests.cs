using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// The export endpoint answers 202 and the client polls the id in the body. A 202 with no body
/// is what shipped in #511: <c>Accepted(Success(result).Value)</c> reads <c>Value</c> off an
/// <see cref="ActionResult{TValue}"/> that only ever had <c>Result</c> set, so the envelope was
/// null, the client threw "The server returned an empty response", and nobody saw it because
/// the plan gate in front of the endpoint let nobody through. This pins the body.
/// </summary>
public class ReportsEndpointTests
{
    private readonly IReportGenerationService _reports = Substitute.For<IReportGenerationService>();
    private readonly IExportConsentService _consent = Substitute.For<IExportConsentService>();
    private readonly IValidator<GenerateReportRequest> _validator = Substitute.For<IValidator<GenerateReportRequest>>();
    private readonly IValidator<RecordExportConsentRequest> _consentValidator =
        Substitute.For<IValidator<RecordExportConsentRequest>>();

    private ReportsController CreateSut()
    {
        var user = Substitute.For<IUserContext>();
        user.IsAuthenticated.Returns(true);
        user.UserId.Returns(Guid.NewGuid());

        _validator.ValidateAsync(Arg.Any<GenerateReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult());
        _consentValidator.ValidateAsync(Arg.Any<RecordExportConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult());

        return new ReportsController(
            user,
            Substitute.For<ILogger<ReportsController>>(),
            _reports,
            _consent,
            _validator,
            _consentValidator,
            new ReuseExportConsentValidator())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static GenerateReportRequest AnyRequest() => new()
    {
        CardiMemberIds = [Guid.NewGuid()],
        DateRangeFrom = new DateOnly(2026, 8, 9),
        DateRangeTo = new DateOnly(2026, 9, 7),
        Format = ReportFormat.Pdf
    };

    [Fact]
    public async Task Generate_Answers202_WithTheQueuedReportInTheEnvelope()
    {
        _reports.GenerateAsync(Arg.Any<Guid>(), Arg.Any<GenerateReportRequest>())
            .Returns(new ReportQueuedResponse
            {
                ReportId = "abc123",
                StatusUrl = "/api/v1/reports/abc123",
                EstimatedReadyInSeconds = 20
            });

        var result = await CreateSut().Generate(AnyRequest(), default);

        var accepted = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);

        var envelope = Assert.IsType<ApiResponse<ReportQueuedResponse>>(accepted.Value);
        Assert.True(envelope.Success);
        Assert.NotNull(envelope.Data);
        Assert.Equal("abc123", envelope.Data.ReportId);
        Assert.Equal("/api/v1/reports/abc123", envelope.Data.StatusUrl);
        Assert.False(string.IsNullOrWhiteSpace(envelope.Message));
    }

    [Fact]
    public async Task RecordConsent_Answers200_WithTheTokenInTheEnvelope()
    {
        _consent.RecordAsync(Arg.Any<Guid>(), Arg.Any<RecordExportConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExportConsentResponse
            {
                ConsentToken = "aabbccdd",
                ExpiresAt = new DateTimeOffset(2026, 9, 9, 12, 2, 0, TimeSpan.Zero)
            });

        var result = await CreateSut().RecordConsent(new RecordExportConsentRequest
        {
            CardiMemberIds = [Guid.NewGuid()],
            DateRangeFrom = new DateOnly(2026, 8, 9),
            DateRangeTo = new DateOnly(2026, 9, 7),
            Format = ReportFormat.Pdf,
            Method = ExportConsentMethod.Password,
            AcceptedResponsibility = true
        }, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<ApiResponse<ExportConsentResponse>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Equal("aabbccdd", envelope.Data!.ConsentToken);
    }

    [Fact]
    public async Task RecordConsent_RetriesOnce_WhenTheStandingGrantIndexCollides()
    {
        _consent.RecordAsync(Arg.Any<Guid>(), Arg.Any<RecordExportConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new Microsoft.EntityFrameworkCore.DbUpdateException(),
                _ => new ExportConsentResponse
                {
                    ConsentToken = "retried",
                    ExpiresAt = new DateTimeOffset(2026, 9, 9, 12, 2, 0, TimeSpan.Zero)
                });

        var result = await CreateSut().RecordConsent(new RecordExportConsentRequest
        {
            CardiMemberIds = [Guid.NewGuid()],
            DateRangeFrom = new DateOnly(2026, 8, 9),
            DateRangeTo = new DateOnly(2026, 9, 7),
            Format = ReportFormat.Pdf,
            Method = ExportConsentMethod.Password,
            AcceptedResponsibility = true,
            RememberFor = ExportConsentRememberFor.OneWeek
        }, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<ApiResponse<ExportConsentResponse>>(ok.Value);
        Assert.Equal("retried", envelope.Data!.ConsentToken);
        await _consent.Received(2).RecordAsync(
            Arg.Any<Guid>(), Arg.Any<RecordExportConsentRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReuseConsent_Answers200_WithTheReuseNamedInTheEnvelope()
    {
        _consent.ReuseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<GenerateReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExportConsentResponse
            {
                ConsentToken = "reusedtoken",
                ExpiresAt = new DateTimeOffset(2026, 9, 11, 12, 2, 0, TimeSpan.Zero),
                Reused = true,
                ReuseNotice = "We're using the confirmation you gave on 4 Sep 2026. It stays in force until 18 Sep 2026. You can stop this in Settings."
            });

        var result = await CreateSut().ReuseConsent(Guid.NewGuid(), AnyRequest(), default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<ApiResponse<ExportConsentResponse>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.True(envelope.Data!.Reused);
        Assert.Contains("We're using the confirmation", envelope.Message);
    }

    [Fact]
    public async Task ListConsents_Answers200_WithTheHistory()
    {
        var id = Guid.NewGuid();
        _consent.ListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([
                new ExportConsentHistoryItem
                {
                    Id = id,
                    RecordedAt = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero),
                    Method = ExportConsentMethod.Biometric,
                    RememberFor = ExportConsentRememberFor.OneWeek,
                    CanRevoke = true,
                    CanReuse = true,
                    Summary = "In force until 18 Sep 2026 · fingerprint or face unlock",
                    Reused = false
                }
            ]);

        var result = await CreateSut().ListConsents(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<ApiResponse<IReadOnlyList<ExportConsentHistoryItem>>>(ok.Value);
        Assert.True(envelope.Success);
        var row = Assert.Single(envelope.Data!);
        Assert.Equal(id, row.Id);
        Assert.True(row.CanRevoke);
    }

    [Fact]
    public async Task RevokeConsent_Answers200()
    {
        var result = await CreateSut().RevokeConsent(Guid.NewGuid(), default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.True(envelope.Success);
        await _consent.Received(1).RevokeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
