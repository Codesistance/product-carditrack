using System.Diagnostics;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Shared.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace CardiTrack.UnitTests.Observability;

/// <summary>
/// <see cref="TracingProxy{T}"/> is built by <see cref="DispatchProxy.Create{T,TProxy}"/>, which
/// emits a subclass of it at runtime — so any change that makes the class non-derivable (sealing
/// it, removing its public parameterless constructor) breaks every registration that goes through
/// <see cref="TracingServiceCollectionExtensions.AddScopedWithTracing{TInterface,TImplementation}"/>.
/// </summary>
/// <remarks>
/// The failure mode is what makes this worth pinning: it throws at <em>resolution</em>, so the host
/// starts clean, the container validates, and nothing goes wrong until the first real call. When it
/// shipped (2026-08-14) it took out push dispatch, <c>StatisticalAlertWorker</c> and
/// <c>InactivityDetectionWorker</c> — six hours of no alerts raised and none delivered, visible only
/// as a repeating log line. Compilation cannot catch it; this does.
/// </remarks>
public class TracingProxyResolutionTests
{
    private static readonly ActivitySource Source = new("CardiTrack.Tests.TracingProxy");

    /// <summary>
    /// The three interfaces <c>PushServiceExtensions.AddPushServices</c> registers with tracing.
    /// Named explicitly rather than reflected over the registration, so adding a fourth is a
    /// deliberate line here — and so this test needs no Firebase credential to run.
    /// </summary>
    public static TheoryData<Type> TracedPushInterfaces =>
    [
        typeof(INotificationChannel),
        typeof(IDispatchService),
        typeof(IAckDeliveryService)
    ];

    [Theory]
    [MemberData(nameof(TracedPushInterfaces))]
    public void EveryTracedPushInterface_CanActuallyBeProxied(Type interfaceType)
    {
        var target = Substitute.For([interfaceType], []);

        var create = typeof(TracingProxy<>).MakeGenericType(interfaceType)
            .GetMethod(nameof(TracingProxy<object>.Create))!;

        // Throws ArgumentException("The base type ... cannot be sealed") if TracingProxy<T> is
        // sealed — the exact exception that took push and both alert workers down.
        var proxy = create.Invoke(null, [target, Source]);

        Assert.NotNull(proxy);
        Assert.True(interfaceType.IsInstanceOfType(proxy));
    }

    [Fact]
    public void AddScopedWithTracing_ResolvesAProxyThatForwardsToTheImplementation()
    {
        var services = new ServiceCollection();
        services.AddScopedWithTracing<IGreeter, Greeter>(Source);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IGreeter>();

        Assert.IsNotType<Greeter>(resolved);
        Assert.Equal("hello ada", resolved.Greet("ada"));
    }

    [Fact]
    public async Task AddScopedWithTracing_PropagatesTheImplementationsExceptionUnwrapped()
    {
        // DispatchProxy surfaces a thrown exception as TargetInvocationException; TracingProxy
        // unwraps it. A caller catching a specific exception type (DispatchService's per-row
        // handlers, the workers' phase guards) depends on that staying true.
        var services = new ServiceCollection();
        services.AddScopedWithTracing<IGreeter, ThrowingGreeter>(Source);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IGreeter>();

        var sync = Assert.Throws<InvalidOperationException>(() => resolved.Greet("ada"));
        Assert.Equal("boom", sync.Message);

        var async = await Assert.ThrowsAsync<InvalidOperationException>(() => resolved.GreetAsync("ada"));
        Assert.Equal("boom async", async.Message);
    }

    public interface IGreeter
    {
        string Greet(string name);

        Task<string> GreetAsync(string name);
    }

    private sealed class Greeter : IGreeter
    {
        public string Greet(string name) => $"hello {name}";

        public Task<string> GreetAsync(string name) => Task.FromResult($"hello {name}");
    }

    private sealed class ThrowingGreeter : IGreeter
    {
        public string Greet(string name) => throw new InvalidOperationException("boom");

        public Task<string> GreetAsync(string name) => throw new InvalidOperationException("boom async");
    }
}
