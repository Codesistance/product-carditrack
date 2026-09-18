using System.Net;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// The provider has refused this connection's refresh token outright — revoked, already spent, or
/// consent withdrawn. <see cref="OAuthTokenRefreshService"/> has already retired the connection to
/// <c>TokenExpired</c> before throwing, so a caller catching this is being told what happened
/// rather than asked to handle it.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the plain <see cref="InvalidOperationException"/> the rest of the refresh path
/// throws because the two deserve different log levels. A provider having a bad minute is worth an
/// error and a stack; a wearer taking their consent back is a settled state the system handled
/// exactly as designed, and logging it as a failure raises the service's error rate every quarter
/// hour for as long as the device stays unreconnected.
/// </para>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> so the call sites that already catch that —
/// <see cref="Services.DeviceAuthRecoveryService"/> and the manual-sync path — keep behaving as
/// they did.
/// </para>
/// </remarks>
public class DeviceGrantRejectedException : InvalidOperationException
{
    /// <summary>The connection whose grant the provider refused, already retired to <c>TokenExpired</c>.</summary>
    public Guid DeviceConnectionId { get; }

    /// <summary>The status the token endpoint answered with, for the record.</summary>
    public HttpStatusCode StatusCode { get; }

    public DeviceGrantRejectedException(Guid deviceConnectionId, HttpStatusCode statusCode, string message)
        : base(message)
    {
        DeviceConnectionId = deviceConnectionId;
        StatusCode = statusCode;
    }
}
