namespace AssetTracker.Core.Abstractions;

/// <summary>
/// Encrypts secrets stored in the database (e.g. the SMTP password setting). Production uses ASP.NET Core Data
/// Protection with a key ring shared by the web app and the worker (see <c>deploy/README.md</c>).
/// </summary>
public interface ISecretProtector
{
    /// <summary>Returns ciphertext safe to store as text.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts ciphertext produced by <see cref="Protect"/>.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The ciphertext is corrupt or the key that encrypted it is not available (e.g. the key folder was lost).
    /// </exception>
    string Unprotect(string ciphertext);
}
