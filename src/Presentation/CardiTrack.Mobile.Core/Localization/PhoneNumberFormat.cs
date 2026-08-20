using System.Text.RegularExpressions;

namespace CardiTrack.Mobile.Core.Localization;

/// <summary>
/// The E.164-style phone rule the API applies, so a mobile form rejects exactly what the server
/// would reject rather than shipping the caregiver's typing off to be refused.
/// </summary>
/// <remarks>
/// Lives beside <see cref="PhonePlaceholder"/>, whose examples exist to satisfy this same pattern:
/// the placeholder and the rule it is an example of drifting apart is the failure worth designing
/// against, and they cannot drift while they sit in one file's worth of the same subject.
/// </remarks>
public static partial class PhoneNumberFormat
{
    /// <summary>The message shown beneath a field this rule rejected. Regional examples come from
    /// <see cref="PhonePlaceholder"/>; this one is the shape, which is the same everywhere.</summary>
    public const string ValidationMessage = "Enter a valid phone number, e.g. +15551234567";

    /// <summary>
    /// Whether <paramref name="value"/> is a number the API will accept. Blank is valid: every
    /// phone field CardiTrack asks for is optional, and "not given" is not "malformed".
    /// </summary>
    public static bool IsValid(string? value) =>
        string.IsNullOrWhiteSpace(value) || Pattern().IsMatch(value.Trim());

    [GeneratedRegex(@"^\+?[1-9]\d{1,14}$")]
    private static partial Regex Pattern();
}
