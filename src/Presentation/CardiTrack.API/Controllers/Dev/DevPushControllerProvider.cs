using System.Reflection;
using CardiTrack.Observability;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace CardiTrack.API.Controllers.Dev;

/// <summary>
/// Decides at startup whether <see cref="DevPushController"/> exists at all. When disabled the
/// controller is dropped from MVC's discovered set, so there is no action to route to and no
/// filter deciding to refuse — a wiring mistake cannot leave a live endpoint behind a guard that
/// was never applied.
/// </summary>
/// <remarks>
/// Removal rather than route-clearing is deliberate. Emptying a controller's selectors leaves its
/// actions behind carrying only their own <c>[HttpPost]</c> selector, which resolves against the
/// application root instead of disappearing — the endpoint would move, not close.
/// <para>
/// Two independent locks, either one sufficient to keep it off:
/// </para>
/// <list type="number">
/// <item><description><c>Dev:PushTokenKey</c> unset. Absent-by-default is why that key appears in
/// no <c>appsettings.json</c> and in no Terraform secret map — a developer opts in on their own
/// machine via user-secrets or an environment variable, and nothing else ever does.</description></item>
/// <item><description>The environment resolves to prod.</description></item>
/// </list>
/// <para>
/// The environment check reads <see cref="DeploymentInfo.EnvironmentName"/>, not
/// <c>IHostEnvironment</c>. Terraform sets <c>ASPNETCORE_ENVIRONMENT</c> to "Dev"/"Prod", not
/// .NET's "Development"/"Production" (see <c>ConfigurationKeys.AspNetCoreEnvironment</c>), so
/// <c>IsDevelopment()</c> is false on deployed dev and <c>IsProduction()</c> is false on deployed
/// prod — either check alone would be wrong in both directions.
/// </para>
/// </remarks>
public sealed class DevPushControllerProvider : IApplicationFeatureProvider<ControllerFeature>
{
    private readonly bool _enabled;

    public DevPushControllerProvider(bool enabled) => _enabled = enabled;

    /// <summary>
    /// True when a key is configured and the environment is anything other than prod. An unset or
    /// unrecognised environment counts as non-prod — matching <see cref="DeploymentInfo"/>'s rule
    /// that a machine which never said which environment it is reports none — because that is a
    /// developer's laptop, and the key check still stands in front of it.
    /// </summary>
    public static bool IsEnabled(string? configuredKey, string? environmentName) =>
        !string.IsNullOrWhiteSpace(configuredKey)
        && !string.Equals(environmentName, "prod", StringComparison.OrdinalIgnoreCase);

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        if (_enabled)
            return;

        feature.Controllers.Remove(typeof(DevPushController).GetTypeInfo());
    }
}
