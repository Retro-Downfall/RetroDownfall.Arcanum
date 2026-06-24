using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class GrimoireKeyDerivationTests
{

    [Fact]
    public void DerivePassphraseFromApiKey_ProducesDeterministicBase64Passphrase()
    {

        string first = GrimoireKeyDerivation.DerivePassphraseFromApiKey("test-api-key-material");

        string second = GrimoireKeyDerivation.DerivePassphraseFromApiKey("test-api-key-material");

        Assert.Equal(first, second);

        Assert.NotEmpty(first);

        Assert.Equal(44, first.Length);

    }

    [Fact]
    public void DerivePassphraseFromEncryptionSecret_UsesDifferentInfoThanApiKey()
    {

        string sharedMaterial = "shared-key-material-value";

        string fromApiKey = GrimoireKeyDerivation.DerivePassphraseFromApiKey(sharedMaterial);

        string fromEncryptionSecret = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(sharedMaterial);

        Assert.NotEqual(fromApiKey, fromEncryptionSecret);

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DerivePassphraseFromApiKey_EmptyMaterial_ThrowsArgumentException(string? keyMaterial)
    {

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => GrimoireKeyDerivation.DerivePassphraseFromApiKey(keyMaterial!));

        Assert.Equal("keyMaterial", exception.ParamName);

        Assert.Contains("Key material is required", exception.Message, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DerivePassphraseFromEncryptionSecret_EmptyMaterial_ThrowsArgumentException(string? keyMaterial)
    {

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(keyMaterial!));

        Assert.Equal("keyMaterial", exception.ParamName);

    }

    [Fact]
    public void DerivePassphraseFromApiKey_DifferentInputs_ProduceDifferentPassphrases()
    {

        string alpha = GrimoireKeyDerivation.DerivePassphraseFromApiKey("alpha-key");

        string beta = GrimoireKeyDerivation.DerivePassphraseFromApiKey("beta-key");

        Assert.NotEqual(alpha, beta);

    }

}
