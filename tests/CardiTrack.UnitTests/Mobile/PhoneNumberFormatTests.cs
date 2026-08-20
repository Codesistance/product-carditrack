using CardiTrack.Mobile.Core.Localization;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The rule the M1-13 contact form and the M1-14 edit form both apply before they send. It exists
/// to agree with <c>UpdateCardiMemberValidator</c>'s own <c>^\+?[1-9]\d{1,14}$</c>, so what these
/// cases really pin is that agreement: a number this accepts and the API refuses is a caregiver
/// watching a save fail for no reason they can see.
/// </summary>
public class PhoneNumberFormatTests
{
    [Theory]
    [InlineData("+15551234567")]
    [InlineData("15551234567")]
    [InlineData("+447700900000")]
    [InlineData("12")]
    [InlineData("+123456789012345")]
    public void IsValid_E164ShapedNumber_IsAccepted(string value)
    {
        Assert.True(PhoneNumberFormat.IsValid(value));
    }

    // Whitespace is trimmed rather than rejected: the forms trim what they send, so a number that
    // arrives with a stray space is one the API will accept and this must not refuse first.
    [Theory]
    [InlineData("  +15551234567  ")]
    [InlineData("\t15551234567")]
    public void IsValid_SurroundingWhitespace_IsTrimmedBeforeMatching(string value)
    {
        Assert.True(PhoneNumberFormat.IsValid(value));
    }

    [Theory]
    [InlineData("+1 555 123 4567")]  // Spaced, the way a caregiver would write it down.
    [InlineData("(555) 123-4567")]
    [InlineData("+0155512345")]      // Leading zero after the plus.
    [InlineData("0155512345")]
    [InlineData("1")]                // Two digits is the floor.
    [InlineData("+1234567890123456")]  // Sixteen digits is one past the ceiling.
    [InlineData("+1555123456a")]
    public void IsValid_AnythingTheApiWouldRefuse_IsRefusedHereFirst(string value)
    {
        Assert.False(PhoneNumberFormat.IsValid(value));
    }

    // Every phone field CardiTrack asks for is optional, and clearing one is how a record is
    // removed — "not given" must not surface as "malformed" under a field the caregiver emptied
    // on purpose.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValid_NotGiven_IsNotAValidationFailure(string? value)
    {
        Assert.True(PhoneNumberFormat.IsValid(value));
    }
}
