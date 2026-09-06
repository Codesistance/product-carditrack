using System.Diagnostics;
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
/// </remarks>
public sealed class OidcDiscoveryWarmup(
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitor<JwtBearerOptions> options,
    ILogger<OidcDiscoveryWarmup> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
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

            _ = WarmAsync(scheme.Name, manager, _stopping.Token);
        }
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
        var started = Stopwatch.GetTimestamp();
        try
        {
            await manager.GetConfigurationAsync(ct);
            logger.LogInformation(
                "OIDC discovery for {Scheme} warmed in {ElapsedMs:0} ms",
                scheme, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is stopping; nothing to report.
        }
        catch (Exception ex)
        {
            // Not an error: the first authenticated request will fetch it under the same budget.
            // Logged at Warning so it reaches Datadog in prod, where Information does not.
            logger.LogWarning(ex,
                "OIDC discovery for {Scheme} could not be warmed after {ElapsedMs:0} ms; the first authenticated request will fetch it",
                scheme, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
