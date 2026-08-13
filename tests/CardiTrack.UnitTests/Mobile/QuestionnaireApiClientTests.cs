using System.Net;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The four calls behind the questions surface, in the same shape as the rest of the client's
/// tests: a queued envelope, then assertions on what actually went out.
/// </summary>
public class QuestionnaireApiClientTests
{
    private static (CardiTrackApiClient Client, FakeHttpMessageHandler Http) CreateSut()
    {
        var http = new FakeHttpMessageHandler();
        var client = new CardiTrackApiClient(
            new HttpClient(http) { BaseAddress = new Uri("https://api.test") });
        return (client, http);
    }

    [Fact]
    public async Task GetQuestionnaires_ReadsTheMembersList_AndUnwrapsTheEnvelope()
    {
        var (client, http) = CreateSut();
        var memberId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, $$"""
            {"success":true,"message":"ok","data":[
              {"id":"{{Guid.NewGuid()}}","cardiMemberId":"{{memberId}}",
               "questionText":"Has anything changed at home recently?","answerText":null,
               "triggerContext":"Sleep has been shorter all week.","status":"pending",
               "generatedAtUtc":"2026-08-13T09:00:00Z","answeredAtUtc":null,"answeredByUserId":null}],
             "timestamp":"2026-08-13T09:00:00Z"}
            """);

        var questionnaires = await client.GetQuestionnairesAsync(memberId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/v1/cardimembers/{memberId}/questionnaires", request.Uri!.AbsolutePath);

        var only = Assert.Single(questionnaires);
        Assert.Equal("Has anything changed at home recently?", only.QuestionText);
        Assert.Equal("pending", only.Status);
        Assert.Null(only.AnswerText);
        Assert.Equal("Sleep has been shorter all week.", only.TriggerContext);
    }

    /// <summary>
    /// Rooted on the questionnaire, not the member: the same call answers a question for the first
    /// time and replaces an answer already given.
    /// </summary>
    [Fact]
    public async Task AnswerQuestionnaire_PutsTheAnswer()
    {
        var (client, http) = CreateSut();
        var questionnaireId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, $$"""
            {"success":true,"message":"ok","data":{"id":"{{questionnaireId}}",
             "cardiMemberId":"{{Guid.NewGuid()}}","questionText":"Has anything changed at home recently?",
             "answerText":"She moved bedrooms.","triggerContext":null,"status":"answered",
             "generatedAtUtc":"2026-08-13T09:00:00Z","answeredAtUtc":"2026-08-13T10:00:00Z",
             "answeredByUserId":null},"timestamp":"2026-08-13T10:00:00Z"}
            """);

        var result = await client.AnswerQuestionnaireAsync(
            questionnaireId, new AnswerQuestionnaireRequest { AnswerText = "She moved bedrooms." });

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/api/v1/questionnaires/{questionnaireId}/answer", request.Uri!.AbsolutePath);
        Assert.Contains("She moved bedrooms.", request.Body);
        Assert.Equal("answered", result.Status);
        Assert.Equal("She moved bedrooms.", result.AnswerText);
    }

    [Fact]
    public async Task DismissQuestionnaire_PutsToTheDismissRoute_WithNoBody()
    {
        var (client, http) = CreateSut();
        var questionnaireId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.OK, $$"""
            {"success":true,"message":"ok","data":{"id":"{{questionnaireId}}",
             "cardiMemberId":"{{Guid.NewGuid()}}","questionText":"Has anything changed at home recently?",
             "answerText":null,"triggerContext":null,"status":"dismissed",
             "generatedAtUtc":"2026-08-13T09:00:00Z","answeredAtUtc":null,"answeredByUserId":null},
             "timestamp":"2026-08-13T10:00:00Z"}
            """);

        var result = await client.DismissQuestionnaireAsync(questionnaireId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/api/v1/questionnaires/{questionnaireId}/dismiss", request.Uri!.AbsolutePath);
        Assert.True(string.IsNullOrEmpty(request.Body));
        Assert.Equal("dismissed", result.Status);
    }

    /// <summary>Delete answers 204 with no envelope, so this exercises the no-content path.</summary>
    [Fact]
    public async Task DeleteQuestionnaire_SendsDelete()
    {
        var (client, http) = CreateSut();
        var questionnaireId = Guid.NewGuid();
        http.Enqueue(HttpStatusCode.NoContent, string.Empty);

        await client.DeleteQuestionnaireAsync(questionnaireId);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/v1/questionnaires/{questionnaireId}", request.Uri!.AbsolutePath);
    }
}
