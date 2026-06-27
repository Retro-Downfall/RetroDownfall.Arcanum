using RetroDownfall.Arcanum.Api.Configuration;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Api.Configuration;

public sealed class ConfigurationRedactorTests
{

    [Fact]
    public void Redact_MasksSecretsAndEndpoints()
    {
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "openai",
                    Endpoint = "https://api.openai.com/v1",
                    ApiKey = "sk-live",
                    LlamaCpp = new ProviderLlamaCppSettings
                    {
                        ModelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["model-a"] = "https://models.test/a.gguf",
                        },
                    },
                },
            ],
            CommLink = new CommLinkSettings { WebhookUrl = "https://hooks.test/secret" },
        };

        ArcanumSettings redacted = ConfigurationRedactor.Redact(settings);

        Assert.Equal("***", redacted.Providers![0].ApiKey);

        Assert.Equal("***", redacted.Providers[0].Endpoint);

        Assert.Equal("***", redacted.Providers[0].LlamaCpp!.ModelMap!["model-a"]);

        Assert.Equal("***", redacted.CommLink.WebhookUrl);
    }

    [Fact]
    public void Redact_EmptySecrets_RemainEmpty()
    {
        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "local", Endpoint = string.Empty, ApiKey = null }],
        };

        ArcanumSettings redacted = ConfigurationRedactor.Redact(settings);

        Assert.Null(redacted.Providers![0].ApiKey);

        Assert.Equal(string.Empty, redacted.Providers[0].Endpoint);
    }

    [Fact]
    public void MergeApiKeys_PreservesCurrentKeyWhenRequestSendsMask()
    {
        ArcanumSettings current = new()
        {
            Providers = [new ProviderSettings { Name = "openai", ApiKey = "sk-real" }],
        };

        ArcanumSettings request = new()
        {
            Providers = [new ProviderSettings { Name = "openai", ApiKey = "***" }],
        };

        ArcanumSettings merged = ConfigurationRedactor.MergeApiKeys(request, current);

        Assert.Equal("sk-real", merged.Providers![0].ApiKey);
    }

    [Fact]
    public void MergeApiKeys_ReplacesKeyWhenRequestSendsNewValue()
    {
        ArcanumSettings current = new()
        {
            Providers = [new ProviderSettings { Name = "openai", ApiKey = "sk-old" }],
        };

        ArcanumSettings request = new()
        {
            Providers = [new ProviderSettings { Name = "openai", ApiKey = "sk-new" }],
        };

        ArcanumSettings merged = ConfigurationRedactor.MergeApiKeys(request, current);

        Assert.Equal("sk-new", merged.Providers![0].ApiKey);
    }

    [Fact]
    public void MergeApiKeys_RoundTripRestoresAllMaskedFields()
    {
        ArcanumSettings current = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "openai",
                    Endpoint = "https://api.openai.com/v1",
                    ApiKey = "sk-live",
                    LlamaCpp = new ProviderLlamaCppSettings
                    {
                        ModelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["model-a"] = "https://models.test/a.gguf",
                        },
                    },
                },
            ],
            CommLink = new CommLinkSettings { WebhookUrl = "https://hooks.test/secret" },
        };

        ArcanumSettings redacted = ConfigurationRedactor.Redact(current);

        ArcanumSettings merged = ConfigurationRedactor.MergeApiKeys(redacted, current);

        Assert.Equal("sk-live", merged.Providers![0].ApiKey);

        Assert.Equal("https://api.openai.com/v1", merged.Providers[0].Endpoint);

        Assert.Equal("https://models.test/a.gguf", merged.Providers[0].LlamaCpp!.ModelMap!["model-a"]);

        Assert.Equal("https://hooks.test/secret", merged.CommLink.WebhookUrl);
    }

    [Fact]
    public void MergeApiKeys_PreservesEndpointWhenRequestSendsMask()
    {
        ArcanumSettings current = new()
        {
            Providers = [new ProviderSettings { Name = "local", Endpoint = "http://127.0.0.1:11434" }],
        };

        ArcanumSettings request = new()
        {
            Providers = [new ProviderSettings { Name = "local", Endpoint = "***" }],
        };

        ArcanumSettings merged = ConfigurationRedactor.MergeApiKeys(request, current);

        Assert.Equal("http://127.0.0.1:11434", merged.Providers![0].Endpoint);
    }

}
