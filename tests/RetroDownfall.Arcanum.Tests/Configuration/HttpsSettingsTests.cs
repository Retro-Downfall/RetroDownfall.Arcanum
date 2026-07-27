using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class HttpsSettingsTests
{

    [Fact]
    public void Defaults_are_disabled_on_port_5443_with_no_paths()
    {

        HttpsSettings https = new();

        Assert.False(https.Enabled);

        Assert.Equal(5443, https.Port);

        Assert.Null(https.CertificatePath);

        Assert.Null(https.PrivateKeyPath);

        Assert.Null(https.CertificatePasswordEnvironmentVariable);

    }

    [Fact]
    public void HostSettings_defaults_include_disabled_https()
    {

        HostSettings host = new();

        Assert.NotNull(host.Https);

        Assert.False(host.Https.Enabled);

        Assert.Equal(5443, host.Https.Port);

    }

}
