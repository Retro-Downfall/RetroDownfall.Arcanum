using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public static class GrimoireKeyDerivation
{

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly byte[] LegacySalt = Utf8NoBom.GetBytes("Arcanum.Grimoire.SQLCipher.salt.v1");

    private static readonly byte[] LegacyInfo = Utf8NoBom.GetBytes("Arcanum.Grimoire.SQLCipher.hkdf.v1");

    private static readonly byte[] DedicatedInfo = Utf8NoBom.GetBytes("Arcanum.Grimoire.SQLCipher.hkdf.v2");

    public const int SaltLengthBytes = 16;

    public const int IterationCount = 600_000;

    public const int KdfVersion1 = 1;

    public const int KdfVersion2 = 2;

    public static string DerivePassphraseFromApiKeyLegacy(string apiKey)
    {

        return DerivePassphrase(apiKey, LegacyInfo);

    }

    public static string DerivePassphraseFromEncryptionSecretLegacy(string encryptionSecret)
    {

        return DerivePassphrase(encryptionSecret, DedicatedInfo);

    }

    public static string DerivePassphraseFromApiKey(string apiKey, ReadOnlySpan<byte> salt)
    {

        return DerivePbkdf2Passphrase(apiKey, salt);

    }

    public static string DerivePassphraseFromEncryptionSecret(string encryptionSecret, ReadOnlySpan<byte> salt)
    {

        return DerivePbkdf2Passphrase(encryptionSecret, salt);

    }

    private static string DerivePassphrase(string keyMaterial, byte[] info)
    {

        if (string.IsNullOrEmpty(keyMaterial))
        {

            throw new ArgumentException("Key material is required to derive the Grimoire passphrase.", nameof(keyMaterial));

        }

        byte[] ikm = Utf8NoBom.GetBytes(keyMaterial);

        byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, outputLength: 32, LegacySalt, info);

        CryptographicOperations.ZeroMemory(ikm);

        string passphrase = Convert.ToBase64String(okm);

        CryptographicOperations.ZeroMemory(okm);

        return passphrase;

    }

    private static string DerivePbkdf2Passphrase(string keyMaterial, ReadOnlySpan<byte> salt)
    {

        if (string.IsNullOrEmpty(keyMaterial))
        {

            throw new ArgumentException("Key material is required to derive the Grimoire passphrase.", nameof(keyMaterial));

        }

        if (salt.Length != SaltLengthBytes)
        {

            throw new ArgumentException($"Salt must be {SaltLengthBytes} bytes.", nameof(salt));

        }

        byte[] ikm = Utf8NoBom.GetBytes(keyMaterial);

        byte[] okm = Rfc2898DeriveBytes.Pbkdf2(
            ikm,
            salt,
            iterations: IterationCount,
            HashAlgorithmName.SHA256,
            outputLength: 32);

        CryptographicOperations.ZeroMemory(ikm);

        string passphrase = Convert.ToBase64String(okm);

        CryptographicOperations.ZeroMemory(okm);

        return passphrase;

    }

}
