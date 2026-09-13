using System.Security.Cryptography;
using System.Text;

namespace CardiTrack.Mobile.Core.Auth;

/// <summary>
/// The per-caregiver key under which the device remembers that the account confirmed the
/// health-data disclosure was dismissed. The account is the record; the device keeps only a hint
/// so an acknowledged caregiver is not re-shown the banner while offline — and a hint that was
/// not tied to the caregiver would let one person's acknowledgement silence the notice for the
/// next person to sign in on the same phone, including after a session expires without the
/// Settings sign-out ever running.
/// </summary>
public static class HealthDataDisclosureScope
{
    /// <summary>
    /// A stable, non-reversible token for the signed-in caregiver's email, or null when there is
    /// no signed-in identity to scope to (in which case nothing may be remembered).
    /// </summary>
    public static string? For(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var normalised = email.Trim().ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }
}
