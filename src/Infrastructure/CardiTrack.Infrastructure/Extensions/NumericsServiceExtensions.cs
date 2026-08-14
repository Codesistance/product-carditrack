using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.Infrastructure.Extensions;

/// <summary>
/// Wires the in-process numerical engine (Math.NET Numerics). Stateless singletons — the
/// library never leaves the process, so this is not a subprocessor registration.
/// </summary>
public static class NumericsServiceExtensions
{
    public static IServiceCollection AddNumerics(this IServiceCollection services)
    {
        services.AddSingleton<ISsaDecomposition, SsaDecomposition>();
        services.AddSingleton<IDescriptiveStatistics, MathNetDescriptiveStatistics>();
        return services;
    }
}
