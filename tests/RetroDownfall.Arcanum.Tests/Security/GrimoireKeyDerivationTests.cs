using System.Security.Cryptography;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class GrimoireKeyDerivationTests
{
    /// <summary>
    /// Known-answer key material. The expected passphrases below were computed once, outside this
    /// repository, by independent implementations — Python <c>hashlib.pbkdf2_hmac</c> (cross-checked
    /// with <c>openssl kdf PBKDF2</c>) and RFC 5869 HKDF over Python <c>hmac</c>/<c>hashlib</c>
    /// (cross-checked with the <c>cryptography</c> package) — and are hard-coded on purpose: every
    /// existing user database was keyed through these exact functions, so any change to the
    /// iteration count, hash, salt/info strings, output length, or text encoding must turn these red.
    /// </summary>
    private const string KnownAnswerSecret = "arcanum-known-answer-secret";

    /// <summary>PBKDF2-HMAC-SHA256(KnownAnswerSecret, 00 01 .. 0f, 600,000, 32) as base64.</summary>
    private const string Pbkdf2KnownAnswer = "uHUfE4FmC6CJy8/ecI5okBh8CnlDyAFZDonKODd0h6Y=";

    /// <summary>
    /// HKDF-SHA256(KnownAnswerSecret, salt "Arcanum.Grimoire.SQLCipher.salt.v1",
    /// info "Arcanum.Grimoire.SQLCipher.hkdf.v1", 32) as base64.
    /// </summary>
    private const string LegacyApiKeyKnownAnswer = "oTC8bURfJjesJxBDnwJSUzIwluhaQOUTO+rb3dMla8M=";

    /// <summary>
    /// HKDF-SHA256(KnownAnswerSecret, salt "Arcanum.Grimoire.SQLCipher.salt.v1",
    /// info "Arcanum.Grimoire.SQLCipher.hkdf.v2", 32) as base64.
    /// </summary>
    private const string LegacyDedicatedSecretKnownAnswer = "0eLTl4dg3If6PUx+fG7MYhWChUiZjERYupdapBvCB6U=";

    private static byte[] KnownAnswerSalt() =>
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f];

    [Fact]
    public void Pbkdf2_matches_the_published_vector()
    {
        Assert.Equal(600_000, GrimoireKeyDerivation.IterationCount);

        Assert.Equal(
            Pbkdf2KnownAnswer,
            GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(KnownAnswerSecret, KnownAnswerSalt()));

        Assert.Equal(
            Pbkdf2KnownAnswer,
            GrimoireKeyDerivation.DerivePassphraseFromApiKey(KnownAnswerSecret, KnownAnswerSalt()));
    }

    [Fact]
    public void Legacy_hkdf_variants_match_published_vectors()
    {
        Assert.Equal(
            LegacyApiKeyKnownAnswer,
            GrimoireKeyDerivation.DerivePassphraseFromApiKeyLegacy(KnownAnswerSecret));

        Assert.Equal(
            LegacyDedicatedSecretKnownAnswer,
            GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecretLegacy(KnownAnswerSecret));
    }

    private static byte[] TestSalt()
    {
        byte[] salt = new byte[GrimoireKeyDerivation.SaltLengthBytes];

        RandomNumberGenerator.Fill(salt);

        return salt;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DerivePassphraseFromApiKeyLegacy_EmptyMaterial_ThrowsArgumentException(string? keyMaterial)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => GrimoireKeyDerivation.DerivePassphraseFromApiKeyLegacy(keyMaterial!));

        Assert.Equal("keyMaterial", exception.ParamName);

        Assert.Contains("Key material is required", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DerivePassphraseFromEncryptionSecretLegacy_EmptyMaterial_ThrowsArgumentException(string? keyMaterial)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecretLegacy(keyMaterial!));

        Assert.Equal("keyMaterial", exception.ParamName);

        Assert.Contains("Key material is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DerivePassphraseFromApiKey_ProducesDeterministicBase64Passphrase()
    {
        byte[] salt = TestSalt();

        string first = GrimoireKeyDerivation.DerivePassphraseFromApiKey("test-api-key-material", salt);

        string second = GrimoireKeyDerivation.DerivePassphraseFromApiKey("test-api-key-material", salt);

        Assert.Equal(first, second);

        Assert.NotEmpty(first);

        Assert.Equal(44, first.Length);
    }

    [Fact]
    public void DerivePassphraseFromEncryptionSecret_UsesDifferentSaltProducesDifferentPassphrase()
    {
        string sharedMaterial = "shared-key-material-value";

        byte[] saltA = TestSalt();

        byte[] saltB = TestSalt();

        string fromSaltA = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(sharedMaterial, saltA);

        string fromSaltB = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(sharedMaterial, saltB);

        Assert.NotEqual(fromSaltA, fromSaltB);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DerivePassphraseFromApiKey_EmptyMaterial_ThrowsArgumentException(string? keyMaterial)
    {
        byte[] salt = TestSalt();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => GrimoireKeyDerivation.DerivePassphraseFromApiKey(keyMaterial!, salt));

        Assert.Equal("keyMaterial", exception.ParamName);

        Assert.Contains("Key material is required", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DerivePassphraseFromEncryptionSecret_EmptyMaterial_ThrowsArgumentException(string? keyMaterial)
    {
        byte[] salt = TestSalt();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(keyMaterial!, salt));

        Assert.Equal("keyMaterial", exception.ParamName);
    }

    [Fact]
    public void DerivePassphraseFromApiKey_DifferentInputs_ProduceDifferentPassphrases()
    {
        byte[] salt = TestSalt();

        string alpha = GrimoireKeyDerivation.DerivePassphraseFromApiKey("alpha-key", salt);

        string beta = GrimoireKeyDerivation.DerivePassphraseFromApiKey("beta-key", salt);

        Assert.NotEqual(alpha, beta);
    }

    [Fact]
    public void DerivePassphraseFromApiKey_WrongSaltLength_ThrowsArgumentException()
    {
        byte[] shortSalt = new byte[8];

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => GrimoireKeyDerivation.DerivePassphraseFromApiKey("material", shortSalt));

        Assert.Equal("salt", exception.ParamName);
    }

    [Fact]
    public void LegacyAndPbkdf2_DeriveDifferentPassphrases()
    {
        byte[] salt = TestSalt();

        string legacy = GrimoireKeyDerivation.DerivePassphraseFromApiKeyLegacy("test-material");

        string modern = GrimoireKeyDerivation.DerivePassphraseFromApiKey("test-material", salt);

        Assert.NotEqual(legacy, modern);
    }
}
