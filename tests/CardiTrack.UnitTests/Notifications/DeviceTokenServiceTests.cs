using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// Registration when the push token arriving is one another install already holds — a cloned
/// emulator image, a device-to-device transfer, a restore that carried the Firebase installation
/// across. The upsert keys on (UserId, DeviceId) and the unique index keys on the token's
/// fingerprint, so before this the second install's insert died on the index and the caregiver got
/// a 500 on every attempt.
/// </summary>
public class DeviceTokenServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPushDeviceTokenRepository _tokens = Substitute.For<IPushDeviceTokenRepository>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly INotificationGapResolver _gapResolver = Substitute.For<INotificationGapResolver>();

    private readonly FixedTimeProvider _timeProvider = new(new DateTimeOffset(2026, 9, 18, 20, 46, 0, TimeSpan.Zero));

    private const string RawToken = "fcm-token-shared-by-two-installs";

    public DeviceTokenServiceTests()
    {
        _unitOfWork.PushDeviceTokens.Returns(_tokens);
        _encryption.Encrypt(Arg.Any<string>()).Returns(call => $"ciphertext:{call.Arg<string>()}");
    }

    private DeviceTokenService CreateSut() =>
        new(_unitOfWork, _encryption, _gapResolver, _timeProvider);

    private Task<PushDeviceRegistration> RegisterAsync(Guid userId, string deviceId) =>
        CreateSut().RegisterAsync(
            userId,
            deviceId,
            DevicePlatform.Android,
            appVersion: "1.0+1",
            RawToken,
            OsAuthorizationStatus.Granted,
            safetyChannelEnabled: true);

    private static PushDeviceToken HolderRow(Guid userId, string deviceId) => new()
    {
        UserId = userId,
        DeviceId = deviceId,
        Platform = DevicePlatform.Android,
        Token = "ciphertext",
        TokenFingerprint = "whatever-the-service-computes",
        OsAuthorizationStatus = OsAuthorizationStatus.Granted
    };

    [Fact]
    public async Task RegisterAsync_WhenAnotherUsersInstallHoldsTheToken_TakesItAndNamesThemAsDisplaced()
    {
        var arriving = Guid.NewGuid();
        var previous = Guid.NewGuid();
        var held = HolderRow(previous, "device-of-the-first-install");

        _tokens.GetByUserAndDeviceAsync(arriving, "device-of-the-second-install", Arg.Any<CancellationToken>())
            .Returns((PushDeviceToken?)null);
        _tokens.GetByFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(held);

        var registration = await RegisterAsync(arriving, "device-of-the-second-install");

        _tokens.Received(1).Remove(held);
        await _tokens.Received(1).AddAsync(Arg.Is<PushDeviceToken>(t => t.UserId == arriving));
        Assert.Equal(previous, registration.DisplacedUserId);
        Assert.Equal(arriving, registration.Token.UserId);
    }

    [Fact]
    public async Task RegisterAsync_WhenAnotherUsersInstallHoldsTheToken_ReconcilesReachabilityForBoth()
    {
        // The displaced caregiver just lost push without doing anything. Re-evaluating their
        // reachability is what arms PUSH_UNREACHABLE and tells them; skip it and they go silently
        // unreachable.
        var arriving = Guid.NewGuid();
        var previous = Guid.NewGuid();

        _tokens.GetByUserAndDeviceAsync(arriving, "device-b", Arg.Any<CancellationToken>())
            .Returns((PushDeviceToken?)null);
        _tokens.GetByFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(HolderRow(previous, "device-a"));

        await RegisterAsync(arriving, "device-b");

        await _gapResolver.Received(1).ResolveForUserAsync(arriving, Arg.Any<CancellationToken>());
        await _gapResolver.Received(1).ResolveForUserAsync(previous, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_WhenTheSameInstallReRegisters_KeepsItsOwnRow()
    {
        // The ordinary case: the foreground heartbeat, several times a day. The row the
        // fingerprint lookup finds is the caller's own, and deleting it would churn a row —
        // and a Tier 1 ciphertext — on every single registration.
        var user = Guid.NewGuid();
        var own = HolderRow(user, "device-a");

        _tokens.GetByUserAndDeviceAsync(user, "device-a", Arg.Any<CancellationToken>()).Returns(own);
        _tokens.GetByFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(own);

        var registration = await RegisterAsync(user, "device-a");

        _tokens.DidNotReceive().Remove(Arg.Any<PushDeviceToken>());
        _tokens.Received(1).Update(own);
        Assert.Null(registration.DisplacedUserId);
    }

    [Fact]
    public async Task RegisterAsync_WhenTheSameUserReinstalled_TakesTheTokenWithoutADisplacedReconcile()
    {
        // A reinstall mints a new device id but can keep the provider's token. The old row is
        // still this caller's, so there is nobody to tell about losing it — and reconciling the
        // same user twice would be a second pass over the same rules for nothing.
        var user = Guid.NewGuid();
        var beforeReinstall = HolderRow(user, "device-before-reinstall");

        _tokens.GetByUserAndDeviceAsync(user, "device-after-reinstall", Arg.Any<CancellationToken>())
            .Returns((PushDeviceToken?)null);
        _tokens.GetByFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(beforeReinstall);

        await RegisterAsync(user, "device-after-reinstall");

        _tokens.Received(1).Remove(beforeReinstall);
        await _gapResolver.Received(1).ResolveForUserAsync(user, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_WhenNobodyHoldsTheToken_InsertsAndReportsNoDisplacement()
    {
        var user = Guid.NewGuid();

        _tokens.GetByUserAndDeviceAsync(user, "device-a", Arg.Any<CancellationToken>())
            .Returns((PushDeviceToken?)null);
        _tokens.GetByFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PushDeviceToken?)null);

        var registration = await RegisterAsync(user, "device-a");

        _tokens.DidNotReceive().Remove(Arg.Any<PushDeviceToken>());
        Assert.Null(registration.DisplacedUserId);
        Assert.Equal("ciphertext:" + RawToken, registration.Token.Token);
    }

    [Fact]
    public async Task RegisterAsync_WhenAConcurrentRegistrationWinsTheIndex_RetriesAndClaimsTheirRow()
    {
        // Both installs read no holder, so both insert, and the unique index fails the loser.
        // The winner's row is committed by the time the retry reads again, which is what makes
        // the second pass the ordinary claim path rather than the same race again.
        var arriving = Guid.NewGuid();
        var winner = Guid.NewGuid();
        var winnersRow = HolderRow(winner, "device-of-the-winner");

        _tokens.GetByUserAndDeviceAsync(arriving, "device-b", Arg.Any<CancellationToken>())
            .Returns((PushDeviceToken?)null);
        _tokens.GetByFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PushDeviceToken?)null, winnersRow);
        _unitOfWork.SaveChangesAsync().Returns(
            _ => throw new InvalidOperationException("23505: duplicate key value violates unique constraint"),
            _ => Task.FromResult(1));

        var registration = await RegisterAsync(arriving, "device-b");

        _unitOfWork.Received(1).ClearTracking();
        await _unitOfWork.Received(1).RollbackTransactionAsync();
        _tokens.Received(1).Remove(winnersRow);
        Assert.Equal(winner, registration.DisplacedUserId);
    }

    [Fact]
    public async Task RegisterAsync_WhenTheRetryAlsoFails_Throws()
    {
        // A fault that is not a lost race — the database being down — must still surface. Retrying
        // forever would turn a dead dependency into a hang on a health service's registration path.
        var user = Guid.NewGuid();

        _tokens.GetByUserAndDeviceAsync(user, "device-a", Arg.Any<CancellationToken>())
            .Returns((PushDeviceToken?)null);
        _tokens.GetByFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PushDeviceToken?)null);
        _unitOfWork.SaveChangesAsync().ThrowsAsync(new InvalidOperationException("database is down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RegisterAsync(user, "device-a"));
        await _unitOfWork.Received(2).RollbackTransactionAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
