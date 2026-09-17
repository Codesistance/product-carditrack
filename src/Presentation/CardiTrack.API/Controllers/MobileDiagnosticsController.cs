using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Observability;
using CardiTrack.Shared.Http;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog.Context;

namespace CardiTrack.API.Controllers;

/// <summary>
/// Receives the mobile app's own Error-and-above log lines — above all the unhandled
/// exception <c>AppLogging.HookUnhandledExceptions</c> writes in the last moments before the
/// process dies — and re-emits each one through this host's logger under the mobile service
/// name, so it reaches Datadog (docs/technical/apm_setup_runbook.md §5).
/// </summary>
/// <remarks>
/// <para>
/// Exists because the phone cannot do this itself: the MAUI Datadog SDK has no entry for this
/// org's UK1 site, so mobile telemetry has been inert since 2026-08-11, and a TestFlight crash
/// report arrives with the managed frames already unwound (see apps/mobile/readme.md,
/// "Symbolicating an iOS crash"). The exception's text survives only in the on-device Serilog
/// file, and this is how that text gets off the device without a Mac.
/// </para>
/// <para>
/// Anonymous by necessity — the crash it was built for happens on the sign-in screen — so the
/// shared key in <see cref="MobileDiagnosticsContract.KeyHeader"/> is the whole of the
/// authorization, backed by the per-IP rate limit on this route (appsettings.json). The key
/// is compiled into store builds, so it is a limiter rather than a secret
/// (<see cref="T:CardiTrack.Infrastructure.Security.MobileDiagnosticsKey"/> says why that is enough here), and nothing accepted is
/// trusted beyond being logged: no identity is inferred, no record is written, no other service
/// is called.
/// </para>
/// <para>
/// Each entry becomes one log event carrying <see cref="LogRelay.ServiceProperty"/>, which the
/// Datadog provider routes to a sink whose resource is <see cref="ApmServiceNames.Mobile"/>.
/// The exception rides as <c>error.stack</c>/<c>error.kind</c>/<c>error.message</c>, the
/// attribute names Datadog's Error Tracking groups on, so one iOS crash repeating across a
/// hundred phones is one issue with a hundred occurrences rather than a hundred lines; the
/// structured frames, the screen, the thread and the device state go on as <c>Mobile*</c>
/// attributes beside it.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/v1/mobile/diagnostics")]
[Produces("application/json")]
public class MobileDiagnosticsController : ControllerBase
{
    private readonly IMobileDiagnosticsKey _key;
    private readonly IValidator<MobileDiagnosticsLogRequest> _validator;
    private readonly ILogger<MobileDiagnosticsController> _logger;

    public MobileDiagnosticsController(
        IMobileDiagnosticsKey key,
        IValidator<MobileDiagnosticsLogRequest> validator,
        ILogger<MobileDiagnosticsController> logger)
    {
        _key = key;
        _validator = validator;
        _logger = logger;
    }

    [HttpPost("logs")]
    [RequestSizeLimit(MobileDiagnosticsContract.MaxBodyBytes)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> Post(
        [FromBody] MobileDiagnosticsLogRequest request, CancellationToken ct)
    {
        // 404 before 401: an environment with no key has no endpoint, and saying "unauthorized"
        // would tell a caller there is something here to be authorized for.
        if (!_key.IsConfigured)
            return NotFound(Error("Not found."));

        if (!_key.Matches(Request.Headers[MobileDiagnosticsContract.KeyHeader].ToString()))
        {
            // One bare 401 for missing and wrong alike — no hint which.
            return Unauthorized(Error("Unauthorized."));
        }

        var validation = await _validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            var error = Error("The diagnostics batch is not valid.");
            error.Errors = validation.Errors
                .Select(e => new ValidationError { Field = e.PropertyName, Message = e.ErrorMessage })
                .ToList();
            return BadRequest(error);
        }

        foreach (var entry in request.Entries)
            Relay(request, entry);

        return Accepted(new ApiResponse<object>
        {
            Success = true,
            Message = $"Recorded {request.Entries.Count} {(request.Entries.Count == 1 ? "entry" : "entries")}.",
            Timestamp = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// The envelope's fields go on as properties rather than into the message so every relayed
    /// line is queryable by build, platform and handset the same way the API's own request
    /// lines are. <c>ClientVersion</c>/<c>ClientPlatform</c> are the names
    /// <c>ClientVersionMiddleware</c> already writes; the body's values win over the request
    /// headers here because the body describes the build that wrote the line, which is the
    /// same build — and the one that must be right if the two ever disagree.
    /// </summary>
    private void Relay(MobileDiagnosticsLogRequest envelope, MobileDiagnosticsLogEntry entry)
    {
        var level = entry.Level.Trim().ToLowerInvariant() switch
        {
            "fatal" => LogLevel.Critical,
            "warning" => LogLevel.Warning,
            _ => LogLevel.Error,
        };

        var scopes = new List<IDisposable>
        {
            LogContext.PushProperty(LogRelay.ServiceProperty, ApmServiceNames.Mobile),
            LogContext.PushProperty("MobileTimestamp", entry.Timestamp),
        };

        try
        {
            // The build and the handset.
            Push(scopes, "ClientPlatform", envelope.Platform);
            Push(scopes, "ClientVersion", envelope.AppVersion);
            Push(scopes, "MobileDevice", envelope.Device);
            Push(scopes, "MobileManufacturer", envelope.Manufacturer);
            Push(scopes, "MobileModel", envelope.Model);
            Push(scopes, "MobileOs", envelope.OsVersion);
            Push(scopes, "MobileOsDescription", envelope.OsDescription);
            Push(scopes, "MobileArchitecture", envelope.Architecture);
            Push(scopes, "MobileRuntime", envelope.Runtime);
            Push(scopes, "MobileLocale", envelope.Locale);
            Push(scopes, "MobileTimeZone", envelope.TimeZone);
            Push(scopes, "MobileAssemblyVersion", envelope.AppAssemblyVersion);
            Push(scopes, "MobileModuleVersionId", envelope.ModuleVersionId);
            Push(scopes, "MobileInstallId", envelope.InstallId);

            // The moment.
            Push(scopes, "MobileSource", entry.Source);
            Push(scopes, "MobileScreen", entry.Screen);
            Push(scopes, "MobileNetwork", entry.NetworkAccess);
            Push(scopes, "MobileThreadName", entry.ThreadName);
            PushValue(scopes, "MobileUptimeSeconds", entry.UptimeSeconds);
            PushValue(scopes, "MobileThreadId", entry.ThreadId);
            PushValue(scopes, "MobileIsMainThread", entry.IsMainThread);
            PushValue(scopes, "MobileManagedMemoryBytes", entry.ManagedMemoryBytes);
            PushValue(scopes, "MobileWorkingSetBytes", entry.WorkingSetBytes);
            Push(scopes, "MobileRecentLog", entry.RecentLog);

            if (!string.IsNullOrWhiteSpace(entry.Exception))
            {
                // Datadog Error Tracking's grouping attributes. The app names the type and
                // message outright; failing that, the first line of a .NET Exception.ToString()
                // is "Namespace.Type: message", so kind and message come from splitting it once.
                var header = FirstLine(entry.Exception);
                var separator = header.IndexOf(": ", StringComparison.Ordinal);
                var kind = entry.ExceptionType ?? (separator > 0 ? header[..separator] : header);
                var message = entry.ExceptionMessage ?? (separator > 0 ? header[(separator + 2)..] : header);

                Push(scopes, "error.stack", entry.Exception);
                Push(scopes, "error.kind", kind);
                Push(scopes, "error.message", message);
            }

            if (entry.Frames.Count > 0)
                scopes.Add(LogContext.PushProperty("MobileFrames", entry.Frames, destructureObjects: true));

            _logger.Log(
                level,
                "Mobile {MobileLevel} relayed from {MobileSourceContext}: {MobileMessage}",
                entry.Level, Sanitize(entry.Source) ?? "app", Sanitize(entry.Message));
        }
        finally
        {
            // Innermost first, the order LogContext expects.
            for (var i = scopes.Count - 1; i >= 0; i--)
                scopes[i].Dispose();
        }
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.TrimStart();
        var end = trimmed.IndexOfAny(['\r', '\n']);
        return end >= 0 ? trimmed[..end] : trimmed;
    }

    private static void Push(List<IDisposable> scopes, string name, string? value)
    {
        var clean = Sanitize(value);
        if (!string.IsNullOrWhiteSpace(clean))
            scopes.Add(LogContext.PushProperty(name, clean));
    }

    /// <summary>
    /// Newlines and other controls in a relayed field would split a log event or inject
    /// attributes. Spaces keep the text searchable without giving the payload a second line.
    /// </summary>
    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
                chars[i] = ' ';
        }

        return new string(chars);
    }

    private static void PushValue<T>(List<IDisposable> scopes, string name, T? value) where T : struct
    {
        if (value.HasValue)
            scopes.Add(LogContext.PushProperty(name, value.Value));
    }

    private static ErrorResponse Error(string message) => new()
    {
        Success = false,
        Message = message,
        Timestamp = DateTime.UtcNow,
    };
}
