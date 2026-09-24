using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// The member-chat send's end-to-end budget (<see cref="MemberChatOptions.SendBudgetSeconds"/>).
/// A send chains several model calls, each with its own ceiling; the budget caps the chain, so a
/// slow one ends as the API's own 503 before Cloud Run's request timeout turns it into a bare 504.
/// </summary>
public class MemberChatSendBudgetTests
{
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly IMemberChatService _chat = Substitute.For<IMemberChatService>();
    private readonly Guid _memberId = Guid.NewGuid();

    public MemberChatSendBudgetTests()
    {
        _userContext.IsAuthenticated.Returns(true);
        _userContext.UserId.Returns(Guid.NewGuid());

        // A send that only ever ends by cancellation — the shape of a chain still waiting on a
        // model host when its budget runs out.
        _chat.SendMessageAsync(Arg.Any<Guid>(), _memberId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>())
                .ContinueWith<MemberChatMessageResponse>(
                    _ => throw new TaskCanceledException(), TaskScheduler.Default));
    }

    private MemberChatController CreateSut(int budgetSeconds) =>
        new(_userContext, Substitute.For<ILogger<MemberChatController>>(), _chat,
            new MemberChatMessageValidator(),
            Options.Create(new MemberChatOptions { SendBudgetSeconds = budgetSeconds }))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    [Fact]
    public async Task ASendThatOutrunsItsBudget_IsAnswered503_NotLeftForCloudRun()
    {
        var result = await CreateSut(budgetSeconds: 1)
            .SendMessage(_memberId, new MemberChatMessageRequest { Message = "how did he sleep?" }, CancellationToken.None);

        var status = Assert.IsAssignableFrom<IStatusCodeActionResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }

    [Fact]
    public async Task TheCallerHangingUp_IsNotReportedAsTheBudget()
    {
        // The caller's own cancellation is theirs to see, not a server-side timeout to excuse.
        using var hangUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateSut(budgetSeconds: 60)
                .SendMessage(_memberId, new MemberChatMessageRequest { Message = "how did he sleep?" }, hangUp.Token));
    }
}
