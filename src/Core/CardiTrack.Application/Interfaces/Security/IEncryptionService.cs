namespace CardiTrack.Application.Interfaces.Security;

/// <summary>
/// Symmetric encryption for data that must not sit in the database in the clear —
/// OAuth tokens and CardiMember medical notes. Implemented in Infrastructure; declared
/// here so Application services can depend on it, as they do for repositories.
/// </summary>
public interface IEncryptionService
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
    byte[] EncryptBytes(byte[] plainBytes);
    byte[] DecryptBytes(byte[] cipherBytes);
}
