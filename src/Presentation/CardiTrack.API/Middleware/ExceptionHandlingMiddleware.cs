using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.API.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, message) = exception switch
        {
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, "Unauthorized access"),
            ArgumentException => (HttpStatusCode.BadRequest, exception.Message),
            // InvalidOperationException is how EF Core and the BCL report infrastructure
            // faults — treat it as a 500 and never echo its message to clients.
            // Controllers that use it for domain rules catch it themselves.
            KeyNotFoundException => (HttpStatusCode.NotFound, "Resource not found"),
            _ => (HttpStatusCode.InternalServerError, "An internal server error occurred")
        };

        // Expected client faults (mapped 4xx) are warnings; everything else is an error.
        var level = (int)statusCode >= 500 ? LogLevel.Error : LogLevel.Warning;
        _logger.Log(level, exception,
            "{ExceptionType} handling {Method} {Path} => {StatusCode}",
            exception.GetType().Name,
            context.Request.Method,
            context.Request.Path.Value,
            (int)statusCode);

        // Too late to replace the body once the response has started streaming.
        if (context.Response.HasStarted)
            return;

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        var response = new ErrorResponse
        {
            Success = false,
            Message = message,
            TraceId = Activity.Current?.Id ?? context.TraceIdentifier,
            Timestamp = DateTime.UtcNow
        };

        // Include detailed error information in development environment only
        if (_env.IsDevelopment() && exception != null)
        {
            response.Errors = new List<ValidationError>
            {
                new ValidationError
                {
                    Field = "Exception",
                    Message = exception.Message
                }
            };

            // Add stack trace in development
            if (!string.IsNullOrEmpty(exception.StackTrace))
            {
                response.Errors.Add(new ValidationError
                {
                    Field = "StackTrace",
                    Message = exception.StackTrace
                });
            }
        }

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await context.Response.WriteAsync(json);
    }
}
