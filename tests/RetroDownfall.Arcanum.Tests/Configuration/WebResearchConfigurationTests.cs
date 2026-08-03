using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Configuration;

[Collection("ProcessEnvironment")]
public sealed class WebResearchConfigurationTests : IDisposable
{
    private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);

    [Fact]
    public void Defaults_are_code_owned_and_resolve_from_public_feature_and_integration_facts()
    {
        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { WebBrowsing = true },
            Integrations = new IntegrationSettings
            {
                WebResearch = new WebResearchIntegrationSettings
                {
                    SearchProvider = "custom-search",
                    PerplexityModel = " SONAR-PRO ",
                    CredentialEnvironmentVariable = "ARCANUM_TEST_WEB_RESEARCH_KEY",
                },
            },
        };

        WebBrowsingSettings resolved = settings.ResolveWebBrowsing();

        Assert.True(resolved.Enabled);
        Assert.Equal("custom-search", resolved.SearchProvider);
        Assert.Equal(WebResearchModels.SonarPro, resolved.PerplexityModel);
        Assert.Equal(
            "ARCANUM_TEST_WEB_RESEARCH_KEY",
            resolved.CredentialEnvironmentVariable);
        Assert.Equal(15, resolved.IdleTimeoutSeconds);
        Assert.Equal(1_000_000, resolved.MaxResponseBytes);
        Assert.Equal(50_000, resolved.MaxContentBytes);
        Assert.Equal(20, resolved.MaxCitations);
        Assert.Equal(10, resolved.MaxLinks);
        Assert.Equal(5, resolved.MaxRedirects);
    }

    [Fact]
    public void Public_web_research_schema_contains_provider_facts_but_no_secret_value()
    {
        Assert.Equal(
            [
                nameof(WebResearchIntegrationSettings.CredentialEnvironmentVariable),
                nameof(WebResearchIntegrationSettings.PerplexityModel),
                nameof(WebResearchIntegrationSettings.SearchProvider),
            ],
            typeof(WebResearchIntegrationSettings)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(static property => property.Name)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.NotNull(
            ConfigurationJsonContext.Default.GetTypeInfo(
                typeof(WebResearchIntegrationSettings)));
    }

    [Fact]
    public void Source_generated_configuration_binder_populates_web_research_integration()
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            ["Arcanum:Features:WebBrowsing"] = "true",
            ["Arcanum:Integrations:WebResearch:SearchProvider"] = "perplexity",
            ["Arcanum:Integrations:WebResearch:PerplexityModel"] = "sonar-pro",
            ["Arcanum:Integrations:WebResearch:CredentialEnvironmentVariable"] =
                "ARCANUM_TEST_WEB_RESEARCH_BINDING_KEY",
        };
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        ServiceCollection services = new();
        services.Configure<ArcanumSettings>(
            configuration.GetSection("Arcanum"));
        using ServiceProvider provider = services.BuildServiceProvider();

        ArcanumSettings settings =
            provider.GetRequiredService<IOptions<ArcanumSettings>>().Value;

        Assert.True(settings.Features.WebBrowsing);
        Assert.Equal(
            WebResearchProviderNames.Perplexity,
            settings.Integrations.WebResearch.SearchProvider);
        Assert.Equal(
            WebResearchModels.SonarPro,
            settings.Integrations.WebResearch.PerplexityModel);
        Assert.Equal(
            "ARCANUM_TEST_WEB_RESEARCH_BINDING_KEY",
            settings.Integrations.WebResearch.CredentialEnvironmentVariable);
    }

    [Fact]
    public void Configuration_serialization_never_reads_or_writes_referenced_api_key()
    {
        const string environmentVariable = "ARCANUM_TEST_WEB_RESEARCH_SERIALIZATION_KEY";
        const string secret = "pplx-secret-that-must-not-be-serialized";
        RememberAndSet(environmentVariable, secret);
        ArcanumSettings settings = new()
        {
            Integrations = new IntegrationSettings
            {
                WebResearch = new WebResearchIntegrationSettings
                {
                    CredentialEnvironmentVariable = environmentVariable,
                },
            },
        };

        string json = JsonSerializer.Serialize(
            settings,
            ConfigurationJsonContext.Default.ArcanumSettings);

        Assert.Contains(environmentVariable, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"apiKey\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Credential_resolver_uses_default_only_when_reference_is_absent(string? reference)
    {
        RememberAndSet(
            EnvironmentCredentialResolver.DefaultPerplexityApiKeyEnvironmentVariable,
            "default-secret");
        WebBrowsingSettings settings = new()
        {
            CredentialEnvironmentVariable = reference,
        };

        Assert.Equal(
            EnvironmentCredentialResolver.DefaultPerplexityApiKeyEnvironmentVariable,
            EnvironmentCredentialResolver.GetWebResearchApiKeyEnvironmentVariableName(settings));
        Assert.Equal(
            "default-secret",
            EnvironmentCredentialResolver.ResolveWebResearchApiKey(settings));
    }

    [Fact]
    public void Credential_resolver_does_not_fall_through_when_explicit_reference_is_missing()
    {
        const string custom = "ARCANUM_TEST_MISSING_WEB_RESEARCH_KEY";
        RememberAndSet(
            EnvironmentCredentialResolver.DefaultPerplexityApiKeyEnvironmentVariable,
            "default-secret");
        RememberAndSet(custom, null);
        WebBrowsingSettings settings = new()
        {
            CredentialEnvironmentVariable = custom,
        };

        Assert.Equal(
            custom,
            EnvironmentCredentialResolver.GetWebResearchApiKeyEnvironmentVariableName(settings));
        Assert.Null(EnvironmentCredentialResolver.ResolveWebResearchApiKey(settings));
    }

    [Fact]
    public void Validator_rejects_invalid_model_blank_provider_and_invalid_credential_reference()
    {
        ArcanumSettings settings = new()
        {
            Integrations = new IntegrationSettings
            {
                WebResearch = new WebResearchIntegrationSettings
                {
                    SearchProvider = " ",
                    PerplexityModel = "sonar-unsupported",
                    CredentialEnvironmentVariable = "INVALID=NAME",
                },
            },
        };

        RetroDownfall.Arcanum.Core.Primitives.Result result =
            new ConfigurationValidator().Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static error =>
                error.Pointer == "integrations.webResearch.searchProvider");
        Assert.Contains(
            result.Error.Details!,
            static error =>
                error.Pointer == "integrations.webResearch.perplexityModel");
        Assert.Contains(
            result.Error.Details!,
            static error =>
                error.Pointer == "integrations.webResearch.credentialEnvironmentVariable");
    }

    [Fact]
    public void Validator_includes_web_research_reference_in_collision_detection()
    {
        const string shared = "ARCANUM_TEST_SHARED_WEB_RESEARCH_KEY";
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "provider",
                    Models = ["model"],
                    CredentialEnvironmentVariable = shared,
                },
            ],
            Integrations = new IntegrationSettings
            {
                WebResearch = new WebResearchIntegrationSettings
                {
                    CredentialEnvironmentVariable = shared.ToLowerInvariant(),
                },
            },
        };

        RetroDownfall.Arcanum.Core.Primitives.Result result =
            new ConfigurationValidator().Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static error =>
                error.Pointer == "providers[0].credentialEnvironmentVariable");
        Assert.Contains(
            result.Error.Details!,
            static error =>
                error.Pointer == "integrations.webResearch.credentialEnvironmentVariable");
        Assert.DoesNotContain(
            result.Error.Details!,
            error => error.Detail.Contains(shared, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mechanical_web_research_limits_clamp_to_safe_ranges()
    {
        Assert.Equal(1, ArcanumSettingClamps.WebBrowsingMaxQueryChars(0));
        Assert.Equal(20_000, ArcanumSettingClamps.WebBrowsingMaxQueryChars(int.MaxValue));
        Assert.Equal(1, ArcanumSettingClamps.WebBrowsingMaxUrlChars(0));
        Assert.Equal(16_384, ArcanumSettingClamps.WebBrowsingMaxUrlChars(int.MaxValue));
        Assert.Equal(1_000, ArcanumSettingClamps.WebBrowsingMaxResponseBytes(0));
        Assert.Equal(
            10_000_000,
            ArcanumSettingClamps.WebBrowsingMaxResponseBytes(int.MaxValue));
        Assert.Equal(0, ArcanumSettingClamps.WebBrowsingMaxCitations(-1));
        Assert.Equal(100, ArcanumSettingClamps.WebBrowsingMaxCitations(int.MaxValue));
        Assert.Equal(0, ArcanumSettingClamps.WebBrowsingMaxRedirects(-1));
        Assert.Equal(20, ArcanumSettingClamps.WebBrowsingMaxRedirects(int.MaxValue));
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
}
