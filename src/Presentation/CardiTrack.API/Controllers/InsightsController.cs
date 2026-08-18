using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

[Authorize]
[AuditHealthDataAccess("ViewInsight")]
[Route("api/v1/insights")]
public class InsightsController : BaseApiController
{
    /// <summary>
    /// How many summaries a history read returns when the caller doesn't say. Sized against how
    /// densely they are now written: the digest job runs half-hourly (and again after each
    /// assessor pass) and its regeneration floor admits at most three <em>ordinary</em> summaries
    /// an hour. Waivers (a problem window, a jump, a baseline divergence, an alert) can write
    /// more; even then this default still spans hours rather than minutes — which is what the
    /// history is for. Well inside
    /// <c>DigestQueryService.MaxHistoryLimit</c>, which remains the ceiling a caller can ask for.
    /// </summary>
    private const int DefaultHistoryLimit = 24;

    private readonly IHealthInsightService _insightService;
    private readonly IDigestQueryService _digests;

    public InsightsController(
        IUserContext userContext,
        ILogger<InsightsController> logger,
        IHealthInsightService insightService,
        IDigestQueryService digests)
        : base(userContext, logger)
    {
        _insightService = insightService;
        _digests = digests;
    }

    /// <summary>Analyse a specific alert using MedGemma.</summary>
    [HttpGet("alerts/{alertId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AlertInsightResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AlertInsightResponse>>> AnalyzeAlert(
        Guid alertId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _insightService.AnalyzeAlertAsync(UserContext.UserId, alertId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Analyse health baseline trends for a CardiMember using MedGemma.</summary>
    [HttpGet("members/{cardiMemberId:guid}/baseline")]
    [ProducesResponseType(typeof(ApiResponse<BaselineInsightResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<BaselineInsightResponse>>> AnalyzeBaseline(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _insightService.AnalyzeBaselineAsync(UserContext.UserId, cardiMemberId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// A short, empathetic line describing a CardiMember's current status — what the Dashboard's
    /// hero card shows once it resolves, in place of the fixed per-severity-tier copy the client
    /// renders while this is in flight. Cached per member; see
    /// <see cref="IHealthInsightService.GetCurrentStatusMessageAsync"/> for the TTL. A null
    /// <c>message</c> means there's nothing to say yet — the client keeps its existing copy.
    /// </summary>
    [HttpGet("members/{cardiMemberId:guid}/status")]
    [ProducesResponseType(typeof(ApiResponse<CurrentStatusMessageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CurrentStatusMessageResponse>>> GetCurrentStatus(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _insightService.GetCurrentStatusMessageAsync(UserContext.UserId, cardiMemberId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// The member's current family summary, or the latest one describing a given local date.
    /// Recomputed by the pipeline as the member's data moves; read-only here, no model call on
    /// this path.
    /// </summary>
    [HttpGet("members/{cardiMemberId:guid}/digest")]
    [ProducesResponseType(typeof(ApiResponse<DigestResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DigestResponse>>> GetDigest(
        Guid cardiMemberId,
        [FromQuery] DateOnly? date,
        [FromQuery] string? audience,
        CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        if (DigestQueryService.ParseAudience(audience) is not { } parsedAudience)
            return Error("That summary type isn't one we recognise — use family or day-review.");

        try
        {
            var result = await _digests.GetDigestAsync(
                UserContext.UserId, cardiMemberId, date, parsedAudience, ct);
            return result is null
                ? Error("No summary has been generated for this member yet.", StatusCodes.Status404NotFound)
                : Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// The member's family summaries newest first — the current one and the recomputations behind
    /// it. An empty list, not a 404: "this member has no summaries yet" is an ordinary answer to a
    /// history question, where the single-summary endpoint above is asking for a thing that either
    /// exists or does not.
    /// </summary>
    /// <param name="cardiMemberId">The member whose summaries are being read.</param>
    /// <param name="limit">
    /// Optional, and nullable so the generated contract says so: a bare <c>int</c> is described as
    /// required by the API explorer and by client generators reading it, which would misdescribe an
    /// endpoint that is perfectly happy without one.
    /// </param>
    /// <param name="audience">
    /// Which series to read: <c>family</c> (the default) for the running summary and its
    /// recomputations, or <c>day-review</c> for one entry per finished day. Optional, and a value
    /// outside those two is refused rather than ignored.
    /// </param>
    /// <param name="search">
    /// Optional case-insensitive text filter over the summary, its headline and its suggestion.
    /// Applied before <paramref name="limit"/>, so it searches the history rather than the page.
    /// </param>
    /// <param name="from">Optional earliest local day, inclusive.</param>
    /// <param name="to">Optional latest local day, inclusive; must not precede <paramref name="from"/>.</param>
    /// <param name="urgency">
    /// Optional filter to one urgency tier — <c>watch</c>, <c>check-in</c>, <c>concerning</c> or
    /// <c>act-now</c>. A value outside those is refused rather than ignored.
    /// </param>
    /// <param name="ct">Cancels the read when the caller disconnects.</param>
    [HttpGet("members/{cardiMemberId:guid}/digests")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<DigestResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<DigestResponse>>>> GetDigestHistory(
        Guid cardiMemberId,
        [FromQuery] int? limit,
        [FromQuery] string? audience,
        [FromQuery] string? search,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? urgency,
        CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        // Refused rather than ignored, the same way the alert filters are: a typo'd audience that
        // silently returned the family list would show a caregiver one kind of summary under the
        // heading of another, which on this screen is worse than an error.
        if (DigestQueryService.ParseAudience(audience) is not { } parsedAudience)
            return Error("That summary type isn't one we recognise — use family or day-review.");

        // Same stance for the urgency filter: a typo must not silently return the unfiltered list.
        if (!DigestQueryService.TryParseUrgency(urgency, out var parsedUrgency))
            return Error("That urgency isn't one we recognise — use watch, check-in, concerning or act-now.");

        if (from is not null && to is not null && from > to)
            return Error("The start date needs to come before the end date.");

        try
        {
            // An omitted or nonsense limit takes the default rather than being refused: the
            // service clamps into range, so there is no value of `limit` that can ask for more
            // than a page.
            var result = await _digests.GetHistoryAsync(
                UserContext.UserId,
                cardiMemberId,
                limit is > 0 ? limit.Value : DefaultHistoryLimit,
                parsedAudience,
                string.IsNullOrWhiteSpace(search) ? null : search,
                from,
                to,
                parsedUrgency,
                ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }
}
