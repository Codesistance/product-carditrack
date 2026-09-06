using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace CardiTrack.API.Extensions;

/// <summary>
/// The outbound HTTP budget for a JWT Bearer scheme's discovery-document and JWKS fetches.
/// </summary>
/// <remarks>
/// <para>
/// Out of the box a bearer scheme's back channel is an infinite <c>SocketsHttpHandler.ConnectTimeout</c>
/// under a 60 s <c>BackchannelTimeout</c>. A connection attempt that never completes — the failure
/// observed on fresh dev instances since 3 September 2026, where the first fetch of Auth0's
/// openid-configuration sat in the connection-pool wait until the minute was up — therefore costs
/// the whole 60 s before the token is rejected with "no security keys", and the handler's
/// key-not-found refresh can pay the same minute again. The mobile client gives up at 30 s, so the
/// caregiver saw a timeout at sign-in either way.
/// </para>
/// <para>
/// Five seconds to connect and ten end to end: a healthy fetch takes 100–300 ms, and a stuck one now
/// fails fast enough for the handler's own refresh retry, and the mobile budget, to absorb it. Both
/// schemes share these numbers so the pipeline's GoogleOidc scheme cannot quietly keep the old ones.
/// </para>
/// </remarks>
public static class OidcBackchannel
{
    /// <summary>How long a single connection attempt (DNS, TCP, TLS) may take.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long one discovery or JWKS request may take end to end.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Bounds <paramref name="options"/>'s back channel. Call from inside the scheme's configure
    /// delegate: <c>JwtBearerPostConfigureOptions</c> builds the <c>Backchannel</c> client and the
    /// <c>ConfigurationManager</c> from these two properties afterwards, so nothing else needs to
    /// change for them to take effect.
    /// </summary>
    public static void Configure(JwtBearerOptions options)
    {
        options.BackchannelHttpHandler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            // Recycle idle connections so an edge or DNS change at the issuer is picked up
            // rather than nursed on one socket for the life of the instance.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        options.BackchannelTimeout = RequestTimeout;
    }
}
