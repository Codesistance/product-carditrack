using System.Collections.Concurrent;
using System.Text;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Caching.Distributed;
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
    private readonly InMemoryDistributedCache _cache = new();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public ReportGenerationServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.Alerts.Returns(_alerts);

        // Defaults: known member, no logs, no alerts, AI returns a fixed report.
        _members.GetByIdAsync(_memberId).Returns(new CardiMember { Id = _memberId, Name = "Margaret Doe" });
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);
        _alerts.GetByCardiMemberAsync(_memberId, false).Returns([]);
        _generativeAi.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Generated report body.");
    }

    private ReportGenerationService CreateSut() =>
        new(_generativeAi, _unitOfWork, _cache, Substitute.For<ILogger<ReportGenerationService>>());

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
    public async Task GenerateAsync_ReturnsQueuedResponse_AndCachesPendingStatus()
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

    // ── Background generation ───────────────────────────────────────────────────

    [Fact]
    public async Task BackgroundGeneration_MarksReportReady_WithDownloadDetails()
    {
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());

        var status = await WaitForTerminalStatusAsync(sut, queued.ReportId);

        Assert.Equal(ReportStatus.Ready, status.Status);
        Assert.Equal("text/plain", status.ContentType);
        Assert.Equal(Encoding.UTF8.GetByteCount("Generated report body."), status.FileSizeBytes);
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

    // ── GetStatusAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_ReturnsNull_WhenReportUnknown()
    {
        var result = await CreateSut().GetStatusAsync(_userId, "rpt_unknown");

        Assert.Null(result);
    }

    // ── DownloadAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_ReturnsUtf8Content_WhenReady()
    {
        var sut = CreateSut();
        var queued = await sut.GenerateAsync(_userId, BuildRequest());
        await WaitForTerminalStatusAsync(sut, queued.ReportId);

        var (content, contentType, fileName) = await sut.DownloadAsync(_userId, queued.ReportId);

        Assert.Equal("Generated report body.", Encoding.UTF8.GetString(content));
        Assert.Equal("text/plain; charset=utf-8", contentType);
        Assert.Equal($"report-{queued.ReportId}.txt", fileName);
    }

    [Fact]
    public async Task DownloadAsync_Throws_WhenReportUnknown()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().DownloadAsync(_userId, "rpt_unknown"));
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
    public async Task Prompt_IncludesMemberName_FormatAndPeriod()
    {
        var prompt = await CapturePromptAsync(BuildRequest(ReportFormat.Csv));

        Assert.Contains("## Patient: Margaret Doe", prompt);
        Assert.Contains("Report format: Csv", prompt);
        Assert.Contains($"Period: {new DateOnly(2026, 2, 7)} to {new DateOnly(2026, 3, 9)}", prompt);
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
        Assert.Contains("steps=4321, HR=68, sleep=410min", prompt);
    }

    [Fact]
    public async Task Prompt_IncludesOnlyAlertsInsideDateRange()
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

        var prompt = await CapturePromptAsync(BuildRequest());

        Assert.Contains("### Alerts", prompt);
        Assert.Contains("In-range alert", prompt);
        Assert.DoesNotContain("Out-of-range alert", prompt);
    }

    [Fact]
    public async Task Prompt_OmitsSections_WhenTogglesDisabled()
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([new ActivityLog { CardiMemberId = _memberId, Date = new DateOnly(2026, 2, 10), Steps = 100 }]);
        _alerts.GetByCardiMemberAsync(_memberId, false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId,
                Title = "Some alert",
                TriggeredDate = new DateTime(2026, 2, 15, 8, 0, 0, DateTimeKind.Utc),
            },
        ]);

        var prompt = await CapturePromptAsync(BuildRequest(includeMetrics: false, includeAlerts: false));

        Assert.DoesNotContain("### Activity Metrics", prompt);
        Assert.DoesNotContain("### Alerts", prompt);
    }

    [Fact]
    public async Task Prompt_SkipsUnknownMembers()
    {
        var unknownId = Guid.NewGuid();
        _members.GetByIdAsync(unknownId).Returns((CardiMember?)null);
        var request = new GenerateReportRequest
        {
            CardiMemberIds = [unknownId],
            DateRangeFrom = new DateOnly(2026, 2, 7),
            DateRangeTo = new DateOnly(2026, 3, 9),
            Format = ReportFormat.Pdf
        };

        var prompt = await CapturePromptAsync(request);

        Assert.DoesNotContain("## Patient:", prompt);
    }

    /// <summary>Dictionary-backed IDistributedCache — enough for status/content round-trips.</summary>
    private sealed class InMemoryDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new();

        public byte[]? Get(string key) => _store.TryGetValue(key, out var value) ? value : null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _store[key] = value;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => _store.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }
}
