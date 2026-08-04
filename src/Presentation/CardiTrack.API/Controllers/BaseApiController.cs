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
