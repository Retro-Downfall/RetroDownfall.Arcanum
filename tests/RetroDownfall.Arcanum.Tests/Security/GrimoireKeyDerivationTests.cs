using System.Security.Cryptography;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class GrimoireKeyDerivationTests
{

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
