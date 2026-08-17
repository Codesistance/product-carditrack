using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.Mobile.Core.Questionnaires;

/// <inheritdoc cref="IQuestionValidityService"/>
public sealed class QuestionValidityService : IQuestionValidityService
{
    private readonly ICardiTrackApiClient _api;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Questions already reported this session, so a page that reloads every time it appears does
    /// not send the same retirement over and over. Bounded by how many questions one member can
    /// have lapsed while the app has been open, which is a handful.
    /// </summary>
    private readonly HashSet<Guid> _reported = [];

    public QuestionValidityService(ICardiTrackApiClient api, TimeProvider? clock = null)
    {
        _api = api;
        _clock = clock ?? TimeProvider.System;
    }

    public QuestionnaireResponse? Verify(QuestionnaireResponse? pending)
    {
        if (pending is null)
            return null;

        if (!QuestionValidity.HasLapsed(pending, _clock.GetUtcNow().UtcDateTime))
            return pending;

        // Once per question per session. lock, rather than a concurrent set, because both callers
        // are page loads on the UI thread and the contention this guards against is a second page
        // load overlapping the first, not throughput.
        bool firstSighting;
        lock (_reported)
        {
            firstSighting = _reported.Add(pending.Id);
        }

        if (firstSighting)
            _ = RetireAsync(pending.Id);

        return null;
    }

    /// <summary>
    /// Tells the server the question has lapsed, and swallows every way that can fail.
    /// </summary>
    /// <remarks>
    /// The caller is drawing a screen and has already stopped drawing the card, which is the part a
    /// caregiver can see. A phone with no signal, or one whose session has expired, has nothing to
    /// tell the user here — there is no action for them to take and nothing has gone wrong from
    /// where they are sitting. The server's own sweep retires the row regardless, so a failed call
    /// costs a retry next time this member's page is opened and nothing else.
    /// </remarks>
    private async Task RetireAsync(Guid questionnaireId)
    {
        try
        {
            await _api.ExpireQuestionnaireAsync(questionnaireId);
        }
        catch (Exception)
        {
            // Retried on the next load — the id stays in _reported only for this attempt.
            // ApiException covers the transport and HTTP failures we expect; anything else that
            // escapes a fire-and-forget task would otherwise be unobserved, which is worse than
            // treating it as "try again when this page next draws".
            lock (_reported)
            {
                _reported.Remove(questionnaireId);
            }
        }
    }
}
