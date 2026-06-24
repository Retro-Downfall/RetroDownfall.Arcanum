using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public static class GrimoireKeyDerivation
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly byte[] Salt = Utf8NoBom.GetBytes("Arcanum.Grimoire.SQLCipher.salt.v1");
    private static readonly byte[] LegacyInfo = Utf8NoBom.GetBytes("Arcanum.Grimoire.SQLCipher.hkdf.v1");

    private static readonly byte[] DedicatedInfo = Utf8NoBom.GetBytes("Arcanum.Grimoire.SQLCipher.hkdf.v2");

    public static string DerivePassphraseFromApiKey(string apiKey)
    {

        return DerivePassphrase(apiKey, LegacyInfo);

    }

    public static string DerivePassphraseFromEncryptionSecret(string encryptionSecret)
    {

        return DerivePassphrase(encryptionSecret, DedicatedInfo);

    }

    private static string DerivePassphrase(string keyMaterial, byte[] info)
    {

        if (string.IsNullOrEmpty(keyMaterial))
        {

            throw new ArgumentException("Key material is required to derive the Grimoire passphrase.", nameof(keyMaterial));

        }

        byte[] ikm = Utf8NoBom.GetBytes(keyMaterial);

        byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, outputLength: 32, Salt, info);

        CryptographicOperations.ZeroMemory(ikm);

        string passphrase = Convert.ToBase64String(okm);

        CryptographicOperations.ZeroMemory(okm);

        return passphrase;

    }
}
