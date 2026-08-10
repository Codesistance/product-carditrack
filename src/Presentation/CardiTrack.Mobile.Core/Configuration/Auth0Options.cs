namespace CardiTrack.Mobile.Core.Configuration;

public sealed record Auth0Options(string Domain, string ClientId, string Audience)
{
    public const string DbConnection = "Username-Password-Authentication";

    // Auth0-fixed connection names — not tenant configuration, so not build-stamped.
    public const string GoogleConnection = "google-oauth2";
    public const string AppleConnection = "apple";

    /// <summary>Same scheme/host the Fitbit device flow registered on both platforms
    /// (Android WebAuthenticationCallbackActivity, iOS Info.plist CFBundleURLSchemes).</summary>
    public const string CallbackUri = "carditrack://oauth/callback";

    public bool IsConfigured => IsSet(Domain) && IsSet(ClientId) && IsSet(Audience);

    // "REPLACE_ME" is the Terraform placeholder in Secret Manager — a build stamped
    // before the operator sets real values must fail as NotConfigured, not with a
    // baffling DNS error against https://REPLACE_ME.
    private static bool IsSet(string value) =>
        !string.IsNullOrWhiteSpace(value) && value != "REPLACE_ME";
}
