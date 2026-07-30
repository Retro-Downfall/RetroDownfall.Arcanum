using System.Reflection;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

[Collection("ProcessEnvironment")]
public sealed class EnvironmentCredentialResolverTests : IDisposable
{
    private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);

    [Fact]
    public void ProviderAndModelSchemasExposeOnlyFactualConfiguration()
    {
        Assert.Equal(
            [
                nameof(ProviderSettings.ContextWindowLimit),
                nameof(ProviderSettings.CredentialEnvironmentVariable),
                nameof(ProviderSettings.Endpoint),
                nameof(ProviderSettings.Models),
                nameof(ProviderSettings.Name),
                nameof(ProviderSettings.Type),
            ],
            PublicPropertyNames<ProviderSettings>());
        Assert.Equal(
            [
                nameof(ModelEntry.MaxBudgetTokens),
                nameof(ModelEntry.Name),
                nameof(ModelEntry.SupportsVision),
                nameof(ModelEntry.WireDialect),
            ],
            PublicPropertyNames<ModelEntry>());
        Assert.Equal(
            [
                nameof(CommLinkIntegrationSettings.AllowedHosts),
                nameof(CommLinkIntegrationSettings.AllowedSchemes),
                nameof(CommLinkIntegrationSettings.WebhookUrlEnvironmentVariable),
            ],
            PublicPropertyNames<CommLinkIntegrationSettings>());
    }

    [Theory]
    [InlineData("OpenAI", "ARCANUM_PROVIDER_OPENAI_API_KEY")]
    [InlineData(" Acme / EU.v2 ", "ARCANUM_PROVIDER_ACME_EU_V2_API_KEY")]
    [InlineData("a---b___c", "ARCANUM_PROVIDER_A_B_C_API_KEY")]
    [InlineData("🔥", "ARCANUM_PROVIDER_UNNAMED_API_KEY")]
    public void ProviderDefaultNameUsesSafeDeterministicNormalization(
        string providerName,
        string expected)
    {
        ProviderSettings provider = new() { Name = providerName };

        Assert.Equal(
            expected,
            EnvironmentCredentialResolver.GetProviderApiKeyEnvironmentVariableName(provider));
    }

    [Fact]
    public void ExplicitProviderReferenceReplacesDerivedDefault()
    {
        const string custom = "ARCANUM_TEST_CUSTOM_PROVIDER_KEY";
        ProviderSettings provider = new()
        {
            Name = "Example",
            CredentialEnvironmentVariable = custom,
        };
        string derived = "ARCANUM_PROVIDER_EXAMPLE_API_KEY";
        RememberAndSet(derived, "derived-secret");
        RememberAndSet(custom, "custom-secret");

        Assert.Equal(
            "custom-secret",
            EnvironmentCredentialResolver.ResolveProviderApiKey(provider));

        System.Environment.SetEnvironmentVariable(custom, null);

        Assert.Null(EnvironmentCredentialResolver.ResolveProviderApiKey(provider));
    }

    [Fact]
    public void HttpsPasswordUsesExplicitReferenceOrDeterministicDefault()
    {
        const string custom = "ARCANUM_TEST_CERTIFICATE_PASSWORD";
        HttpsSettings defaults = new();
        HttpsSettings configured = new()
        {
            CertificatePasswordEnvironmentVariable = custom,
        };
        RememberAndSet(
            EnvironmentCredentialResolver.DefaultHttpsCertificatePasswordEnvironmentVariable,
            "default-password");
        RememberAndSet(custom, "custom-password");

        Assert.Equal(
            EnvironmentCredentialResolver.DefaultHttpsCertificatePasswordEnvironmentVariable,
            EnvironmentCredentialResolver.GetHttpsCertificatePasswordEnvironmentVariableName(defaults));
        Assert.Equal(
            "default-password",
            EnvironmentCredentialResolver.ResolveHttpsCertificatePassword(defaults));
        Assert.Equal(
            "custom-password",
            EnvironmentCredentialResolver.ResolveHttpsCertificatePassword(configured));
    }

    [Fact]
    public void CommLinkWebhookUsesExplicitReferenceOrDeterministicDefault()
    {
        const string custom = "ARCANUM_TEST_COMMLINK_WEBHOOK_URL";
        CommLinkSettings defaults = new();
        CommLinkSettings configured = new()
        {
            WebhookUrlEnvironmentVariable = custom,
        };
        RememberAndSet(
            EnvironmentCredentialResolver.DefaultCommLinkWebhookUrlEnvironmentVariable,
            "https://default.example.test/secret");
        RememberAndSet(custom, "https://custom.example.test/secret");

        Assert.Equal(
            EnvironmentCredentialResolver.DefaultCommLinkWebhookUrlEnvironmentVariable,
            EnvironmentCredentialResolver.GetCommLinkWebhookUrlEnvironmentVariableName(defaults));
        Assert.Equal(
            "https://default.example.test/secret",
            EnvironmentCredentialResolver.ResolveCommLinkWebhookUrl(defaults));
        Assert.Equal(
            "https://custom.example.test/secret",
            EnvironmentCredentialResolver.ResolveCommLinkWebhookUrl(configured));
    }

    [Theory]
    [InlineData("9STARTS_WITH_DIGIT")]
    [InlineData("HAS-HYPHEN")]
    [InlineData("HAS.DOT")]
    [InlineData("HAS=EQUALS")]
    [InlineData("sk-secret-value")]
    public void EnvironmentReferencesRequirePortableNames(string name)
    {
        Assert.False(
            EnvironmentCredentialResolver.IsValidEnvironmentVariableName(name));
    }

    public void Dispose()
    {
        foreach ((string name, string? value) in _originalValues)
        {
            System.Environment.SetEnvironmentVariable(name, value);
        }
    }

    private void RememberAndSet(string name, string? value)
    {
        if (!_originalValues.ContainsKey(name))
        {
            _originalValues[name] = System.Environment.GetEnvironmentVariable(name);
        }

        System.Environment.SetEnvironmentVariable(name, value);
    }

    private static string[] PublicPropertyNames<T>() =>
        typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
}
