using System.Reflection;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Architecture;

/// <summary>
/// A service registered only under a key cannot be taken as a plain constructor dependency: the
/// container has no unkeyed registration to satisfy it, so the class activates fine in a unit
/// test that hands the dependency over directly, and throws the first time the real container
/// builds it.
/// </summary>
/// <remarks>
/// <para>
/// This is not hypothetical. <c>InactivityDetectionService</c> took
/// <see cref="IDeviceSyncService"/> as a constructor parameter, every unit test passed because
/// they injected a substitute, and the worker then threw
/// <c>Unable to resolve service for type 'IDeviceSyncService'</c> on every fifteen-minute tick
/// in dev — with the failure logged as a scheduled-invocation error rather than a crash, so the
/// device-silence safety net was simply off, quietly, until someone read the logs.
/// </para>
/// <para>
/// The one resolution path is <c>IServiceProvider.GetDeviceSyncService(deviceType)</c>, which
/// picks the engine from the configured DeviceType→HealthApi mapping. This test enforces that by
/// reflection over the constructors of the assemblies that could make the mistake, rather than by
/// trusting a comment.
/// </para>
/// </remarks>
public class KeyedServiceInjectionTests
{
    /// <summary>
    /// Types only ever registered with <c>AddKeyed*</c>. Extend this when a new keyed
    /// registration is introduced — see <c>DeviceProviderServiceExtensions</c>.
    /// </summary>
    private static readonly Type[] KeyedOnlyServices = [typeof(IDeviceSyncService)];

    /// <summary>The registration site itself, which legitimately names the type in a factory.</summary>
    private static readonly string[] AllowedDeclaringTypeNames = ["DeviceProviderServiceExtensions"];

    [Fact]
    public void NoConstructorTakesAKeyedOnlyServiceDirectly()
    {
        Assembly[] assemblies =
        [
            typeof(InactivityDetectionService).Assembly,          // CardiTrack.Infrastructure
            typeof(IDeviceSyncService).Assembly,                  // CardiTrack.Application
        ];

        var offenders = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => !AllowedDeclaringTypeNames.Contains(type.Name))
            .SelectMany(type => type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters()
                    .Where(parameter => KeyedOnlyServices.Contains(parameter.ParameterType))
                    .Select(parameter => $"{type.FullName}({parameter.ParameterType.Name} {parameter.Name})")))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These constructors take a keyed-only service directly, which the container cannot "
            + "satisfy — resolve it per device type with IServiceProvider.GetDeviceSyncService "
            + $"instead:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }
}
