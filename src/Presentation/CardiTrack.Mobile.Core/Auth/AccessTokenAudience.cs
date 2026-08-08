using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace CardiTrack.Mobile.Core.Auth;

/// <summary>Outcome of comparing an access token's <c>aud</c> claim to the configured API audience.</summary>
public enum AudienceCheck
{
    /// <summary>The token names the configured audience — API calls will authorize.</summary>
    Match,

    /// <summary>
    /// The token decodes but doesn't name the configured audience — either it names another
    /// one, or it carries no usable <c>aud</c> at all. Every API call will 401 regardless.
    /// </summary>
    Mismatch,

    /// <summary>Not a readable JWT: Auth0 issues an opaque /userinfo token when no API audience was requested.</summary>
    Unreadable,
}

/// <summary>
/// Reads the <c>aud</c> claim of an access token so a build stamped with the wrong
/// Auth0 audience can be named in the log instead of showing up as an endless 401 loop.
/// Diagnostics only — nothing here authorizes anything, and the token is never validated.
/// </summary>
public static class AccessTokenAudience
{
    /// <summary>
    /// The token's audiences. <c>aud</c> is a single string or an array of them (RFC 7519),
    /// so both shapes are flattened here; an opaque or malformed token yields none.
    /// </summary>
    public static IReadOnlyList<string> Read(string? accessToken) =>
        JwtPayloadReader.ReadPayload(accessToken) is { } payload ? ReadFrom(payload) : [];

    /// <remarks>
    /// Keyed off whether the payload decoded, not off how many audiences came back: a readable
    /// JWT that simply carries no usable <c>aud</c> is a mismatch, not an unreadable token, and
    /// calling it unreadable would name the wrong fault in the one log line that exists to
    /// name the right one.
    /// </remarks>
    public static AudienceCheck Check(string? accessToken, string expectedAudience)
    {
        if (JwtPayloadReader.ReadPayload(accessToken) is not { } payload)
            return AudienceCheck.Unreadable;

        return ReadFrom(payload).Contains(expectedAudience, StringComparer.Ordinal)
            ? AudienceCheck.Match
            : AudienceCheck.Mismatch;
    }

    private static IReadOnlyList<string> ReadFrom(JObject payload) => payload["aud"] switch
    {
        JValue { Type: JTokenType.String } single => [(string)single!],
        JArray many => [.. many
            .OfType<JValue>()
            .Where(value => value.Type == JTokenType.String)
            .Select(value => (string)value!)],
        _ => [],
    };

    /// <summary>The audiences as a log-safe string. Auth0 audiences are API identifiers, not secrets.</summary>
    public static string Describe(string? accessToken)
    {
        var audiences = Read(accessToken);
        return audiences.Count == 0 ? "(none)" : string.Join(", ", audiences);
    }

    /// <summary>
    /// Names the audience a token was actually issued for when it isn't the one this build
    /// targets. A wrong or missing <c>Auth0Audience</c> is a build/tenant fault the app can't
    /// correct at runtime; without this it surfaces only as every API call returning 401.
    /// </summary>
    public static void Warn(ILogger logger, string? accessToken, string expectedAudience, string operation)
    {
        switch (Check(accessToken, expectedAudience))
        {
            case AudienceCheck.Mismatch:
                logger.LogError(
                    "Access token from {Operation} is for audience {ActualAudience}, not the configured {ExpectedAudience} — API calls will 401.",
                    operation, Describe(accessToken), expectedAudience);
                break;
            case AudienceCheck.Unreadable:
                logger.LogWarning(
                    "Access token from {Operation} is not a readable JWT — Auth0 issues an opaque token when no API audience is requested, and the API will reject it.",
                    operation);
                break;
        }
    }
}
