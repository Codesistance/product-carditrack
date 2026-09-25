using System.Text;
using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// The streaming send (<see cref="MemberChatController.StreamMessage"/>): steps as the pipeline
/// reports them, then the saved answer — and every failure that has its own status still
/// answered with it, because nothing is written before the first step.
/// </summary>
public class MemberChatStreamTests
{
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly IMemberChatService _chat = Substitute.For<IMemberChatService>();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly MemoryStream _body = new();

    private static readonly MemberChatMessageResponse Answer = new()
    {
        SessionId = Guid.NewGuid(),
        Reply = "The week looks steady.",
        Charts = Array.Empty<ChartSeries>(),
        GeneratedAt = DateTimeOffset.UnixEpoch,
    };

    public MemberChatStreamTests()
    {
        _userContext.IsAuthenticated.Returns(true);
        _userContext.UserId.Returns(Guid.NewGuid());
    }

    private MemberChatController CreateSut(int budgetSeconds = 60)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = _body;
        return new(_userContext, Substitute.For<ILogger<MemberChatController>>(), _chat,
            new MemberChatMessageValidator(),
            Options.Create(new MemberChatOptions { SendBudgetSeconds = budgetSeconds }))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private void SendDoes(Func<IProgress<MemberChatStep>, CancellationToken, Task<MemberChatMessageResponse>> send) =>
        _chat.SendMessageAsync(Arg.Any<Guid>(), _memberId, Arg.Any<string>(),
                Arg.Any<IProgress<MemberChatStep>?>(), Arg.Any<CancellationToken>())
            .Returns(call => send(call.Arg<IProgress<MemberChatStep>?>()!, call.Arg<CancellationToken>()));

    private Task<IActionResult> Stream(MemberChatController sut, CancellationToken ct = default) =>
        sut.StreamMessage(_memberId, new MemberChatMessageRequest { Message = "how did he sleep?" }, ct);

    private string Written => Encoding.UTF8.GetString(_body.ToArray());

    [Fact]
    public async Task AnAnsweredSend_StreamsItsSteps_ThenTheAnswer_ThenDone()
    {
        SendDoes((progress, _) =>
        {
            progress.Report(MemberChatStep.Understanding);
            progress.Report(MemberChatStep.Reading);
            return Task.FromResult(Answer);
        });
        var sut = CreateSut();

        var result = await Stream(sut);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, sut.Response.StatusCode);
        Assert.Equal("text/event-stream", sut.Response.ContentType);
        var text = Written;
        var understanding = text.IndexOf("event: step\ndata: {\"step\":\"understanding\"", StringComparison.Ordinal);
        var reading = text.IndexOf("\"step\":\"reading\"", StringComparison.Ordinal);
        var answer = text.IndexOf("event: answer\ndata: {", StringComparison.Ordinal);
        var done = text.IndexOf("event: done\n", StringComparison.Ordinal);
        Assert.True(understanding >= 0 && understanding < reading && reading < answer && answer < done, text);
        Assert.Contains("\"reply\":\"The week looks steady.\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASendAnsweredWithoutAModel_StreamsJustTheAnswer()
    {
        // A journal yes, a message with no question: no steps, and the answer opens the stream.
        SendDoes((_, _) => Task.FromResult(Answer));
        var sut = CreateSut();

        await Stream(sut);

        Assert.StartsWith("event: answer\n", Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusal_BeforeAnyStep_IsAnOrdinary400_WithNothingStreamed()
    {
        SendDoes((_, _) => throw new ArgumentException("That question can't be answered here."));
        var sut = CreateSut();

        var result = await Stream(sut);

        var status = Assert.IsAssignableFrom<IStatusCodeActionResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status.StatusCode);
        Assert.Equal(0, _body.Length);
    }

    [Fact]
    public async Task APreStreamFailure_IsWrittenAsJson_NotRefusedAs406()
    {
        // Executed through MVC's own result pipeline, not just inspected: a content-type filter
        // on the action would make these results unformattable and turn them into 406s.
        SendDoes((_, _) => throw new ArgumentException("That question can't be answered here."));
        var services = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();
        var sut = CreateSut();
        sut.HttpContext.RequestServices = services;
        var action = new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor
        {
            FilterDescriptors = typeof(MemberChatController).GetMethod(nameof(MemberChatController.StreamMessage))!
                .GetCustomAttributes(inherit: true).OfType<IFilterMetadata>()
                .Select(f => new FilterDescriptor(f, FilterScope.Action)).ToList(),
        };
        Assert.DoesNotContain(action.FilterDescriptors, f => f.Filter is ProducesAttribute);

        var result = await Stream(sut);
        await result.ExecuteResultAsync(new ActionContext(sut.HttpContext, new RouteData(), action));

        Assert.Equal(StatusCodes.Status400BadRequest, sut.Response.StatusCode);
        Assert.StartsWith("application/json", sut.Response.ContentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnviewableMember_IsAnOrdinary404()
    {
        SendDoes((_, _) => throw new KeyNotFoundException("We couldn't find what you were looking for."));

        var result = await Stream(CreateSut());

        var status = Assert.IsAssignableFrom<IStatusCodeActionResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, status.StatusCode);
    }

    [Fact]
    public async Task AModelHostFailure_AfterTheStreamStarted_EndsItWithA503ErrorEvent()
    {
        SendDoes((progress, _) =>
        {
            progress.Report(MemberChatStep.Understanding);
            throw new HttpRequestException("saturated");
        });
        var sut = CreateSut();

        var result = await Stream(sut);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, sut.Response.StatusCode);
        Assert.Contains("event: error\ndata: {\"status\":503,", Written, StringComparison.Ordinal);
        Assert.DoesNotContain("event: answer", Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnexpectedFault_AfterTheStreamStarted_EndsWithA500ErrorEvent_AndStillPropagates()
    {
        // The exception middleware logs it but cannot write its body once the 200 is out; the
        // stream's last event carries that body instead.
        SendDoes((progress, _) =>
        {
            progress.Report(MemberChatStep.Understanding);
            throw new InvalidOperationException("database fault");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Stream(CreateSut()));

        Assert.Contains("event: error\ndata: {\"status\":500,", Written, StringComparison.Ordinal);
        Assert.DoesNotContain("database fault", Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASendThatOutrunsItsBudget_MidStream_EndsWithA503ErrorEvent()
    {
        SendDoes(async (progress, ct) =>
        {
            progress.Report(MemberChatStep.Reading);
            await Task.Delay(Timeout.Infinite, ct);
            return Answer;
        });

        await Stream(CreateSut(budgetSeconds: 1));

        Assert.Contains("event: error\ndata: {\"status\":503,", Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASendThatOutrunsItsBudget_BeforeAnyStep_IsAnOrdinary503()
    {
        SendDoes(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Answer;
        });

        var result = await Stream(CreateSut(budgetSeconds: 1));

        var status = Assert.IsAssignableFrom<IStatusCodeActionResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }

    [Fact]
    public async Task TheCallerHangingUp_CancelsTheSend_AndIsNotReportedAsAnError()
    {
        var sendCancelled = false;
        SendDoes(async (progress, ct) =>
        {
            progress.Report(MemberChatStep.Reading);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                sendCancelled = true;
                throw;
            }
            return Answer;
        });
        using var hangUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stream(CreateSut(), hangUp.Token));

        Assert.True(sendCancelled);
        Assert.DoesNotContain("event: error", Written, StringComparison.Ordinal);
    }
}
