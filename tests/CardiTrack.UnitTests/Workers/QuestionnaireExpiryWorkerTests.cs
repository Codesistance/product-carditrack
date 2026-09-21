using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.UnitTests.Observability;
using CardiTrack.Worker;
using CardiTrack.Worker.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CardiTrack.UnitTests.Workers;

/// <summary>
/// The sweep records how many rows actually moved to Expired, not how many it read as lapsed.
/// </summary>
[Collection(QuestionnaireTelemetryCollection.Name)]
public class QuestionnaireExpiryWorkerTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemberQuestionnaireRepository _questionnaires =
        Substitute.For<IMemberQuestionnaireRepository>();
    private readonly IServiceProvider _provider = Substitute.For<IServiceProvider>();

    public QuestionnaireExpiryWorkerTests()
    {
        _unitOfWork.MemberQuestionnaires.Returns(_questionnaires);
        _provider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
    }

    [Fact]
    public async Task RecordsOnlyRowsThatMovedToExpired()
    {
        _questionnaires.ExpireLapsedPendingAsync(
                Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(3);

        using var capture = new QuestionnaireMetricCapture();

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        Assert.Contains(capture.Longs, m => m.Instrument == "questionnaire.expired" && m.Value == 3);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task RecordsNothing_WhenNoRowMoved()
    {
        _questionnaires.ExpireLapsedPendingAsync(
                Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        using var capture = new QuestionnaireMetricCapture();
        var before = capture.Longs.Count(m => m.Instrument == "questionnaire.expired");

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        Assert.Equal(before, capture.Longs.Count(m => m.Instrument == "questionnaire.expired"));
    }

    private TestableExpiryWorker CreateWorker()
    {
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(_provider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var options = Substitute.For<IOptionsMonitor<WorkerOptions>>();
        options.Get(nameof(QuestionnaireExpiryWorker))
            .Returns(new WorkerOptions { CronExpression = "0 */15 * * * *" });

        return new TestableExpiryWorker(
            options, scopeFactory, NullLogger<QuestionnaireExpiryWorker>.Instance);
    }

    private sealed class TestableExpiryWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        Microsoft.Extensions.Logging.ILogger<QuestionnaireExpiryWorker> logger)
        : QuestionnaireExpiryWorker(options, scopeFactory, logger)
    {
        public Task RunOnceAsync(CancellationToken ct) => ExecuteJobAsync(ct);
    }
}
