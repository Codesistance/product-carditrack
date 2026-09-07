using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
public abstract class BaseApiController : ControllerBase
{
    protected readonly IUserContext UserContext;
    protected readonly ILogger Logger;

    protected BaseApiController(IUserContext userContext, ILogger logger)
    {
        UserContext = userContext;
        Logger = logger;
    }

    /// <summary>
    /// Returns a successful API response with data
    /// </summary>
    protected ActionResult<ApiResponse<T>> Success<T>(T data, string message = "Here you go!")
    {
        return Ok(new ApiResponse<T>
        {
            Success = true,
            Message = message,
            Data = data,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Returns a 202 API response with data: the work is queued, and the body says where to poll.
    /// </summary>
    /// <remarks>
    /// Named Queued rather than Accepted so it does not shadow <see cref="ControllerBase.Accepted(object)"/>,
    /// which other controllers hand a ready-made envelope. Built here rather than as
    /// <c>Accepted(Success(data).Value)</c> in a controller: the
    /// implicit conversion from an <see cref="OkObjectResult"/> to
    /// <see cref="ActionResult{TValue}"/> fills <c>Result</c>, not <c>Value</c>, so that shape
    /// sends a 202 with no body and the client reads an empty envelope. Export shipped that way
    /// behind a plan gate nobody could pass, so nothing noticed until the gate came off.
    /// </remarks>
    protected ActionResult<ApiResponse<T>> Queued<T>(T data, string message = "On it!")
    {
        return StatusCode(StatusCodes.Status202Accepted, new ApiResponse<T>
        {
            Success = true,
            Message = message,
            Data = data,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Returns a 201 API response with data
    /// </summary>
    protected ActionResult<ApiResponse<T>> Created<T>(T data, string message = "All set!")
    {
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<T>
        {
            Success = true,
            Message = message,
            Data = data,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Returns a successful API response without data
    /// </summary>
    protected ActionResult<ApiResponse<object>> Success(string message = "All done!")
    {
        return Ok(new ApiResponse<object>
        {
            Success = true,
            Message = message,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Returns a 400 response populated with FluentValidation errors
    /// </summary>
    protected ActionResult ValidationFailed(ValidationResult result)
    {
        return BadRequest(new ErrorResponse
        {
            Success = false,
            Message = "Some details need a second look — please check them and try again.",
            Errors = result.Errors.Select(e => new ValidationError
            {
                Field = e.PropertyName,
                Message = e.ErrorMessage
            }).ToList(),
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Returns an error response
    /// </summary>
    protected ActionResult Error(string message, int statusCode = 400)
    {
        return StatusCode(statusCode, new ErrorResponse
        {
            Success = false,
            Message = message,
            Timestamp = DateTime.UtcNow
        });
    }
}
