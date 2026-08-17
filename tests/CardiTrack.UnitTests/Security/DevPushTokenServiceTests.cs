using System.Security.Cryptography;
using System.Text;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Security;

namespace CardiTrack.UnitTests.Security;

/// <summary>
/// The dev test-push endpoint has no authenticated caller behind it — this token is the whole of
/// its authorization (notification_engine.md §13). A token that validates for the wrong user or
/// the wrong category would turn a developer convenience into "send an arbitrary push to anyone",
/// so scope-binding gets the same scrutiny AckTokenServiceTests gives forgery.
/// </summary>
public class DevPushTokenServiceTests
{
    private static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();

    private readonly MovableTimeProvider _time = new(new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero));
    private readonly DevPushTokenService _sut;

    public DevPushTokenServiceTests()
    {
        _sut = new DevPushTokenService(GenerateKey(), _time);
    }

    private DateTime ExpiresIn(TimeSpan window) => _time.GetUtcNow().UtcDateTime + window;

    // ── Key validation (mirrors AckTokenServiceTests) ─────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("REPLACE_ME")]
    public void Ctor_RejectsMissingOrPlaceholderKey(string? key)
    {
        Assert.Throws<ArgumentException>(() => new DevPushTokenService(key!));
    }

    [Fact]
    public void Ctor_RejectsWrongLengthKey()
    {
        Assert.Throws<ArgumentException>(() => new DevPushTokenService(Convert.ToBase64String(new byte[16])));
    }

    [Fact]
    public void Ctor_RejectsNonBase64Key()
    {
        Assert.Throws<ArgumentException>(() => new DevPushTokenService("not base64 at all!!"));
    }

    // ── Happy path ────────────────────────────────────────────────────────────────

    [Fact]
    public void IssuedToken_ValidatesForTheSameRequest()
    {
        var token = _sut.Issue(UserId, DeliveryCategory.Safety, null, ExpiresIn(TimeSpan.FromMinutes(2)));

        Assert.True(_sut.Validate(token, UserId, DeliveryCategory.Safety, null));
    }

    [Fact]
    public void IssuedToken_ValidatesWhenSeverityIsPartOfTheRequest()
    {
        var token = _sut.Issue(UserId, DeliveryCategory.Health, AlertSeverity.Red, ExpiresIn(TimeSpan.FromMinutes(2)));

        Assert.True(_sut.Validate(token, UserId, DeliveryCategory.Health, AlertSeverity.Red));
    }

    // ── Scope binding: the token authorizes one push, not a capability ────────────

    [Fact]
    public void Token_DoesNotValidateForADifferentUser()
    {
        var token = _sut.Issue(UserId, DeliveryCategory.Safety, null, ExpiresIn(TimeSpan.FromMinutes(2)));

        Assert.False(_sut.Validate(token, OtherUserId, DeliveryCategory.Safety, null));
    }

    [Fact]
    public void Token_DoesNotValidateForADifferentCategory()
    {
        // The escalation that matters: Nudge is harmless, Safety overrides quiet hours and pages.
        var token = _sut.Issue(UserId, DeliveryCategory.Nudge, null, ExpiresIn(TimeSpan.FromMinutes(2)));

        Assert.False(_sut.Validate(token, UserId, DeliveryCategory.Safety, null));
    }

    [Fact]
    public void Token_DoesNotValidateForADifferentSeverity()
    {
        var token = _sut.Issue(UserId, DeliveryCategory.Health, AlertSeverity.Orange, ExpiresIn(TimeSpan.FromMinutes(2)));

        Assert.False(_sut.Validate(token, UserId, DeliveryCategory.Health, AlertSeverity.Red));
    }

    [Fact]
    public void Token_DistinguishesAbsentSeverityFromAPresentOne()
    {
        var token = _sut.Issue(UserId, DeliveryCategory.Health, null, ExpiresIn(TimeSpan.FromMinutes(2)));

        Assert.False(_sut.Validate(token, UserId, DeliveryCategory.Health, AlertSeverity.Red));
        Assert.True(_sut.Validate(token, UserId, DeliveryCategory.Health, null));
    }

    [Fact]
    public void TokenFromADifferentKey_DoesNotValidate()
    {
        var other = new DevPushTokenService(GenerateKey(), _time);
        var token = other.Issue(UserId, DeliveryCategory.Safety, null, ExpiresIn(TimeSpan.FromMinutes(2)));

        Assert.False(_sut.Validate(token, UserId, DeliveryCategory.Safety, null));
    }

    // ── Expiry, both ends ─────────────────────────────────────────────────────────

    [Fact]
    public void ExpiredToken_DoesNotValidate()
    {
        var token = _sut.Issue(UserId, DeliveryCategory.Safety, null, ExpiresIn(TimeSpan.FromMinutes(2)));

        _time.Advance(TimeSpan.FromMinutes(3));

        Assert.False(_sut.Validate(token, UserId, DeliveryCategory.Safety, null));
    }

    [Fact]
    public void Issue_RejectsAnExpiryBeyondTheMaximumLifetime()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _sut.Issue(UserId, DeliveryCategory.Safety, null, ExpiresIn(DevPushTokenService.MaxLifetime + TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public void Validate_RejectsAnExpiryBeyondTheMaximumLifetime()
    {
        // Issue() is not the only way a token gets built — scripts/dev-push.ps1 computes one from
        // the same key, so anything holding the key can mint a year-long expiry. The upper bound
        // has to be enforced on the way in, not only on the way out.
        var key = GenerateKey();
        var service = new DevPushTokenService(key, _time);
        var farFuture = _time.GetUtcNow().ToUnixTimeSeconds() + (long)TimeSpan.FromDays(365).TotalSeconds;

        var token = Mint(key, UserId, DeliveryCategory.Safety, null, farFuture);

        Assert.False(service.Validate(token, UserId, DeliveryCategory.Safety, null));
    }

    // ── Malformed input ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nodot")]
    [InlineData("not-a-number.abc")]
    [InlineData("1755421200.!!!not-base64!!!")]
    [InlineData("1755421200.aaaa.bbbb")]
    public void MalformedToken_DoesNotValidate(string token)
    {
        Assert.False(_sut.Validate(token, UserId, DeliveryCategory.Safety, null));
    }

    [Fact]
    public void TamperedMac_DoesNotValidate()
    {
        var token = _sut.Issue(UserId, DeliveryCategory.Safety, null, ExpiresIn(TimeSpan.FromMinutes(2)));
        var parts = token.Split('.');
        var flipped = parts[1][0] == 'A' ? 'B' : 'A';

        Assert.False(_sut.Validate($"{parts[0]}.{flipped}{parts[1][1..]}", UserId, DeliveryCategory.Safety, null));
    }

    [Fact]
    public void TamperedExpiry_DoesNotValidate()
    {
        var token = _sut.Issue(UserId, DeliveryCategory.Safety, null, ExpiresIn(TimeSpan.FromMinutes(2)));
        var parts = token.Split('.');
        var extended = long.Parse(parts[0]) + 60;

        Assert.False(_sut.Validate($"{extended}.{parts[1]}", UserId, DeliveryCategory.Safety, null));
    }

    // ── The documented wire format ────────────────────────────────────────────────

    [Fact]
    public void IndependentlyComputedToken_Validates()
    {
        // scripts/dev-push.ps1 mints tokens from this format without going through the service.
        // Recomputing it here from the spec — rather than round-tripping Issue() into Validate(),
        // which would pass no matter what the format drifted to — is what keeps the script working.
        var key = GenerateKey();
        var service = new DevPushTokenService(key, _time);
        var expiresAt = _time.GetUtcNow().AddMinutes(2).ToUnixTimeSeconds();

        var token = Mint(key, UserId, DeliveryCategory.Health, AlertSeverity.Red, expiresAt);

        Assert.True(service.Validate(token, UserId, DeliveryCategory.Health, AlertSeverity.Red));
    }

    /// <summary>
    /// The token format as documented on <see cref="DevPushTokenService"/> and reimplemented in
    /// <c>scripts/dev-push.ps1</c>: <c>{expiresAtUnixSeconds}.{base64url(HMAC-SHA256(key,
    /// "devpush|{userId:N}|{category}|{severity or -}|{expiresAtUnixSeconds}"))}</c>.
    /// </summary>
    private static string Mint(
        string base64Key, Guid userId, DeliveryCategory category, AlertSeverity? severity, long expiresAtUnixSeconds)
    {
        var payload = $"devpush|{userId:N}|{category}|{severity?.ToString() ?? "-"}|{expiresAtUnixSeconds}";
        var mac = HMACSHA256.HashData(Convert.FromBase64String(base64Key), Encoding.UTF8.GetBytes(payload));
        var base64Url = Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{expiresAtUnixSeconds}.{base64Url}";
    }

    /// <summary>Mirrors <c>DispatchServiceTests.FixedTimeProvider</c>, but able to move forward so expiry can be crossed.</summary>
    private sealed class MovableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
