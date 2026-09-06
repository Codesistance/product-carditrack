using System.Collections.Concurrent;
using System.Text;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class ReportGenerationServiceTests
{
    private readonly IGenerativeAiService _generativeAi = Substitute.For<IGenerativeAiService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IDeviceConnectionRepository _devices = Substitute.For<IDeviceConnectionRepository>();
    private readonly InMemoryReportRepository _reports = new();
    private readonly InMemoryReportStorage _storage = new();
    private readonly RecordingRenderer _renderer = new(ReportFormat.Pdf);
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();
    private readonly ReportStorageOptions _options = new();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public ReportGenerationServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.DeviceConnections.Returns(_devices);
        _unitOfWork.Reports.Returns(_reports);

        // Defaults: known member, no logs, no alerts, AI returns a fixed narrative.
        _members.GetByIdAsync(_memberId).Returns(new CardiMember { Id = _memberId, Name = "Margaret Doe" });
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);
        _alerts.GetByCardiMemberAsync(_memberId, false).Returns([]);
        _devices.GetByCardiMemberIdAsync(Arg.Any<Guid>()).Returns([]);
        _generativeAi.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Generated report body.");
    }

    /// <summary>
    /// Background generation resolves its own scope, so the fake hands back the same substitutes
    /// the test set up — which is also what makes the "survives a restart" test below meaningful:
    /// a second service instance shares only the repository and the bucket, never in-memory state.
    /// </summary>
    private IServiceScopeFactory BuildScopeFactory()
    {
        var provider = new StubServiceProvider(new Dictionary<Type, object>
        {
            [typeof(IUnitOfWork)] = _unitOfWork,
            [typeof(IGenerativeAiService)] = _generativeAi,
            [typeof(IEnumerable<IReportRenderer>)] = new IReportRenderer[] { _renderer }
        });

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    private ReportGenerationService CreateSut() =>
        new(_unitOfWork, _storage, _access, _options, BuildScopeFactory(),
            Substitute.For<ILogger<ReportGenerationService>>());

    /// <summary>Makes the access service refuse the given member, as it does for an unlinked user.</summary>
    private void DenyAccessTo(Guid memberId)
    {
        _access.RequireViewAccessAsync(
                Arg.Any<Guid>(),
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids != null && ids.Contains(memberId)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new KeyNotFoundException("CardiMember not found")));
    }

    private GenerateReportRequest BuildRequest(
        ReportFormat format = ReportFormat.Pdf,
        bool includeMetrics = true,
        bool includeAlerts = true) => new()
        {
            CardiMemberIds = [_memberId],
            DateRangeFrom = new DateOnly(2026, 2, 7),
            DateRangeTo = new DateOnly(2026, 3, 9),
            Format = format,
            IncludeMetrics = includeMetrics,
            IncludeAlerts = includeAlerts
        };

    /// <summary>Generation runs on an unobserved background task; poll until it lands.</summary>
    private async Task<ReportStatusResponse> WaitForTerminalStatusAsync(ReportGenerationService sut, string reportId)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = await sut.GetStatusAsync(_userId, reportId);
            if (status is not null && status.Status != ReportStatus.Pending)
                return status;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Report {reportId} never left Pending.");
    }

    /// <summary>Holds the AI call open so the report stays Pending until released.</summary>
    private TaskCompletionSource<string> HoldGeneration()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _generativeAi.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);
        return gate;
    }

    // ── GenerateAsync — queueing ────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_ReturnsQueuedResponse_AndRecordsPendingStatus()
    {
        var gate = HoldGeneration();

        var queued = await CreateSut().GenerateAsync(_userId, BuildRequest());

        Assert.NotEmpty(queued.ReportId);
        Assert.Equal(ReportStatus.Pending, queued.Status);
        Assert.Equal($"/api/v1/reports/{queued.ReportId}", queued.StatusUrl);
        Assert.Equal(30, queued.EstimatedReadyInSeconds);

        var status = await CreateSut().GetStatusAsync(_userId, queued.ReportId);
        Assert.NotNull(status);
        Assert.Equal(ReportStatus.Pending, status!.Status);
        Assert.Equal([_memberId.ToString()], status.Metadata!.CardiMembers);
        Assert.Equal(new DateOnly(2026, 2, 7), status.Metadata.DateRangeFrom);
        Assert.Equal(new DateOnly(2026, 3, 9), status.Metadata.DateRangeTo);

        gate.SetResult("done");
    }

    [Fact]
    public async Task GenerateAsync_IssuesDistinctReportIds()
    {
        var sut = CreateSut();

        var first = await sut.GenerateAsync(_userId, BuildRequest());
        var second = await sut.GenerateAsync(_userId, BuildRequest());

        Assert.NotEqual(first.ReportId, second.ReportId);
    }

    [Fact]
    public async Task GenerateAsync_EchoesTheRequestedFormat_Immediately()
    {
        // The cache-backed design never copied Format into the stored status, so the API's own
        // docs recorded `format` as "currently always null".
        var gate = HoldGeneration();

        var queued = await CreateSut().GenerateAsync(_userId, BuildRequest(ReportFormat.FhirR4));
        var status = await CreateSut().GetStatusAsync(_userId, queued.ReportId);

        Assert.Equal(ReportFormat.FhirR4, status!.Format);

        gate.SetResult("done");
    }

    // ── Durability ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadyReport_SurvivesTheServiceInstanceThatMadeIt()
    {
        // The regression the whole durable-storage change exists for: under the cache-backed
        // design an API restart lost finished reports along with in-flight ones.
        var generator = CreateSut();
        var queued = await generator.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(generator, queued.ReportId);

        var afterRestart = CreateSut();

        var status = await afterRestart.GetStatusAsync(_userId, queued.ReportId);
        Assert.Equal(ReportStatus.Ready, status!.Status);

        var (content, _, _) = await afterRestart.DownloadAsync(_userId, queued.ReportId);
        Assert.NotEmpty(content);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNull_OnceThePublishedWindowHasPassed()
    {
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        // Expiry is decided by the timestamp, not by whether the cleanup sweep has run — a report
        // must never outlive the window it advertised just because the worker is between passes.
        var report = _reports.Single();
        report.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);

        Assert.Null(await sut.GetStatusAsync(_userId, queued.ReportId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sut.DownloadAsync(_userId, queued.ReportId));
    }

    [Fact]
    public async Task GenerateAsync_StampsExpiryAtQueueTime_NotAtCompletion()
    {
        // A slow generation must not quietly shorten the window the caregiver was promised.
        var gate = HoldGeneration();
        var before = DateTime.UtcNow;

        await CreateSut().GenerateAsync(_userId, BuildRequest());
        var report = _reports.Single();

        Assert.InRange(
            report.ExpiresAt,
            before.Add(_options.Retention).AddSeconds(-5),
            DateTime.UtcNow.Add(_options.Retention).AddSeconds(5));

        gate.SetResult("done");
    }

    [Fact]
    public async Task DownloadAsync_ReportsNotFound_WhenTheObjectIsGoneButTheRowRemains()
    {
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        // A partially-completed cleanup, or a lifecycle rule that moved first.
        _storage.Clear();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sut.DownloadAsync(_userId, queued.ReportId));
    }

    // ── Background generation ───────────────────────────────────────────────────

    [Fact]
    public async Task BackgroundGeneration_MarksReportReady_WithDownloadDetails()
    {
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());

        var status = await WaitForTerminalStatusAsync(sut, queued.ReportId);

        Assert.Equal(ReportStatus.Ready, status.Status);
        Assert.Equal(RecordingRenderer.ContentTypeValue, status.ContentType);
        Assert.Equal(_renderer.LastRendered!.Content.Length, status.FileSizeBytes);
        Assert.Equal($"/api/v1/reports/{queued.ReportId}/download", status.DownloadUrl);
        Assert.NotNull(status.CompletedAt);
        Assert.NotNull(status.DownloadExpiresAt);
    }

    [Fact]
    public async Task BackgroundGeneration_MarksReportFailed_WhenAiThrows_WithoutLeakingDetails()
    {
        _generativeAi.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("provider quota exceeded"));
        var sut = CreateSut();

        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        var status = await WaitForTerminalStatusAsync(sut, queued.ReportId);

        Assert.Equal(ReportStatus.Failed, status.Status);
        Assert.NotNull(status.Error);
        Assert.DoesNotContain("quota", status.Error);
    }

    [Fact]
    public async Task BackgroundGeneration_MarksReportFailed_WhenNoRendererHandlesTheFormat()
    {
        // HL7 v2 is a defined enum member for MVP 2. The request validator refuses it at the
        // edge; if one ever reaches here it must fail the report, not throw into nothing.
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest(ReportFormat.Hl7V2));

        var status = await WaitForTerminalStatusAsync(sut, queued.ReportId);

        Assert.Equal(ReportStatus.Failed, status.Status);
    }

    [Fact]
    public async Task BackgroundGeneration_StoresBytesUnderAnOwnerScopedObjectName()
    {
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        // Owner-prefixed so an erasure sweep can find everything belonging to one account.
        var objectName = Assert.Single(_storage.ObjectNames);
        Assert.Equal($"reports/{_userId}/{queued.ReportId}.{RecordingRenderer.ExtensionValue}", objectName);
    }

    // ── Filenames ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_NamesTheFileAfterTheMemberAndPeriod()
    {
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        var (_, _, fileName) = await sut.DownloadAsync(_userId, queued.ReportId);

        Assert.Equal(
            $"carditrack-export-margaret-doe-20260207-20260309.{RecordingRenderer.ExtensionValue}",
            fileName);
    }

    [Theory]
    [InlineData("Margaret O'Doe-Smith", "margaret-o-doe-smith")]
    [InlineData("玛格丽特", "member")]
    [InlineData("../../etc/passwd", "etc-passwd")]
    public async Task DownloadAsync_BuildsAFilenameSafeSubject(string memberName, string expectedSlug)
    {
        // The filename reaches a Content-Disposition header, so a name carrying a quote or a path
        // separator must not carry through to it.
        _members.GetByIdAsync(_memberId).Returns(new CardiMember { Id = _memberId, Name = memberName });
        var sut = CreateSut();

        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);
        var (_, _, fileName) = await sut.DownloadAsync(_userId, queued.ReportId);

        Assert.StartsWith($"carditrack-export-{expectedSlug}-", fileName);
    }

    [Fact]
    public async Task DownloadAsync_NamesTheFileByCount_ForMultipleMembers()
    {
        var secondMemberId = Guid.NewGuid();
        SetUpMember(secondMemberId, "John Roe");
        var sut = CreateSut();

        var queued = await sut.GenerateAsync(_userId, new GenerateReportRequest
        {
            CardiMemberIds = [_memberId, secondMemberId],
            DateRangeFrom = new DateOnly(2026, 2, 7),
            DateRangeTo = new DateOnly(2026, 3, 9),
            Format = ReportFormat.Pdf
        });
        await WaitForTerminalStatusAsync(sut, queued.ReportId);
        var (_, _, fileName) = await sut.DownloadAsync(_userId, queued.ReportId);

        // A filename is not the place to list a family.
        Assert.StartsWith("carditrack-export-2-members-", fileName);
    }

    // ── Pseudonymisation ────────────────────────────────────────────────────────
    //
    // Reports go to the general provider (Gemini's consumer endpoint, outside the Google Cloud
    // BAA). Readings on their own are not identifying; a name attached to them is.

    [Fact]
    public async Task Prompt_CarriesNoPatientName()
    {
        var prompt = await CapturePromptAsync(BuildRequest());

        Assert.DoesNotContain("Margaret Doe", prompt);
        Assert.Contains("Patient A", prompt);
    }

    [Fact]
    public async Task Prompt_LabelsEachMemberDistinctly()
    {
        var secondMemberId = Guid.NewGuid();
        SetUpMember(secondMemberId, "John Roe");

        var prompt = await CapturePromptAsync(new GenerateReportRequest
        {
            CardiMemberIds = [_memberId, secondMemberId],
            DateRangeFrom = new DateOnly(2026, 2, 7),
            DateRangeTo = new DateOnly(2026, 3, 9),
            Format = ReportFormat.Pdf
        });

        Assert.DoesNotContain("Margaret Doe", prompt);
        Assert.DoesNotContain("John Roe", prompt);
        Assert.Contains("Patient A", prompt);
        Assert.Contains("Patient B", prompt);
    }

    [Fact]
    public async Task NarrativeHandedToTheRenderer_HasRealNamesRestored()
    {
        _generativeAi.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Patient A is walking less than usual. Check in with Patient A this week.");
        var sut = CreateSut();

        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        // Every occurrence, not just the first — the caregiver reads a report about a person.
        Assert.Equal(
            "Margaret Doe is walking less than usual. Check in with Margaret Doe this week.",
            _renderer.LastNarrative);
    }

    [Fact]
    public async Task Narrative_RestoresLongLabelsWithoutPrefixCollision()
    {
        // 27 members, so labels run past "Patient Z" into "Patient AA" — a naive replace would
        // rewrite the "Patient A" prefix inside "Patient AA" and corrupt the report.
        var memberIds = new List<Guid> { _memberId };
        for (var i = 1; i < 27; i++)
        {
            var id = Guid.NewGuid();
            SetUpMember(id, $"Member {i:00}");
            memberIds.Add(id);
        }

        _generativeAi.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Patient AA is stable. Patient A is not.");
        var sut = CreateSut();

        var queued = await sut.GenerateAsync(_userId, new GenerateReportRequest
        {
            CardiMemberIds = memberIds,
            DateRangeFrom = new DateOnly(2026, 2, 7),
            DateRangeTo = new DateOnly(2026, 3, 9),
            Format = ReportFormat.Pdf
        });
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        Assert.Equal("Member 26 is stable. Margaret Doe is not.", _renderer.LastNarrative);
    }

    [Theory]
    [InlineData(ReportFormat.Csv)]
    [InlineData(ReportFormat.FhirR4)]
    public async Task MachineReadableFormats_AreRenderedWithoutCallingTheModel(ReportFormat format)
    {
        // A spreadsheet column or a FHIR resource holding a paragraph of generated English is
        // neither useful nor safe to feed onward into an EHR — and a caregiver asking for CSV
        // should not wait on an inference, or fail when the provider is down.
        var renderer = new RecordingRenderer(format);
        var sut = new ReportGenerationService(
            _unitOfWork, _storage, _access, _options,
            BuildScopeFactoryFor(renderer), Substitute.For<ILogger<ReportGenerationService>>());

        var queued = await sut.GenerateAsync(_userId, BuildRequest(format));
        var status = await WaitForTerminalStatusAsync(sut, queued.ReportId);

        Assert.Equal(ReportStatus.Ready, status.Status);
        Assert.Null(renderer.LastNarrative);
        await _generativeAi.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Access control ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_Throws_WhenUserMayNotViewARequestedMember()
    {
        DenyAccessTo(_memberId);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().GenerateAsync(_userId, BuildRequest()));
    }

    [Fact]
    public async Task GenerateAsync_QueuesNothing_WhenAccessIsRefused()
    {
        DenyAccessTo(_memberId);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().GenerateAsync(_userId, BuildRequest()));

        // Nothing was handed to the model, no row was written, and no job was left running.
        await _generativeAi.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Empty(_reports.All);
    }

    [Fact]
    public async Task GenerateAsync_ChecksEveryRequestedMember_NotJustTheFirst()
    {
        var secondMemberId = Guid.NewGuid();
        SetUpMember(secondMemberId, "John Doe");
        DenyAccessTo(secondMemberId);

        var request = new GenerateReportRequest
        {
            CardiMemberIds = [_memberId, secondMemberId],
            DateRangeFrom = new DateOnly(2026, 2, 7),
            DateRangeTo = new DateOnly(2026, 3, 9),
            Format = ReportFormat.Pdf
        };

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().GenerateAsync(_userId, request));
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNull_ForAnotherUsersReport()
    {
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        // Holding the report id is not enough — it reads as "no such report" for anyone else.
        Assert.Null(await sut.GetStatusAsync(_otherUserId, queued.ReportId));
    }

    [Fact]
    public async Task DownloadAsync_Throws_ForAnotherUsersReport()
    {
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sut.DownloadAsync(_otherUserId, queued.ReportId));
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNull_ForUnauthenticatedCaller()
    {
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        Assert.Null(await sut.GetStatusAsync(Guid.Empty, queued.ReportId));
    }

    // ── Malformed report ids ────────────────────────────────────────────────────

    [Theory]
    [InlineData("rpt_unknown")]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task GetStatusAsync_ReturnsNull_ForAnIdThatIsNotAReportId(string reportId)
    {
        // A garbage id is a miss, not a parse exception surfacing as a 500.
        Assert.Null(await CreateSut().GetStatusAsync(_userId, reportId));
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNull_WhenReportUnknown()
    {
        Assert.Null(await CreateSut().GetStatusAsync(_userId, Guid.NewGuid().ToString("N")));
    }

    // ── DownloadAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_ReturnsRenderedBytes_WhenReady()
    {
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        var (content, contentType, _) = await sut.DownloadAsync(_userId, queued.ReportId);

        Assert.Equal(_renderer.LastRendered!.Content, content);
        Assert.Equal(RecordingRenderer.ContentTypeValue, contentType);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("rpt_unknown")]
    public async Task DownloadAsync_DoesNotEchoTheRequestedId_WhenItIsNotFound(string reportId)
    {
        // ReportsController hands ex.Message straight to the client, so an interpolated id would
        // put caller-supplied text back in the response — and would put developer copy in front
        // of a caregiver. Fixed copy, matching what the status endpoint says.
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().DownloadAsync(_userId, reportId));

        Assert.DoesNotContain(reportId, ex.Message);
        Assert.Equal("We couldn't find that report — it may have expired. Try generating a new one.", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_DoesNotNameTheInternalStatus_WhileTheReportIsPending()
    {
        // "(status: Pending)" is an enum name, and it reached the caregiver verbatim.
        var gate = HoldGeneration();
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.DownloadAsync(_userId, queued.ReportId));

        Assert.DoesNotContain(queued.ReportId, ex.Message);
        Assert.DoesNotContain(nameof(ReportStatus.Pending), ex.Message);

        gate.SetResult("done");
    }

    [Fact]
    public async Task DownloadAsync_TellsAnExpiredAndAReapedReportApartToNobody()
    {
        // Unknown, expired, another user's and content-already-gone are deliberately
        // indistinguishable; saying which one it was in the copy would undo that.
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);
        _storage.Clear();

        var reaped = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sut.DownloadAsync(_userId, queued.ReportId));
        var unknown = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sut.DownloadAsync(_userId, Guid.NewGuid().ToString("N")));

        Assert.Equal(unknown.Message, reaped.Message);
    }

    [Fact]
    public async Task DownloadAsync_Throws_WhenReportUnknown()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().DownloadAsync(_userId, Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task DownloadAsync_Throws_WhileReportStillPending()
    {
        var gate = HoldGeneration();
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.DownloadAsync(_userId, queued.ReportId));

        gate.SetResult("done");
    }

    [Fact]
    public async Task DownloadAsync_Throws_WhenReportFailed()
    {
        _generativeAi.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("boom"));
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.DownloadAsync(_userId, queued.ReportId));
    }

    // ── Data gathering ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Gather_PassesOnlyAlertsInsideTheDateRange()
    {
        _alerts.GetByCardiMemberAsync(_memberId, false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId,
                Title = "In-range alert",
                Severity = AlertSeverity.Red,
                TriggeredDate = new DateTime(2026, 2, 15, 8, 0, 0, DateTimeKind.Utc),
            },
            new Alert
            {
                CardiMemberId = _memberId,
                Title = "Out-of-range alert",
                Severity = AlertSeverity.Yellow,
                TriggeredDate = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc),
            },
        ]);
        var sut = CreateSut();

        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        var alerts = _renderer.LastData!.Members.Single().Alerts;
        Assert.Equal("In-range alert", Assert.Single(alerts).Title);
    }

    [Fact]
    public async Task Gather_SkipsUnknownMembers()
    {
        var unknownId = Guid.NewGuid();
        _members.GetByIdAsync(unknownId).Returns((CardiMember?)null);
        var sut = CreateSut();

        var queued = await sut.GenerateAsync(_userId, new GenerateReportRequest
        {
            CardiMemberIds = [unknownId],
            DateRangeFrom = new DateOnly(2026, 2, 7),
            DateRangeTo = new DateOnly(2026, 3, 9),
            Format = ReportFormat.Pdf
        });
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        Assert.Empty(_renderer.LastData!.Members);
    }

    [Fact]
    public async Task Gather_PassesTheSectionTogglesThrough()
    {
        var sut = CreateSut();

        var queued = await sut.GenerateAsync(
            _userId, BuildRequest(includeMetrics: false, includeAlerts: false));
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        Assert.False(_renderer.LastSections!.IncludeMetrics);
        Assert.False(_renderer.LastSections.IncludeAlerts);
    }

    [Fact]
    public async Task Gather_PassesNoReadings_WhenTheMetricsSectionIsOff()
    {
        // The narrative prompt is built from whatever the gather returns, so loading the logs
        // regardless meant a caregiver who unticked metrics still had their readings described in
        // the PDF — and still had them sent to the general provider.
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([new ActivityLog { CardiMemberId = _memberId, Date = new DateOnly(2026, 2, 10), Steps = 4321 }]);
        var sut = CreateSut();

        var queued = await sut.GenerateAsync(_userId, BuildRequest(includeMetrics: false));
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        Assert.Empty(_renderer.LastData!.Members.Single().ActivityLogs);
    }

    [Fact]
    public async Task Prompt_DescribesNoMetrics_WhenTheMetricsSectionIsOff()
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([new ActivityLog { CardiMemberId = _memberId, Date = new DateOnly(2026, 2, 10), Steps = 4321 }]);

        var prompt = await CapturePromptAsync(BuildRequest(includeMetrics: false));

        // Not just absent from the rendered file — absent from what leaves for the provider.
        Assert.DoesNotContain("### Activity Metrics", prompt);
        Assert.DoesNotContain("4321", prompt);
    }

    [Fact]
    public async Task Gather_DoesNotReadDevices_WhenTheSectionIsOff()
    {
        var sut = CreateSut();

        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        await _devices.DidNotReceive().GetByCardiMemberIdAsync(Arg.Any<Guid>());
    }

    // ── Prompt building ─────────────────────────────────────────────────────────

    private async Task<string> CapturePromptAsync(GenerateReportRequest request)
    {
        string? prompt = null;
        _generativeAi.GenerateAsync(Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>())
            .Returns("Generated report body.");
        var sut = CreateSut();

        var queued = await sut.GenerateAsync(_userId, request);
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        Assert.NotNull(prompt);
        return prompt!;
    }

    [Fact]
    public async Task Prompt_IncludesPseudonymisedMemberAndPeriod()
    {
        var prompt = await CapturePromptAsync(BuildRequest());

        // Was "## Patient: Margaret Doe" — the name is now withheld from the provider.
        Assert.Contains("## Patient A", prompt);
        Assert.Contains($"covering {new DateOnly(2026, 2, 7)} to {new DateOnly(2026, 3, 9)}.", prompt);
    }

    [Fact]
    public async Task Prompt_IncludesMetricLines_ForLogsInRange()
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(
            [
                new ActivityLog
                {
                    CardiMemberId = _memberId,
                    Date = new DateOnly(2026, 2, 10),
                    Steps = 4321,
                    RestingHeartRate = 68,
                    SleepMinutes = 410,
                },
            ]);

        var prompt = await CapturePromptAsync(BuildRequest());

        Assert.Contains("### Activity Metrics", prompt);
        Assert.Contains("steps=4321, HR=68, sleep(night ending that morning)=410min", prompt);
    }

    [Fact]
    public async Task Prompt_NamesNothingForADayTheDeviceMissed()
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([new ActivityLog { CardiMemberId = _memberId, Date = new DateOnly(2026, 2, 10) }]);

        var prompt = await CapturePromptAsync(BuildRequest());

        // Never "steps=, HR=, sleep=min" — an empty value reads as a real one.
        Assert.Contains("nothing measured", prompt);
        Assert.DoesNotContain("steps=,", prompt);
    }

    private void SetUpMember(Guid id, string name)
    {
        _members.GetByIdAsync(id).Returns(new CardiMember { Id = id, Name = name });
        _activityLogs.GetByCardiMemberAndDateRangeAsync(id, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);
        _alerts.GetByCardiMemberAsync(id, false).Returns([]);
    }

    private IServiceScopeFactory BuildScopeFactoryFor(IReportRenderer renderer)
    {
        var provider = new StubServiceProvider(new Dictionary<Type, object>
        {
            [typeof(IUnitOfWork)] = _unitOfWork,
            [typeof(IGenerativeAiService)] = _generativeAi,
            [typeof(IEnumerable<IReportRenderer>)] = new[] { renderer }
        });

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    /// <summary>Captures what the service handed the renderer, and returns deterministic bytes.</summary>
    private sealed class RecordingRenderer : IReportRenderer
    {
        public const string ContentTypeValue = "application/test";
        public const string ExtensionValue = "test";

        public RecordingRenderer(ReportFormat format) => Format = format;

        public ReportFormat Format { get; }

        public ReportDataSet? LastData { get; private set; }
        public ReportSections? LastSections { get; private set; }
        public string? LastNarrative { get; private set; }
        public RenderedReport? LastRendered { get; private set; }

        public Task<RenderedReport> RenderAsync(
            ReportDataSet data, ReportSections sections, string? narrative, CancellationToken ct = default)
        {
            LastData = data;
            LastSections = sections;
            LastNarrative = narrative;
            LastRendered = new RenderedReport(
                Encoding.UTF8.GetBytes(narrative ?? "rendered"), ContentTypeValue, ExtensionValue);

            return Task.FromResult(LastRendered);
        }
    }

    /// <summary>Dictionary-backed bucket — enough for upload/download/delete round-trips.</summary>
    private sealed class InMemoryReportStorage : IReportStorage
    {
        private readonly ConcurrentDictionary<string, byte[]> _objects = new();

        public IReadOnlyCollection<string> ObjectNames => _objects.Keys.ToList();

        public void Clear() => _objects.Clear();

        public Task<string> UploadAsync(
            Guid ownerUserId, string reportId, string extension, string contentType,
            ReadOnlyMemory<byte> content, CancellationToken ct = default)
        {
            var objectName = $"reports/{ownerUserId}/{reportId}.{extension}";
            _objects[objectName] = content.ToArray();
            return Task.FromResult(objectName);
        }

        public Task<byte[]?> DownloadAsync(string objectName, CancellationToken ct = default) =>
            Task.FromResult(_objects.TryGetValue(objectName, out var bytes) ? bytes : null);

        public Task DeleteAsync(string objectName, CancellationToken ct = default)
        {
            _objects.TryRemove(objectName, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Dictionary-backed report repository. Entities are stored by reference, as EF's change
    /// tracker would hold them, so an <c>Update</c> the service makes is visible to a later read.
    /// </summary>
    private sealed class InMemoryReportRepository : IReportRepository
    {
        private readonly ConcurrentDictionary<Guid, Report> _rows = new();

        public IReadOnlyCollection<Report> All => _rows.Values.ToList();
        public Report Single() => _rows.Values.Single();

        public Task<Report?> GetForOwnerAsync(Guid reportId, Guid ownerUserId, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(reportId, out var report) && report.OwnerUserId == ownerUserId
                ? report
                : null);

        public Task<IReadOnlyList<Report>> GetExpiredAsync(DateTime asOf, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Report>>(
                _rows.Values.Where(r => r.ExpiresAt <= asOf).OrderBy(r => r.ExpiresAt).Take(limit).ToList());

        public Task<IReadOnlyList<Report>> GetStalePendingAsync(DateTime olderThan, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Report>>(
                _rows.Values
                    .Where(r => r.Status == ReportStatus.Pending && r.CreatedDate <= olderThan)
                    .OrderBy(r => r.CreatedDate).Take(limit).ToList());

        public Task<Report?> GetByIdAsync(Guid id) =>
            Task.FromResult(_rows.TryGetValue(id, out var report) ? report : null);

        public Task AddAsync(Report entity)
        {
            _rows[entity.Id] = entity;
            return Task.CompletedTask;
        }

        public void Update(Report entity) => _rows[entity.Id] = entity;
        public void Remove(Report entity) => _rows.TryRemove(entity.Id, out _);

        public void RemoveRange(IEnumerable<Report> entities)
        {
            foreach (var entity in entities)
                Remove(entity);
        }

        public Task<IEnumerable<Report>> GetAllAsync() => Task.FromResult<IEnumerable<Report>>(_rows.Values);

        public Task<IEnumerable<Report>> FindAsync(
            System.Linq.Expressions.Expression<Func<Report, bool>> predicate) =>
            Task.FromResult(_rows.Values.Where(predicate.Compile()));

        public Task<Report?> FirstOrDefaultAsync(
            System.Linq.Expressions.Expression<Func<Report, bool>> predicate) =>
            Task.FromResult(_rows.Values.FirstOrDefault(predicate.Compile()));

        public Task AddRangeAsync(IEnumerable<Report> entities)
        {
            foreach (var entity in entities)
                _rows[entity.Id] = entity;
            return Task.CompletedTask;
        }
    }

    /// <summary>Resolves exactly the services background generation asks its scope for.</summary>
    private sealed class StubServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services;

        public StubServiceProvider(Dictionary<Type, object> services) => _services = services;

        public object? GetService(Type serviceType) =>
            _services.TryGetValue(serviceType, out var service) ? service : null;
    }
}
