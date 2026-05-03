using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public static class GrimoireKeyDerivation
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly byte[] Salt = Utf8NoBom.GetBytes("Arcanum.Grimoire.SQLCipher.salt.v1");
    private static readonly byte[] Info = Utf8NoBom.GetBytes("Arcanum.Grimoire.SQLCipher.hkdf.v1");
    public static string DerivePassphraseFromApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new ArgumentException("API key is required to derive the Grimoire passphrase.", nameof(apiKey));
        }

        byte[] ikm = Utf8NoBom.GetBytes(apiKey);
        byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, outputLength: 32, Salt, Info);
        return Convert.ToBase64String(okm);
    }
}
