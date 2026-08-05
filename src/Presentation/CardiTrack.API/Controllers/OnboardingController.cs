using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
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
    private readonly IOrganizationService _organizationService;
    private readonly IUserService _userService;
    private readonly ICardiMemberService _cardiMemberService;
    private readonly IOnboardingService _onboardingService;
    private readonly IValidator<CreateOrganizationRequest> _organizationValidator;
    private readonly IValidator<CreateCardiMemberRequest> _cardiMemberValidator;

    public OnboardingController(
        IUserContext userContext,
        ILogger<OnboardingController> logger,
        IOrganizationService organizationService,
        IUserService userService,
        ICardiMemberService cardiMemberService,
        IOnboardingService onboardingService,
        IValidator<CreateOrganizationRequest> organizationValidator,
        IValidator<CreateCardiMemberRequest> cardiMemberValidator)
        : base(userContext, logger)
    {
        _organizationService = organizationService;
        _userService = userService;
        _cardiMemberService = cardiMemberService;
        _onboardingService = onboardingService;
        _organizationValidator = organizationValidator;
        _cardiMemberValidator = cardiMemberValidator;
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
    public async Task<ActionResult<ApiResponse<OnboardingSetupResponse>>> Setup(
        [FromBody] OnboardingSetupRequest request)
    {
        var validation = await _organizationValidator.ValidateAsync(request.Organization);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        Logger.LogInformation(
            "Onboarding setup for Auth0 user {Auth0UserId}: organization {Name}, Type: {Type}",
            UserContext.Auth0UserId, request.Organization.Name, request.Organization.Type);

        // Auth0UserId and verification state come from the token, never from the client body.
        var response = await _onboardingService.SetupAsync(
            request, UserContext.Auth0UserId, UserContext.EmailVerified);

        return Created(response, "Welcome aboard — your organization and account are ready!");
    }

    /// <summary>
    /// Step 2: Create organization (Family or Business)
    /// </summary>
    [HttpPost("organization")]
    [ProducesResponseType(typeof(ApiResponse<OrganizationResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<OrganizationResponse>>> CreateOrganization(
        [FromBody] CreateOrganizationRequest request)
    {
        var validation = await _organizationValidator.ValidateAsync(request);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        Logger.LogInformation("Creating organization: {Name}, Type: {Type}", request.Name, request.Type);

        var response = await _organizationService.CreateOrganizationAsync(request);

        return Created(response, "Your organization is ready!");
    }

    /// <summary>
    /// Step 4: Create user account linked to Auth0
    /// </summary>
    [HttpPost("user")]
    [ProducesResponseType(typeof(ApiResponse<UserResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<UserResponse>>> CreateUser(
        [FromBody] CreateUserRequest request)
    {
        // Get Auth0UserId and verification state from the authenticated user context —
        // both come from the token, never from the client body.
        request.Auth0UserId = UserContext.Auth0UserId;
        request.EmailVerified = UserContext.EmailVerified;

        Logger.LogInformation("Creating user account for Auth0 user: {Auth0UserId}", request.Auth0UserId);

        var response = await _userService.CreateUserAsync(request);

        return Created(response, "Welcome aboard — your account is ready!");
    }

    /// <summary>
    /// Step 5: Create CardiMember (person to monitor)
    /// </summary>
    [HttpPost("cardimember")]
    [ProducesResponseType(typeof(ApiResponse<CardiMemberResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<CardiMemberResponse>>> CreateCardiMember(
        [FromBody] CreateCardiMemberRequest request)
    {
        var validation = await _cardiMemberValidator.ValidateAsync(request);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        if (!UserContext.IsAuthenticated || UserContext.OrganizationId == Guid.Empty)
        {
            return Error("Let's set up your organization first — then you can add a CardiMember.", 403);
        }

        Logger.LogInformation(
            "Creating CardiMember {Name} for organization {OrgId}",
            request.Name,
            UserContext.OrganizationId);

        var response = await _cardiMemberService.CreateCardiMemberAsync(
            UserContext.OrganizationId,
            UserContext.UserId,
            request);

        return Created(response, $"{response.Name} has been added to your care circle!");
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

        var cardiMembers = await _cardiMemberService.GetByOrganizationIdAsync(UserContext.OrganizationId);
        var message = cardiMembers.Count switch
        {
            0 => "No CardiMembers yet — add your first one to get started!",
            1 => "Here's your CardiMember",
            _ => $"Here are your {cardiMembers.Count} CardiMembers"
        };
        return Success(cardiMembers, message);
    }
}
