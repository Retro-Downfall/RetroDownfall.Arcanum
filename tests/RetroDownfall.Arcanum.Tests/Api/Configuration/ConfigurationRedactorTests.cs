using System.Text.Json;
using RetroDownfall.Arcanum.Api.Configuration;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Api.Configuration;

[Collection("ProcessEnvironment")]
public sealed class ConfigurationRedactorTests : IDisposable
{
    private const string CredentialVariable = "ARCANUM_TEST_REDACTOR_PROVIDER_KEY";
    private const string CertificateVariable = "ARCANUM_TEST_REDACTOR_CERT_PASSWORD";
    private const string WebhookVariable = "ARCANUM_TEST_REDACTOR_COMMLINK_URL";
    private readonly string? _originalCredential =
        System.Environment.GetEnvironmentVariable(CredentialVariable);
    private readonly string? _originalCertificate =
        System.Environment.GetEnvironmentVariable(CertificateVariable);
    private readonly string? _originalWebhook =
        System.Environment.GetEnvironmentVariable(WebhookVariable);

    [Fact]
    public void RedactMasksEndpointsAndPreservesSecretReferences()
    {
        ArcanumSettings settings = Settings();

        ArcanumSettings redacted = ConfigurationRedactor.Redact(settings);

        Assert.Equal("***", redacted.Providers[0].Endpoint);
        Assert.Equal(
            CredentialVariable,
            redacted.Providers[0].CredentialEnvironmentVariable);
        Assert.Equal(
            WebhookVariable,
            redacted.Integrations.CommLink.WebhookUrlEnvironmentVariable);
        Assert.Equal(
            CertificateVariable,
            redacted.Host.Https.CertificatePasswordEnvironmentVariable);
    }

    [Fact]
    public void RedactNeverReadsOrSerializesReferencedEnvironmentValues()
    {
        System.Environment.SetEnvironmentVariable(
            CredentialVariable,
            "provider-secret-material");
        System.Environment.SetEnvironmentVariable(
            CertificateVariable,
            "certificate-secret-material");
        System.Environment.SetEnvironmentVariable(
            WebhookVariable,
            "https://hooks.test/secret-material");

        ArcanumSettings redacted = ConfigurationRedactor.Redact(Settings());
        string json = JsonSerializer.Serialize(
            new ArcanumConfigurationFile { Arcanum = redacted },
            ConfigurationJsonContext.Default.ArcanumConfigurationFile);

        Assert.DoesNotContain("provider-secret-material", json, StringComparison.Ordinal);
        Assert.DoesNotContain("certificate-secret-material", json, StringComparison.Ordinal);
        Assert.DoesNotContain("https://hooks.test/secret-material", json, StringComparison.Ordinal);
        Assert.Contains(CredentialVariable, json, StringComparison.Ordinal);
        Assert.Contains(CertificateVariable, json, StringComparison.Ordinal);
        Assert.Contains(WebhookVariable, json, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeRedactedSecretsRestoresEndpointAndPreservesReferences()
    {
        ArcanumSettings current = Settings();
        ArcanumSettings redacted = ConfigurationRedactor.Redact(current);

        ArcanumSettings merged =
            ConfigurationRedactor.MergeRedactedSecrets(redacted, current);

        Assert.Equal(
            "https://api.openai.com/v1",
            merged.Providers[0].Endpoint);
        Assert.Equal(
            WebhookVariable,
            merged.Integrations.CommLink.WebhookUrlEnvironmentVariable);
        Assert.Equal(
            CredentialVariable,
            merged.Providers[0].CredentialEnvironmentVariable);
    }

    [Fact]
    public void ValidateNoResidualMaskRejectsNewProviderEndpointMask()
    {
        ArcanumSettings merged = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "brand-new",
                    Endpoint = "***",
                    Models = ["model"],
                },
            ],
        };

        Result result = ConfigurationRedactor.ValidateNoResidualMask(merged);

        Assert.True(result.IsFailure);
        Assert.Equal("Config.UnresolvedMask", result.Error.Code);
    }

    [Fact]
    public void ValidateNoResidualMaskAcceptsCredentialReference()
    {
        ArcanumSettings settings = Settings();
        ArcanumSettings merged =
            ConfigurationRedactor.MergeRedactedSecrets(
                ConfigurationRedactor.Redact(settings),
                settings);

        Assert.True(ConfigurationRedactor.ValidateNoResidualMask(merged).IsSuccess);
    }

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable(
            CredentialVariable,
            _originalCredential);
        System.Environment.SetEnvironmentVariable(
            CertificateVariable,
            _originalCertificate);
        System.Environment.SetEnvironmentVariable(
            WebhookVariable,
            _originalWebhook);
    }

    private static ArcanumSettings Settings() =>
        new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "openai",
                    Endpoint = "https://api.openai.com/v1",
                    CredentialEnvironmentVariable = CredentialVariable,
                    Models = ["gpt-5"],
                },
            ],
            Host = new HostSettings
            {
                Https = new HttpsSettings
                {
                    CertificatePasswordEnvironmentVariable =
                        CertificateVariable,
                },
            },
            Integrations = new IntegrationSettings
            {
                CommLink = new CommLinkIntegrationSettings
                {
                    WebhookUrlEnvironmentVariable = WebhookVariable,
                },
            },
        };
}
