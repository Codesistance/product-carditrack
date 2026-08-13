using System.Security.Cryptography;
using CardiTrack.Application.Interfaces.Security;

namespace CardiTrack.Infrastructure.Security;

/// <summary>
/// Reads a service-encrypted column back to plain text, tolerating rows written before the column
/// was encrypted.
/// </summary>
/// <remarks>
/// <para>
/// Encryption on this platform is applied by the service that owns the field rather than by an EF
/// value converter (docs/technical/data_protection_architecture.md), which means every reader of an
/// encrypted column has to decrypt it. That document names the hazard exactly: "a value converter
/// is still the more durable fix if a second reader is ever added". The prompt-context sources are
/// that second reader, and they arrived to find the first one's fallback logic private to
/// <c>CardiMemberService</c> in the Application layer — which Infrastructure cannot reference.
/// </para>
/// <para>
/// So the ten lines are restated here rather than shared, and the two copies are cross-referenced.
/// If a third reader appears, that is the signal to move the whole concern to a value converter
/// instead of copying this a third time.
/// </para>
/// </remarks>
internal static class EncryptedFieldReader
{
    /// <summary>
    /// The decrypted value, or the stored value when it was never encrypted.
    /// </summary>
    /// <remarks>
    /// Values written before encryption are still in the database as plain text, and AES-GCM's
    /// authentication tag makes those indistinguishable from corruption. Falling back to the stored
    /// value means a legacy row reads back as what was typed. Mirrors
    /// <c>CardiMemberService.Reveal</c>.
    /// </remarks>
    internal static string? Reveal(IEncryptionService encryption, string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return null;

        try
        {
            return encryption.Decrypt(stored);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            return stored;
        }
    }
}
