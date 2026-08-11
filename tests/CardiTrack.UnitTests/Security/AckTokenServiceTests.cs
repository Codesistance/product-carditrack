using System.Security.Cryptography;
using CardiTrack.Infrastructure.Security;

namespace CardiTrack.UnitTests.Security;

/// <summary>
/// notification_engine.md §15: "the test that matters most in the suite" — a forged, expired,
/// replayed, or other-device ack token must be rejected, and rejection must never itself be
/// mistaken for a stop-escalation signal by anything downstream (that's the caller's job,
/// verified in AckDeliveryService/NotificationsController tests, but this class is where a
/// wrong verdict would originate).
/// </summary>
public class AckTokenServiceTests
{
    private static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly AckTokenService _sut = new(GenerateKey());

    private static readonly Guid DeliveryId = Guid.NewGuid();
    private static readonly Guid PushDeviceTokenId = Guid.NewGuid();
    private static readonly Guid OtherDeviceTokenId = Guid.NewGuid();
    private static readonly DateTime ExpiresAt = DateTime.UtcNow.AddMinutes(30);

    // ── Key validation (mirrors AesEncryptionServiceTests) ────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("REPLACE_ME")]
    public void Ctor_RejectsMissingOrPlaceholderKey(string? key)
    {
        Assert.Throws<ArgumentException>(() => new AckTokenService(key!));
    }

    [Fact]
    public void Ctor_RejectsWrongLengthKey()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);
        Assert.Throws<ArgumentException>(() => new AckTokenService(shortKey));
    }

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public void IssuedAckToken_ValidatesForTheSameDeliveryAndReturnsTheDeviceId()
    {
        var token = _sut.IssueAckToken(DeliveryId, PushDeviceTokenId, ExpiresAt);

        var valid = _sut.ValidateAckToken(token, DeliveryId, out var resolvedTokenId);

        Assert.True(valid);
        Assert.Equal(PushDeviceTokenId, resolvedTokenId);
    }

    [Fact]
    public void FetchToken_NeverValidatesAsAnAckToken()
    {
        // §7.2 C5 — "useless for any other call". Domain separation between the two token
        // kinds, not just a naming difference.
        var fetchToken = _sut.IssueFetchToken(DeliveryId, PushDeviceTokenId, ExpiresAt);

        Assert.False(_sut.ValidateAckToken(fetchToken, DeliveryId, out _));
    }

    [Fact]
    public void AckToken_NeverValidatesAsAFetchToken()
    {
        var ackToken = _sut.IssueAckToken(DeliveryId, PushDeviceTokenId, ExpiresAt);

        Assert.False(_sut.ValidateFetchToken(ackToken, DeliveryId, out _));
    }

    // ── Forged / tampered ──────────────────────────────────────────────────────

    [Fact]
    public void ForgedToken_IsRejected()
    {
        Assert.False(_sut.ValidateAckToken("not-a-real-token", DeliveryId, out _));
    }

    [Fact]
    public void TokenSignedByADifferentKey_IsRejected()
    {
        var otherService = new AckTokenService(GenerateKey());
        var token = otherService.IssueAckToken(DeliveryId, PushDeviceTokenId, ExpiresAt);

        Assert.False(_sut.ValidateAckToken(token, DeliveryId, out _));
    }

    [Fact]
    public void TamperedDeviceTokenId_IsRejected()
    {
        // Attacker takes a valid token and edits the embedded device id in the clear — the HMAC
        // was computed over the original value, so the recomputed MAC no longer matches.
        var token = _sut.IssueAckToken(DeliveryId, PushDeviceTokenId, ExpiresAt);
        var parts = token.Split('.');
        var tampered = $"{OtherDeviceTokenId:N}.{parts[1]}.{parts[2]}";

        Assert.False(_sut.ValidateAckToken(tampered, DeliveryId, out _));
    }

    // ── Expired ────────────────────────────────────────────────────────────────

    [Fact]
    public void ExpiredToken_IsRejected()
    {
        var token = _sut.IssueAckToken(DeliveryId, PushDeviceTokenId, DateTime.UtcNow.AddMinutes(-1));

        Assert.False(_sut.ValidateAckToken(token, DeliveryId, out _));
    }

    // ── Replayed against the wrong delivery ("other-device" per §15) ─────────────

    [Fact]
    public void TokenIssuedForOneDelivery_IsRejectedAgainstAnother()
    {
        var token = _sut.IssueAckToken(DeliveryId, PushDeviceTokenId, ExpiresAt);
        var otherDeliveryId = Guid.NewGuid();

        Assert.False(_sut.ValidateAckToken(token, otherDeliveryId, out _));
    }
}
