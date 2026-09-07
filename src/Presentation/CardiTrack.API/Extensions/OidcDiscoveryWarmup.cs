using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CardiTrack.API.Extensions;

/// <summary>
/// Fetches every JWT Bearer scheme's discovery document, and with it the signing keys, as the host
/// starts — instead of on the first caregiver request that needs a token validated.
/// </summary>
/// <remarks>
/// <para>
/// <c>JwtBearerHandler</c> loads its <c>ConfigurationManager</c> lazily, so on a fresh instance the
/// first authenticated request pays for the round trip to the issuer. Dev runs at zero minimum
/// instances, which makes that every sign-in after an idle gap; and when that first connection
/// hangs (see <see cref="OidcBackchannel"/>) it is a caregiver, not a startup probe, who waits.
/// </para>
/// <para>
/// The fetch is started, not awaited. Readiness must not depend on the issuer being reachable, and
/// a hung connection costs the same whether it stalls the startup probe or the first request. If
/// the warm-up has not finished when a request arrives, the handler's own fetch takes over under
/// the same timeouts, and <c>ConfigurationManager</c> serialises the two so nothing is fetched
/// twice.
/// </para>
/// <para>
/// One attempt was not enough. The first dev instance after PR #514 hit the 5 s connect timeout
/// once, gave up, and left the first caregiver to pay the retry — the very cost the warm-up exists
/// to take off them. A fresh instance's first outbound connection failing is the common case on
/// Cloud Run (cold NAT path, cold DNS), and the second attempt a few seconds later usually lands,
/// so each scheme now gets <see cref="MaxAttempts"/> tries with <see cref="Backoffs"/> between
/// them. The whole sequence stays fire-and-forget: at worst it runs for about a minute in the
/// background and then defers to the first request exactly as before.
/// </para>
/// </remarks>
public sealed class OidcDiscoveryWarmup(
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitor<JwtBearerOptions> options,
    ILogger<OidcDiscoveryWarmup> logger,
    TimeProvider? timeProvider = null) : IHostedService, IDisposable
{
    /// <summary>
    /// The pause before each retry, in order. Short and fixed rather than exponential: the aim is
    /// to outlast a cold path on a fresh instance, not to survive an issuer outage — after this
    /// the first request tries again anyway.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> Backoffs =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    /// <summary>How many times one scheme's discovery is attempted before the warm-up gives up.</summary>
    public static int MaxAttempts => Backoffs.Count + 1;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Completes when every warm-up that <see cref="StartAsync"/> kicked off has ended, however it
    /// ended. Nothing in the host awaits this — the tests do, so they can observe a fire-and-forget
    /// sequence without polling.
    /// </summary>
    internal Task Completion { get; private set; } = Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // An aborted startup cancels the warm-ups it kicked off; once startup has completed
        // only StopAsync does, which is why the registration lives no longer than this method.
        using var abortedStartup = cancellationToken.Register(
            static state => ((CancellationTokenSource)state!).Cancel(), _stopping);

        var warmups = new List<Task>();
        foreach (var scheme in await schemes.GetAllSchemesAsync())
        {
            if (scheme.HandlerType != typeof(JwtBearerHandler))
                continue;

            // Resolving the named options runs JwtBearerPostConfigureOptions, which is what
            // creates the ConfigurationManager from Authority — the same instance the handler
            // will use, so warming it here is warming the handler.
            var manager = options.Get(scheme.Name).ConfigurationManager;
            if (manager is null)
                continue;

            warmups.Add(WarmAsync(scheme.Name, manager, _stopping.Token));
        }

        Completion = Task.WhenAll(warmups);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();

    private async Task WarmAsync(
        string scheme, IConfigurationManager<OpenIdConnectConfiguration> manager, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await manager.GetConfigurationAsync(ct);

                // Warning, not Information, on purpose. Dev samples Information to 10 %, so the
                // line that says the warm-up worked — one per instance, per scheme — was almost
                // never there when someone came looking, and prod does not ship Information at
                // all. A single startup line per instance is worth seeing, and Warning is the
                // level that reaches Datadog in both environments.
                logger.LogWarning(
                    "OIDC discovery for {Scheme} warmed in {ElapsedMs:0} ms on attempt {Attempt} of {MaxAttempts}",
                    scheme, Stopwatch.GetElapsedTime(started).TotalMilliseconds, attempt, MaxAttempts);
                return;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Host is stopping; nothing to report. Not narrowed to OperationCanceledException
                // because ConfigurationManager wraps whatever its fetch threw — the cancellation
                // included — in its own InvalidOperationException.
                return;
            }
            catch (Exception ex)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                var failure = DescribeFailure(ex, elapsed);

                if (attempt == MaxAttempts)
                {
                    // Not an error: the first authenticated request tries again under the same
                    // budget, and may hit the same wall. Logged at Warning so it reaches Datadog
                    // in prod, where Information does not.
                    logger.LogWarning(ex,
                        "OIDC discovery for {Scheme} could not be warmed after {ElapsedMs:0} ms on attempt {Attempt} of {MaxAttempts} ({Failure}); the first authenticated request will try again",
                        scheme, elapsed.TotalMilliseconds, attempt, MaxAttempts, failure);
                    return;
                }

                logger.LogWarning(ex,
                    "OIDC discovery for {Scheme} failed after {ElapsedMs:0} ms on attempt {Attempt} of {MaxAttempts} ({Failure}); retrying in {BackoffSeconds} s",
                    scheme, elapsed.TotalMilliseconds, attempt, MaxAttempts, failure, Backoffs[attempt - 1].TotalSeconds);
            }

            try
            {
                await Task.Delay(Backoffs[attempt - 1], _time, ct);
            }
            catch (OperationCanceledException)
            {
                return; // Host stopped during the back-off.
            }
        }
    }

    /// <summary>
    /// Says which leg of the fetch gave way, so the next investigation can tell DNS from connect
    /// from TLS without a packet capture. The thrown exception is <c>ConfigurationManager</c>'s
    /// generic IDX20803 wrapper; the answer is somewhere down its inner chain.
    /// </summary>
    /// <remarks>
    /// A connect that never completes surfaces as an <see cref="OperationCanceledException"/> or
    /// <see cref="TimeoutException"/> rather than a socket error, and the same pair is what the
    /// request budget throws. The two are told apart by how long the attempt took: the connect
    /// timeout fires at <see cref="OidcBackchannel.ConnectTimeout"/>, well inside
    /// <see cref="OidcBackchannel.RequestTimeout"/>.
    /// </remarks>
    internal static string DescribeFailure(Exception exception, TimeSpan elapsed)
    {
        var chain = new List<Exception>();
        for (var e = exception; e is not null; e = e.InnerException)
            chain.Add(e);

        var root = chain[^1];
        var rootType = root.GetType().Name;

        if (chain.OfType<SocketException>().FirstOrDefault() is { } socket)
        {
            return socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                ? $"{rootType}: DNS lookup failed ({socket.SocketErrorCode})"
                : $"{rootType}: TCP connect failed ({socket.SocketErrorCode})";
        }

        if (chain.OfType<AuthenticationException>().Any())
            return $"{rootType}: TLS handshake failed";

        if (chain.Any(static e => e is OperationCanceledException or TimeoutException))
        {
            return elapsed < OidcBackchannel.RequestTimeout
                ? $"{rootType}: timed out in the connect phase — DNS, TCP or TLS did not complete within the {OidcBackchannel.ConnectTimeout.TotalSeconds:0} s ConnectTimeout"
                : $"{rootType}: timed out waiting for the response within the {OidcBackchannel.RequestTimeout.TotalSeconds:0} s BackchannelTimeout";
        }

        if (chain.OfType<HttpRequestException>().FirstOrDefault(static e => e.HttpRequestError != HttpRequestError.Unknown) is { } http)
            return $"{rootType}: {http.HttpRequestError}";

        return $"{rootType}: {root.Message}";
    }
}
