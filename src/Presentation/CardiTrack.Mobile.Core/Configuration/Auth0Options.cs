namespace CardiTrack.Mobile.Core.Configuration;

public sealed record Auth0Options(string Domain, string ClientId, string Audience)
{
    public const string DbConnection = "Username-Password-Authentication";

    public bool IsConfigured => IsSet(Domain) && IsSet(ClientId) && IsSet(Audience);

    // "REPLACE_ME" is the Terraform placeholder in Secret Manager — a build stamped
    // before the operator sets real values must fail as NotConfigured, not with a
    // baffling DNS error against https://REPLACE_ME.
    private static bool IsSet(string value) =>
        !string.IsNullOrWhiteSpace(value) && value != "REPLACE_ME";
}
