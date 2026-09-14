using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Serilog;

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
/// Five seconds to connect and ten per request. Discovery and JWKS are separate requests, so one
/// configuration load is bounded at twenty seconds, and a connect that never completes fails at
/// five; a healthy fetch takes 100–300 ms. That is fast enough for the handler's own refresh retry,
/// and the mobile client's 30 s budget, to absorb. Both schemes share these numbers so the
/// pipeline's GoogleOidc scheme cannot quietly keep the old ones.
/// </para>
/// <para>
/// <see cref="ConnectTimeout"/> alone was not enough, because it is a budget for the whole
/// connect — not for each address the issuer resolves to. The Auth0 tenant is Cloudflare-fronted
/// and answers with four addresses (two A, two AAAA); <c>MultiConnectSocketAsyncEventArgs</c> tries
/// them one at a time, with no Happy Eyeballs, so a single address that swallows SYNs consumes all
/// five seconds and the other three are never reached. That is what the warm-up kept reporting on
/// roughly two of every five dev cold starts: DNS resolved, TLS never began, and a retry seconds
/// later connected in 160 ms because the resolver handed back a different address first.
/// A <c>ConnectCallback</c> replaces that with one bounded attempt per address, so a dead
/// address costs its own share of the budget rather than the lot.
/// </para>
/// </remarks>
public static class OidcBackchannel
{
    /// <summary>How long a single connection attempt (DNS, TCP, TLS) may take.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long one discovery or JWKS request may take end to end.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The floor on one address's share of <see cref="ConnectTimeout"/>. An issuer that answers
    /// with many addresses must not divide the budget so finely that a healthy edge — 20–50 ms of
    /// handshake from europe-west2, plus whatever a bad minute adds — is cut off as if it were
    /// dead.
    /// </summary>
    internal static readonly TimeSpan MinPerAddressTimeout = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Opens the failure message for the case where every address was tried and none answered.
    /// <see cref="OidcDiscoveryWarmup.DescribeFailure"/> keys on it, so that this failure mode —
    /// new with the per-address callback — names itself in the logs rather than arriving as
    /// another unattributed cancellation.
    /// </summary>
    internal const string AllAddressesFailedMarker = "no address accepted a connection";

    /// <summary>
    /// How long one address may take, given how many the issuer resolved to. A single address gets
    /// the whole budget, which is the pre-existing behaviour; several share it, no finer than
    /// <see cref="MinPerAddressTimeout"/>.
    /// </summary>
    internal static TimeSpan PerAddressTimeout(int addressCount)
    {
        if (addressCount <= 1)
            return ConnectTimeout;

        var share = ConnectTimeout / addressCount;
        return share < MinPerAddressTimeout ? MinPerAddressTimeout : share;
    }

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
            ConnectCallback = static (context, cancellationToken) =>
                ConnectAsync(context.DnsEndPoint, cancellationToken),
        };
        options.BackchannelTimeout = RequestTimeout;
    }

    /// <summary>
    /// Resolves <paramref name="endPoint"/> and connects to the first address that answers, giving
    /// each its own slice of <see cref="ConnectTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Resolver order is kept rather than sorted — preferring IPv4 would very likely paper over the
    /// dev failures on its own, but it would also hide which family is at fault, and it would be
    /// wrong on a host that does have working IPv6. Each address that fails is logged with the
    /// reason instead, so the next look at the logs can say whether it is a family or a particular
    /// Cloudflare edge, and that question can then be settled on evidence.
    /// </remarks>
    internal static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endPoint, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        return await ConnectAsync(endPoint, addresses, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The address loop, with resolution already done — the seam the tests drive, so that "the
    /// first address is dead and the second is not" can be set up without a resolver that answers
    /// that way.
    /// </summary>
    internal static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endPoint, IPAddress[] addresses, CancellationToken cancellationToken)
    {
        var logger = Log.ForContext(typeof(OidcBackchannel));

        var perAddress = PerAddressTimeout(addresses.Length);
        var failures = new List<string>(addresses.Length);
        Exception? last = null;

        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(perAddress);

            try
            {
                await socket.ConnectAsync(address, endPoint.Port, attempt.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();

                // The whole connect budget is gone, or the request was abandoned: stop rather
                // than spend what is left of someone else's timeout on the next address.
                if (cancellationToken.IsCancellationRequested)
                    throw;

                last = ex;
                var reason = DescribeAttempt(ex, perAddress);
                failures.Add($"{address} ({reason})");

                // Warning, for the same reason the warm-up logs at Warning: dev samples
                // Information at 10 % and prod does not ship it at all, and this line is the
                // only record of which address is at fault.
                logger.Warning(
                    "OIDC back channel could not reach {Address} for {Host}:{Port} ({Reason}); trying the next of {AddressCount}",
                    address, endPoint.Host, endPoint.Port, reason, addresses.Length);
            }
        }

        throw new HttpRequestException(
            $"{AllAddressesFailedMarker}: {endPoint.Host}:{endPoint.Port} resolved to {addresses.Length} "
            + $"address(es) and none answered within {perAddress.TotalMilliseconds:0} ms each — "
            + string.Join("; ", failures),
            last);
    }

    /// <summary>
    /// Says why one address did not answer. A per-address timeout arrives as a bare
    /// <see cref="OperationCanceledException"/> carrying nothing useful, so it is named here from
    /// the budget that produced it.
    /// </summary>
    private static string DescribeAttempt(Exception exception, TimeSpan perAddress) => exception switch
    {
        SocketException socket => socket.SocketErrorCode.ToString(),
        OperationCanceledException => $"no answer within {perAddress.TotalMilliseconds:0} ms",
        _ => exception.GetType().Name,
    };
}
