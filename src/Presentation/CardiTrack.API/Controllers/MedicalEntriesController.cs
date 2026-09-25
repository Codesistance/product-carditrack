using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

/// <summary>
/// A member's medical information as a ledger of lines — conditions, allergies, medications —
/// each with who wrote it and when it was last confirmed, and a history of what changed.
/// </summary>
/// <remarks>
/// Every action answers with the whole ledger as it now stands. A denied or unknown member or line
/// is 404, never 403 — see <see cref="ICardiMemberAccessService"/>. The single note on the member
/// (<c>medicalNotes</c> on the profile) is kept as a summary of the current lines for older app
/// builds; see <see cref="IMedicalEntryService"/>.
/// </remarks>
[Authorize]
[AuditHealthDataAccess("ViewMedicalEntries")]
[Route("api/v1/cardimembers/{cardiMemberId:guid}/medical-entries")]
public class MedicalEntriesController : BaseApiController
{
    private readonly IMedicalEntryService _entries;
    private readonly IValidator<MedicalEntryRequest> _validator;

    public MedicalEntriesController(
        IUserContext userContext,
        ILogger<MedicalEntriesController> logger,
        IMedicalEntryService entries,
        IValidator<MedicalEntryRequest> validator)
        : base(userContext, logger)
    {
        _entries = entries;
        _validator = validator;
    }

    /// <summary>The current lines, grouped by kind, and the history of changed and removed ones.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<MedicalEntriesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<ApiResponse<MedicalEntriesResponse>>> Get(Guid cardiMemberId, CancellationToken ct) =>
        Run(() => _entries.GetAsync(UserContext.UserId, cardiMemberId, ct));

    /// <summary>Adds a line.</summary>
    [HttpPost]
    [AuditHealthDataAccess("AddMedicalEntry")]
    [ProducesResponseType(typeof(ApiResponse<MedicalEntriesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MedicalEntriesResponse>>> Add(
        Guid cardiMemberId, [FromBody] MedicalEntryRequest request, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        var validation = await _validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        return await Run(
            () => _entries.AddAsync(UserContext.UserId, cardiMemberId, request.Kind, request.Text, ct),
            "Added.");
    }

    /// <summary>Changes a current line. The old wording stays in the history as changed.</summary>
    [HttpPut("{entryId:guid}")]
    [AuditHealthDataAccess("ReviseMedicalEntry")]
    [ProducesResponseType(typeof(ApiResponse<MedicalEntriesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MedicalEntriesResponse>>> Revise(
        Guid cardiMemberId, Guid entryId, [FromBody] MedicalEntryRequest request, CancellationToken ct)
    {
        if (NotSignedIn(out var error))
            return error;

        var validation = await _validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        return await Run(
            () => _entries.ReviseAsync(UserContext.UserId, cardiMemberId, entryId, request.Kind, request.Text, ct),
            "Saved.");
    }

    /// <summary>Takes a current line off the list. It stays in the history.</summary>
    [HttpPost("{entryId:guid}/remove")]
    [AuditHealthDataAccess("RemoveMedicalEntry")]
    [ProducesResponseType(typeof(ApiResponse<MedicalEntriesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<ApiResponse<MedicalEntriesResponse>>> Remove(
        Guid cardiMemberId, Guid entryId, CancellationToken ct) =>
        Run(() => _entries.RemoveAsync(UserContext.UserId, cardiMemberId, entryId, ct), "Removed — it's kept in the history.");

    /// <summary>Records that a current line still holds.</summary>
    [HttpPost("{entryId:guid}/confirm")]
    [AuditHealthDataAccess("ConfirmMedicalEntry")]
    [ProducesResponseType(typeof(ApiResponse<MedicalEntriesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<ApiResponse<MedicalEntriesResponse>>> Confirm(
        Guid cardiMemberId, Guid entryId, CancellationToken ct) =>
        Run(() => _entries.ConfirmAsync(UserContext.UserId, cardiMemberId, entryId, ct), "Thanks — we'll take it as current.");

    /// <summary>
    /// Deletes a line outright, current or in the history. A real delete, not an archive — the
    /// family's words about someone are theirs to have removed.
    /// </summary>
    [HttpDelete("{entryId:guid}")]
    [AuditHealthDataAccess("EraseMedicalEntry")]
    [ProducesResponseType(typeof(ApiResponse<MedicalEntriesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<ApiResponse<MedicalEntriesResponse>>> Erase(
        Guid cardiMemberId, Guid entryId, CancellationToken ct) =>
        Run(() => _entries.EraseAsync(UserContext.UserId, cardiMemberId, entryId, ct), "Deleted.");

    /// <summary>
    /// The shape every action shares: signed in, then the call, with a missing member or line as
    /// 404 and a list that has outgrown what it can hold as 400.
    /// </summary>
    private async Task<ActionResult<ApiResponse<MedicalEntriesResponse>>> Run(
        Func<Task<MedicalEntriesResponse>> call, string message = "Here you go!")
    {
        if (NotSignedIn(out var error))
            return error;

        try
        {
            return Success(await call(), message);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, StatusCodes.Status400BadRequest);
        }
    }

    private bool NotSignedIn(out ActionResult error)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            error = Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
            return true;
        }

        error = null!;
        return false;
    }
}
