using System.Security.Cryptography;

namespace LanDisk.Update;

/// <summary>
/// The one public key LanDisk trusts for update packages.
///
/// This is a public key: it is safe to ship and safe to read. The matching private key is held
/// offline by the release process (see <c>tools/New-SigningKey.ps1</c>) and is deliberately absent
/// from this repository, from every build output and from every delivery bundle. A package that
/// does not verify against this exact key is refused before a single byte is written to disk.
/// </summary>
public static class ReleasePublicKey
{
    /// <summary>Short fingerprint of the subject public key info; shown in the UI as the signer id.</summary>
    public const string KeyId = "ac3ff1673b23518a";

    /// <summary>Base64 SubjectPublicKeyInfo (ECDSA P-256) of the LanDisk release signing key.</summary>
    public const string SubjectPublicKeyInfoBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEgB/vgYhkfDXuqVtw24W0K+U/E0+dZ3+j4y14Unt9NSstPAkrOPRqZ+0/IXtO9JQ/tmBep0Hyj5iVMNUWQE0rYg==";

    public static ECDsa Create()
        => UpdateSigning.LoadPublicKeyFromBase64(SubjectPublicKeyInfoBase64);

    /// <summary>
    /// Verifies that the compiled-in constant really is a usable key and that its fingerprint
    /// matches <see cref="KeyId"/>. Called at startup so a corrupted build fails loudly instead of
    /// silently rejecting every future update.
    /// </summary>
    public static bool SelfCheck(out string keyId)
    {
        try
        {
            using ECDsa key = Create();
            keyId = UpdateSigning.KeyId(key);
            return string.Equals(keyId, KeyId, StringComparison.Ordinal);
        }
        catch
        {
            keyId = string.Empty;
            return false;
        }
    }
}
