using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

/// <summary>
/// The identity lookup runs after the provider has already issued a grant, so anything short of
/// the caller's own cancellation has to come back as "unknown account" — an exception escaping
/// here would abandon that grant.
/// </summary>
public class DeviceAccountIdentityResolverTests
{
    private readonly IDeviceApiClient _client = Substitute.For<IDeviceApiClient>();

    private DeviceAccountIdentityResolver CreateSut()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new List<DeviceProviderSettings>
        {
            new() { Provider = nameof(HealthApi.GoogleHealth), DeviceTypes = ["Fitbit"] },
        }));
        services.AddKeyedSingleton(HealthApi.GoogleHealth, _client);
        return new DeviceAccountIdentityResolver(
            services.BuildServiceProvider(), NullLogger<DeviceAccountIdentityResolver>.Instance);
    }

    [Fact]
    public async Task ReturnsTheProvidersHealthUserId()
    {
        _client.GetHealthUserIdAsync("token").Returns("ACCOUNT_A");

        Assert.Equal("ACCOUNT_A", await CreateSut().TryResolveAsync(DeviceType.Fitbit, "token"));
    }

    [Fact]
    public async Task AnHttpTimeout_IsAnUnknownAccount_NotAnEscapingCancellation()
    {
        // HttpClient reports its own timeout as a TaskCanceledException.
        _client.GetHealthUserIdAsync("token").Returns<Task<string?>>(_ => throw new TaskCanceledException());

        Assert.Null(await CreateSut().TryResolveAsync(DeviceType.Fitbit, "token"));
    }

    [Fact]
    public async Task AProviderFailure_IsAnUnknownAccount()
    {
        _client.GetHealthUserIdAsync("token").Returns<Task<string?>>(_ => throw new HttpRequestException("down"));

        Assert.Null(await CreateSut().TryResolveAsync(DeviceType.Fitbit, "token"));
    }

    [Fact]
    public async Task TheCallersOwnCancellation_Propagates()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _client.GetHealthUserIdAsync("token").Returns<Task<string?>>(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateSut().TryResolveAsync(DeviceType.Fitbit, "token", cancelled.Token));
    }
}
