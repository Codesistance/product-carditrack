using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

/// <summary>
/// Handles user onboarding workflow for CardiTrack application
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class OnboardingController : BaseApiController
{
    private readonly IUserService _userService;
    private readonly ICardiMemberService _cardiMemberService;
    private readonly IOnboardingService _onboardingService;
    private readonly IValidator<CreateOrganizationRequest> _organizationValidator;
    private readonly IValidator<CreateCardiMemberRequest> _cardiMemberValidator;
    private readonly IGuestFamilyProvisioner _guestFamilies;

    /// <summary>
    /// Matches the column. A GUID in any of its spellings fits comfortably; anything longer is a
    /// client using the header as storage rather than as a name.
    /// </summary>
    private const int MaxIdempotencyKeyLength = 64;

    public OnboardingController(
        IUserContext userContext,
        ILogger<OnboardingController> logger,
        IUserService userService,
        ICardiMemberService cardiMemberService,
        IOnboardingService onboardingService,
        IValidator<CreateOrganizationRequest> organizationValidator,
        IValidator<CreateCardiMemberRequest> cardiMemberValidator,
        IGuestFamilyProvisioner guestFamilies)
        : base(userContext, logger)
    {
        _userService = userService;
        _cardiMemberService = cardiMemberService;
        _onboardingService = onboardingService;
        _organizationValidator = organizationValidator;
        _cardiMemberValidator = cardiMemberValidator;
        _guestFamilies = guestFamilies;
    }

    /// <summary>
    /// Steps 2–4 in one call: create organization, trial subscription, and user
    /// atomically. Preferred over the separate organization/user endpoints — a
    /// client failure between those two calls leaves an orphaned organization,
    /// which this endpoint makes impossible.
    /// </summary>
    [HttpPost("setup")]
    [ProducesResponseType(typeof(ApiResponse<OnboardingSetupResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<OnboardingSetupResponse>>> Setup(
        [FromBody] OnboardingSetupRequest request)
    {
        // No organization in the body is the guest path, not a malformed request: they are
        // signing up to join a family somebody else runs. Validate only what was actually sent.
        if (request.Organization is { } organization)
        {
            var validation = await _organizationValidator.ValidateAsync(organization);
            if (!validation.IsValid)
                return ValidationFailed(validation);

            Logger.LogInformation(
                "Onboarding setup for Auth0 user {Auth0UserId}: organization {Name}, Type: {Type}",
                UserContext.Auth0UserId, organization.Name, organization.Type);
        }
        else
        {
            Logger.LogInformation(
                "Onboarding setup for Auth0 user {Auth0UserId}: guest, no family of their own.",
                UserContext.Auth0UserId);
        }

        // Identity comes from the request context, not the client body: email from the
        // token's email claim (body is only a fallback when the claim is absent) and
        // locale from Accept-Language. Auth0UserId and verification state are
        // token-only, passed explicitly below.
        if (!string.IsNullOrEmpty(UserContext.Email))
            request.User.Email = UserContext.Email;
        request.User.Locale = UserContext.Locale;

        var response = await _onboardingService.SetupAsync(
            request, UserContext.Auth0UserId, UserContext.EmailVerified);

        return Created(response, "Welcome aboard — your organization and account are ready!");
    }

    // POST organization and POST user are gone (2026-09-22). They were the two-call version of
    // the single call above, and splitting account creation in half is what made the hole: the
    // second call had to be told which organization to join and at what role, and the only thing
    // it could check was that nobody had joined yet. "Empty" is not ownership, so any
    // authenticated caller holding an organization id could race the real one and be installed as
    // its admin. That was survivable while User.Role authorized nothing; it stopped being
    // survivable when UserOrganization.Role became the fact family authorization rests on.
    //
    // Guarding it would have meant recording a creator and comparing it — a column, a migration,
    // and a check that has to be right for ever. Removing the seam means there is nothing to
    // guard: SetupAsync creates the organization and its first member in one transaction, so the
    // creator is known rather than inferred. Nothing called these — the mobile client's own
    // methods for them were dead code, removed with them.

    /// <summary>
    /// Step 5: Create CardiMember (person to monitor)
    /// </summary>
    /// <remarks>
    /// The one onboarding step that writes health data (date of birth, sex, medical notes), so
    /// it is audited like every other health-data surface. The member does not exist until the
    /// service returns, so the id is handed to the middleware through <c>HttpContext.Items</c>
    /// rather than read from the route.
    /// </remarks>
    [HttpPost("cardimember")]
    [AuditHealthDataAccess("CreateCardiMember")]
    [ProducesResponseType(typeof(ApiResponse<CardiMemberResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<CardiMemberResponse>>> CreateCardiMember(
        [FromBody] CreateCardiMemberRequest request)
    {
        var validation = await _cardiMemberValidator.ValidateAsync(request);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", 403);
        }

        // A guest — somebody who signed up to join a family rather than start one — has no
        // organization until now. Adding a member of their own is the moment they need a family
        // and a plan, so this creates both. Somebody who already has one gets it back unchanged,
        // so a retry cannot mint a second family.
        Guid organizationId;
        try
        {
            organizationId = await _guestFamilies.ResolveHomeOrganizationAsync(UserContext.UserId);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, 403);
        }

        // The caregiver's own name for this attempt, so a retry after a lost response returns the
        // member the first attempt made rather than making a second one (#1074). Optional: an
        // installed app that predates this header still creates members exactly as before. A
        // malformed key is refused rather than ignored — silently dropping it would leave the
        // client believing it had protection it did not have, which is worse than no protection.
        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            idempotencyKey = null;
        }
        else if (idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            return Error($"Idempotency-Key must be {MaxIdempotencyKeyLength} characters or fewer.", 400);
        }

        // No name in the log line: a member's name is an identifier, and nothing about diagnosing
        // a failed create needs it.
        Logger.LogInformation(
            "Creating CardiMember for organization {OrgId}",
            organizationId);

        CardiMemberResponse response;
        try
        {
            response = await _cardiMemberService.CreateCardiMemberAsync(
                organizationId,
                UserContext.UserId,
                request,
                idempotencyKey);
        }
        catch (CardiMemberCreationOutcomeUnknownException ex)
        {
            // The commit's outcome is unknown, so the member may exist. The audit entry for
            // this request must still name it; the exception handler turns the throw into a
            // 500 and the audit middleware, which sits outside it, reads this on the way out.
            HttpContext.Items[AuditHealthDataAccessAttribute.CardiMemberIdItemKey] = ex.CardiMemberId;
            throw;
        }
        catch (FamilyRuleException ex)
        {
            // The plan has no room for another CardiMember. Said in the service's own words —
            // safe here because the caller is inside the family it is adding to (their home
            // family, resolved above). Caught here rather than mapped globally, because the same
            // exception from an invite redemption reaches somebody not yet inside, and there the
            // family's plan and headcount are not theirs to read. 422, as FamiliesController and
            // FamilyJoinController return for it.
            return Error(ex.Message, StatusCodes.Status422UnprocessableEntity);
        }

        HttpContext.Items[AuditHealthDataAccessAttribute.CardiMemberIdItemKey] = response.Id;

        return Created(response, $"{response.FirstName} has been added to your care circle!");
    }

    /// <summary>
    /// Get onboarding status for current user
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(ApiResponse<OnboardingStatusResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<OnboardingStatusResponse>>> GetOnboardingStatus()
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Success(new OnboardingStatusResponse
            {
                HasOrganization = false,
                HasUserAccount = false,
                CurrentStep = 1,
                NextStepMessage = "Let's get you signed in first"
            }, "Here's where you are in your setup");
        }

        var status = await _userService.GetOnboardingStatusAsync(UserContext.UserId, UserContext.EmailVerified);
        return Success(status, "Here's where you are in your setup");
    }

    /// <summary>
    /// Get all CardiMembers for current user's organization
    /// </summary>
    [HttpGet("cardimembers")]
    [ProducesResponseType(typeof(ApiResponse<List<CardiMemberResponse>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<List<CardiMemberResponse>>>> GetCardiMembers()
    {
        if (!UserContext.IsAuthenticated || UserContext.OrganizationId == Guid.Empty)
        {
            return Error("Let's set up your organization first.", 403);
        }

        var cardiMembers = await _cardiMemberService.GetForUserInOrganizationAsync(
            UserContext.UserId, UserContext.OrganizationId);
        var message = cardiMembers.Count switch
        {
            0 => "No CardiMembers yet — add your first one to get started!",
            1 => "Here's your CardiMember",
            _ => $"Here are your {cardiMembers.Count} CardiMembers"
        };
        return Success(cardiMembers, message);
    }
}
