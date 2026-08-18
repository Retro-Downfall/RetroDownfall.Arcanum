using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ConfigurationValidatorTests
{

    private readonly ConfigurationValidator _validator = new();

    [Fact]
    public void Validate_ValidOpenAiCompatibleProvider_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = ["llama3"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_InvalidProviderCredentialEnvironmentVariable_ReturnsFailure()
    {
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "provider",
                    Type = AiProviderKind.OpenAICompatible,
                    CredentialEnvironmentVariable = "INVALID=NAME",
                    Models = ["model"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static error =>
                error.Pointer
                == "providers[0].credentialEnvironmentVariable");
    }

    [Theory]
    [InlineData("Arcanum__Host__Port")]
    [InlineData("ARCANUM_Arcanum__Host__Port")]
    [InlineData("ARCANUM_EDITION")]
    [InlineData("ARCANUM_HOST_ANY")]
    [InlineData("ARCANUM_ALLOW_HOST_PROCESS_TOOLS")]
    [InlineData("ARCANUM_SKIP_KEY_BOOTSTRAP")]
    public void Validate_SecretReferenceOverlappingConfigurationNamespace_ReturnsFailure(
        string environmentVariable)
    {
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "provider",
                    Type = AiProviderKind.OpenAICompatible,
                    CredentialEnvironmentVariable = environmentVariable,
                    Models = ["model"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static error =>
                error.Pointer
                == "providers[0].credentialEnvironmentVariable");
    }

    [Fact]
    public void Validate_BlankProviderName_ReturnsFailure()
    {
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "   ",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = ["model"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "providers[0].name");
    }

    [Fact]
    public void Validate_DerivedProviderCredentialReferencesCollide_ReturnsFailure()
    {
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "A-B",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = ["model-a"],
                },
                new ProviderSettings
                {
                    Name = "A B",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = ["model-b"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "providers[0].credentialEnvironmentVariable");
        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "providers[1].credentialEnvironmentVariable");
    }

    [Fact]
    public void Validate_ExplicitProviderReferenceDisambiguatesDerivedReferences_ReturnsSuccess()
    {
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "A-B",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = ["model-a"],
                },
                new ProviderSettings
                {
                    Name = "A B",
                    Type = AiProviderKind.OpenAICompatible,
                    CredentialEnvironmentVariable = "ARCANUM_PROVIDER_A_B_SECONDARY_API_KEY",
                    Models = ["model-b"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validate_CommLinkReferenceMustBePortableAndUniqueWithoutEchoingIt()
    {
        const string sharedReference = "SHARED_SECRET_REFERENCE";
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "provider",
                    Type = AiProviderKind.OpenAICompatible,
                    CredentialEnvironmentVariable = sharedReference,
                    Models = ["model"],
                },
            ],
            Integrations = new IntegrationSettings
            {
                CommLink = new CommLinkIntegrationSettings
                {
                    WebhookUrlEnvironmentVariable = sharedReference.ToLowerInvariant(),
                },
            },
        };

        Result collision = _validator.Validate(settings);

        Assert.True(collision.IsFailure);
        Assert.Contains(
            collision.Error.Details!,
            static error => error.Pointer == "providers[0].credentialEnvironmentVariable");
        Assert.Contains(
            collision.Error.Details!,
            static error => error.Pointer == "integrations.commLink.webhookUrlEnvironmentVariable");
        Assert.DoesNotContain(
            collision.Error.Details!,
            error => error.Detail.Contains(sharedReference, StringComparison.OrdinalIgnoreCase));

        settings.Integrations.CommLink.WebhookUrlEnvironmentVariable = "INVALID=NAME";

        Result invalid = _validator.Validate(settings);

        Assert.True(invalid.IsFailure);
        Assert.Contains(
            invalid.Error.Details!,
            static error => error.Pointer == "integrations.commLink.webhookUrlEnvironmentVariable");
        Assert.DoesNotContain(
            invalid.Error.Details!,
            static error => error.Detail.Contains("INVALID=NAME", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_InvalidCertificatePasswordEnvironmentVariable_ReturnsFailure()
    {
        ArcanumSettings settings = new()
        {
            Host = new HostSettings
            {
                Https = new HttpsSettings
                {
                    CertificatePasswordEnvironmentVariable = "INVALID=NAME",
                },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static error =>
                error.Pointer
                == "host.https.certificatePasswordEnvironmentVariable");
    }

    [Fact]
    public void Validate_PricingRatesOutsideSupportedRange_ReturnsFailure()
    {
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "provider",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = ["model"],
                },
            ],
            Cost = new CostSettings
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry { InputPer1M = -1m },
                    ModelPricing =
                    {
                        ["model"] = new ModelPricingEntry { ReasoningPer1M = 1_000_001m },
                    },
                },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "cost.pricing.defaultPricing.inputPer1M");
        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "cost.pricing.modelPricing[model].reasoningPer1M");
    }

    [Fact]
    public void Validate_OpenAiCompatibleProviderWithoutModels_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = [],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Configuration.ValidationFailed", result.Error.Code);

        Assert.NotNull(result.Error.Details);

        Assert.Contains(result.Error.Details!, static e => e.Detail.Contains("ollama", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(result.Error.Details!, static e => e.Detail.Contains("no configured models", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public void Validate_OpenAiCompatibleProviderWithMalformedEndpoint_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "not a valid uri",
                    Models = ["llama3"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.NotNull(result.Error.Details);

        Assert.Contains(
            result.Error.Details!,
            static e => e.Detail.Contains("endpoint", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            result.Error.Details!,
            static e => e.Detail.Contains("not a valid uri", StringComparison.Ordinal));

    }

    [Fact]
    public void Validate_OpenAiCompatibleProviderWithNonHttpEndpointScheme_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "ftp://example.test/v1",
                    Models = ["llama3"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

    }

    [Fact]
    public void Validate_OpenAiCompatibleProviderWithValidHttpsEndpoint_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://example.test/v1",
                    Models = ["llama3"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_DuplicateProviderNames_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "shared", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://one.example.test/v1", Models = ["m1"] },
                new ProviderSettings { Name = "shared", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://two.example.test/v1", Models = ["m2"] },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.NotNull(result.Error.Details);

        Assert.Contains(result.Error.Details!, static e => e.Detail.Contains("unique", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public void Validate_DuplicateProviderNamesCaseInsensitive_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "Shared", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://one.example.test/v1", Models = ["m1"] },
                new ProviderSettings { Name = "shared", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://two.example.test/v1", Models = ["m2"] },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

    }

    [Fact]
    public void Validate_UniqueProviderNames_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "one", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://one.example.test/v1", Models = ["m1"] },
                new ProviderSettings { Name = "two", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://two.example.test/v1", Models = ["m2"] },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_DefaultModelNotConfigured_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            DefaultModel = "missing-model",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = ["llama3"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.NotNull(result.Error.Details);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "defaultModel");

        Assert.Contains(result.Error.Details!, static e => e.Detail.Contains("missing-model", StringComparison.Ordinal));

    }

    [Fact]
    public void Validate_FastModelNotConfigured_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            FastModel = "fast-missing",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = ["llama3"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.NotNull(result.Error.Details);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "fastModel");

        Assert.Contains(result.Error.Details!, static e => e.Detail.Contains("fast-missing", StringComparison.Ordinal));

    }

    [Fact]
    public void Validate_DefaultModelExactMatch_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            DefaultModel = "llama3:latest",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = ["llama3:latest"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_MultipleErrors_ReturnsStructuredDetails()
    {

        ArcanumSettings settings = new()
        {
            DefaultModel = "missing",
            FastModel = "also-missing",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "empty",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = [],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.NotNull(result.Error.Details);

        Assert.True(result.Error.Details!.Count >= 3);

        Assert.Contains(result.Error.Details, static e => e.Pointer == "providers[0]");

        Assert.Contains(result.Error.Details, static e => e.Pointer == "defaultModel");

        Assert.Contains(result.Error.Details, static e => e.Pointer == "fastModel");

    }

    [Fact]
    public void RuntimeDefaults_JsonRpcFrameAccommodatesToolOutputCap()
    {
        IntelligenceSettings intelligence = ArcanumRuntimeDefaults.Intelligence;
        McpSettings mcp = ArcanumRuntimeDefaults.Mcp;
        long configuredCap =
            ArcanumSettingClamps.ToolOutputCapBytes(intelligence.ToolOutputCapBytes);
        long effectiveCap = ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
            intelligence.ToolOutputCapBytes,
            ArcanumSettingClamps.McpMaxJsonRpcLineBytes(mcp.MaxJsonRpcLineBytes));

        Assert.Equal(configuredCap, effectiveCap);

    }

    [Fact]
    public void Validate_NullProviders_uses_empty_provider_list()
    {

        ArcanumSettings settings = new()
        {

            Providers = null!,

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_FastModelExactMatch_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {

            FastModel = "llama3:latest",

            Providers =
            [

                new ProviderSettings
                {

                    Name = "ollama",

                    Type = AiProviderKind.OpenAICompatible,

                    Models = ["llama3:latest"],

                },

            ],

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_RelativeAllowedRoot_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [

                new ProviderSettings
                {

                    Name = "ollama",

                    Type = AiProviderKind.OpenAICompatible,

                    Models = ["llama3"],

                },

            ],

            Security = new SecuritySettings
            {
                CampaignRoots = ["relative/path"],

            },

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "security.campaignRoots");

    }

    [Fact]
    public void Validate_MissingAllowedRootDirectory_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [

                new ProviderSettings
                {

                    Name = "ollama",

                    Type = AiProviderKind.OpenAICompatible,

                    Models = ["llama3"],

                },

            ],

            Security = new SecuritySettings
            {
                SpellWorkspaceRoots = [Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())],

            },

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "security.spellWorkspaceRoots");

    }

    [Fact]
    public void Validate_ValidAllowedRootDirectory_ReturnsSuccess()
    {

        string tempDir = Path.Combine(Path.GetTempPath(), "arcanum-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempDir);

        try
        {

            ArcanumSettings settings = new()
            {

                Providers =
                [

                    new ProviderSettings
                    {

                        Name = "ollama",

                        Type = AiProviderKind.OpenAICompatible,

                        Models = ["llama3"],

                    },

                ],

                Security = new SecuritySettings
                {
                    PerceptionWorkspaceRoots = [tempDir],

                },

            };

            Result result = _validator.Validate(settings);

            Assert.True(result.IsSuccess);

        }
        finally
        {

            Directory.Delete(tempDir, recursive: true);

        }

    }

    [Fact]
    public void Validate_MissingHostWorkspace_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [

                new ProviderSettings
                {

                    Name = "ollama",

                    Type = AiProviderKind.OpenAICompatible,

                    Models = ["llama3"],

                },

            ],

            Workspaces = new WorkspaceSettings
            {
                DefaultRoot = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()),

            },

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "workspaces.defaultRoot");

    }

    [Fact]
    public void Validate_NullFeatures_DoesNotThrow()
    {

        ArcanumSettings settings = new()
        {

            Features = null!,

            Providers =
            [

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] },

            ],

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_NullIntegrations_DoesNotThrow()
    {

        ArcanumSettings settings = new()
        {

            Integrations = null!,

            Providers =
            [

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] },

            ],

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_NullProviderModels_TreatedAsNoModels()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = null! },

            ],

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "providers[0]");

    }

    [Fact]
    public void Validate_NullPublicPolicySubObjects_DoesNotThrow()
    {

        ArcanumSettings settings = new()
        {

            Security = null!,
            Workspaces = null!,
            Host = null!,

            Providers =
            [

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] },

            ],

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_EmbeddingsDisabled_ReturnsSuccess_WithNoProviderOrModel()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Features = new FeatureSettings { Embeddings = false },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_EmbeddingsEnabledWithoutProvider_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings { Model = "nomic-embed-text" },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "integrations.embeddings.provider");

    }

    [Fact]
    public void Validate_EmbeddingsEnabledWithoutModel_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings { Provider = "ollama" },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "integrations.embeddings.model");

    }

    [Fact]
    public void Validate_EmbeddingsEnabledWithUnknownProvider_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "does-not-exist",
                    Model = "nomic-embed-text",
                },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "integrations.embeddings.provider");

        Assert.Contains(result.Error.Details!, static e => e.Detail.Contains("does-not-exist", StringComparison.Ordinal));

    }

    [Fact]
    public void Validate_EmbeddingsEnabledWithValidProviderAndModel_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "ollama",
                    Model = "nomic-embed-text",
                },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Theory]
    [InlineData(nameof(FeatureSettings.SessionSearch))]
    [InlineData(nameof(FeatureSettings.CodebaseRetrieval))]
    [InlineData(nameof(FeatureSettings.Saga))]
    [InlineData(nameof(FeatureSettings.SemanticSpellRouting))]
    public void Validate_EmbeddingBackedFeatureWithoutEmbeddings_DerivesSubstrate(string flagName)
    {

        FeatureSettings features = flagName switch
        {
            nameof(FeatureSettings.SessionSearch) => new FeatureSettings { SessionSearch = true },
            nameof(FeatureSettings.CodebaseRetrieval) => new FeatureSettings { CodebaseRetrieval = true },
            nameof(FeatureSettings.Saga) => new FeatureSettings { Saga = true },
            nameof(FeatureSettings.SemanticSpellRouting) => new FeatureSettings { SemanticSpellRouting = true },
            _ => throw new ArgumentOutOfRangeException(nameof(flagName)),
        };

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Features = features,
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "ollama",
                    Model = "nomic-embed-text",
                },
            },
        };

        EmbeddingSettings embeddings = settings.ResolveEmbeddings();
        Result result = _validator.Validate(settings);

        Assert.True(embeddings.Enabled);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_SagaExtractionWithoutSagaOrEmbeddings_DerivesBothParents()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Features = new FeatureSettings { SagaExtraction = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "ollama",
                    Model = "nomic-embed-text",
                },
            },
        };

        EmbeddingSettings embeddings = settings.ResolveEmbeddings();
        Result result = _validator.Validate(settings);

        Assert.True(embeddings.Enabled);

        Assert.True(embeddings.SagaEnabled);

        Assert.True(embeddings.Saga.ExtractionEnabled);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_AllFeatureFlagsEnabledWithEmbeddingsEnabled_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Features = new FeatureSettings
            {
                Embeddings = true,
                SessionSearch = true,
                CodebaseRetrieval = true,
                Saga = true,
                SemanticSpellRouting = true,
            },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "ollama",
                    Model = "nomic-embed-text",
                },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_ScryingDefaults_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void ScryingMaxImageBytes_clamps_to_physical_bounds()
    {
        Assert.Equal(1_024L, ArcanumSettingClamps.ScryingMaxImageBytes(0));
        Assert.Equal(
            20L * 1024L * 1024L,
            ArcanumSettingClamps.ScryingMaxImageBytes(long.MaxValue));

    }

    [Fact]
    public void ScryingMaxImagesPerRequest_clamps_to_physical_bounds()
    {
        Assert.Equal(1, ArcanumSettingClamps.ScryingMaxImagesPerRequest(0));
        Assert.Equal(100, ArcanumSettingClamps.ScryingMaxImagesPerRequest(int.MaxValue));

    }

    [Fact]
    public void Validate_ScryingEnabledWithEmptyAllowedMimeTypes_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Features = new FeatureSettings { Scrying = true },
            Security = new SecuritySettings { AllowedImageMimeTypes = [] },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "security.allowedImageMimeTypes");

    }

    [Fact]
    public void Validate_ScryingDisabledWithEmptyAllowedMimeTypes_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Features = new FeatureSettings { Scrying = false },
            Security = new SecuritySettings { AllowedImageMimeTypes = [] },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_ProviderModelsWithVisionCapableEntry_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "openai",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = [new ModelEntry("gpt-4o", SupportsVision: true), "gpt-4o-mini"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_ProviderModelWithReasoningDefaults_ReturnsSuccess()
    {

        Result result = _validator.Validate(SettingsWithReasoning(null, null));

        Assert.True(result.IsSuccess);

    }

    [Theory]
    [InlineData(ReasoningWireDialect.Standard, true)]
    [InlineData(ReasoningWireDialect.OpenRouter, true)]
    [InlineData(ReasoningWireDialect.TopLevelReasoningBudget, true)]
    [InlineData(ReasoningWireDialect.AnthropicThinking, true)]
    public void Validate_EveryReasoningDialectCombination(
        ReasoningWireDialect wireDialect,
        bool expectedValid)
    {

        Result result = _validator.Validate(SettingsWithReasoning(wireDialect, null));

        Assert.Equal(expectedValid, result.IsSuccess);

    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2_097_153)]
    public void Validate_ReasoningMaxBudgetOutsideClamp_ReturnsFailure(int maxBudgetTokens)
    {

        

        Result result = _validator.Validate(SettingsWithReasoning(ReasoningWireDialect.OpenRouter, maxBudgetTokens));

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "providers[0].models[0].reasoning.maxBudgetTokens");

    }

    [Fact]
    public void Validate_BudgetControlWithStandardDialect_ReturnsFailure()
    {

        

        Result result = _validator.Validate(SettingsWithReasoning(ReasoningWireDialect.Standard, 32768));

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "providers[0].models[0].reasoning.wireDialect");

    }


    [Fact]
    public void Validate_MaxBudgetWithStandardDialect_ReturnsFailure()
    {

        Result result = _validator.Validate(SettingsWithReasoning(ReasoningWireDialect.Standard, 32768));

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "providers[0].models[0].reasoning.wireDialect");

    }



    [Fact]
    public void Validate_MaxBudgetWithOmittedDialect_ReturnsFailure()
    {

        Result result = _validator.Validate(SettingsWithReasoning(null, 32768));

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "providers[0].models[0].reasoning.wireDialect");

    }

    [Fact]
    public void Validate_NullProviderElement_ReturnsPointerBearingFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers = [null!],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "providers[0]");

    }

    [Fact]
    public void Validate_NullModelEntry_ReturnsPointerBearingFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = [null!],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "providers[0].models[0]");

    }

    [Fact]
    public void Validate_NullA2ASkillElement_ReturnsPointerBearingFailure()
    {

        ArcanumSettings settings = new()
        {
            Integrations = new IntegrationSettings
            {
                A2A = new A2AIntegrationSettings
                {
                    Skills = [null!],
                },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "integrations.a2A.skills[0]");

    }

    [Theory]
    [InlineData(ReasoningWireDialect.OpenRouter)]
    [InlineData(ReasoningWireDialect.TopLevelReasoningBudget)]
    [InlineData(ReasoningWireDialect.AnthropicThinking)]
    public void Validate_BudgetControlWithExplicitNumericDialect_ReturnsSuccess(ReasoningWireDialect dialect)
    {

        

        Result result = _validator.Validate(SettingsWithReasoning(dialect, 32768));

        Assert.True(result.IsSuccess);

    }


    [Fact]
    public void Validate_ListenAnyWithoutHttps_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Host = new HostSettings
            {
                ListenAny = true,
                Https = new HttpsSettings { Enabled = false },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "host.https.enabled");

    }

    [Fact]
    public void Validate_HttpsDisabledWithNoCertificatePath_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Host = new HostSettings { Https = new HttpsSettings { Enabled = false } },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_HttpsEnabledWithoutCertificatePath_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Host = new HostSettings { Https = new HttpsSettings { Enabled = true, Port = 5443, CertificatePath = null } },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "host.https.certificatePath");

    }

    [Fact]
    public void Validate_HttpsPortEqualsHttpPort_ReturnsFailure()
    {

        string certificatePath = Path.GetTempFileName();

        try
        {

            ArcanumSettings settings = new()
            {
                Host = new HostSettings
                {
                    Port = 5001,
                    Https = new HttpsSettings { Enabled = true, Port = 5001, CertificatePath = certificatePath },
                },
            };

            Result result = _validator.Validate(settings);

            Assert.True(result.IsFailure);

            Assert.Contains(result.Error.Details!, static e => e.Pointer == "host.https.port");

        }
        finally
        {

            File.Delete(certificatePath);

        }

    }

    [Theory]
    [InlineData(0)]
    [InlineData(70_000)]
    public void Validate_HttpsPortOutsideClamp_ReturnsFailure(int port)
    {

        string certificatePath = Path.GetTempFileName();

        try
        {

            ArcanumSettings settings = new()
            {
                Host = new HostSettings
                {
                    Port = 5001,
                    Https = new HttpsSettings { Enabled = true, Port = port, CertificatePath = certificatePath },
                },
            };

            Result result = _validator.Validate(settings);

            Assert.True(result.IsFailure);

            Assert.Contains(result.Error.Details!, static e => e.Pointer == "host.https.port");

        }
        finally
        {

            File.Delete(certificatePath);

        }

    }

    [Fact]
    public void Validate_HttpsEnabledWithMissingCertificateFile_ReturnsFailure()
    {

        string missing = Path.Combine(Path.GetTempPath(), $"arcanum-missing-{Guid.NewGuid():N}.pfx");

        ArcanumSettings settings = new()
        {
            Host = new HostSettings
            {
                Port = 5001,
                Https = new HttpsSettings { Enabled = true, Port = 5443, CertificatePath = missing },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "host.https.certificatePath");

    }

    [Fact]
    public void Validate_HttpsEnabledWithExistingPfx_ReturnsSuccess()
    {

        string certificatePath = Path.GetTempFileName();

        try
        {

            ArcanumSettings settings = new()
            {
                Host = new HostSettings
                {
                    Port = 5001,
                    Https = new HttpsSettings
                    {
                        Enabled = true,
                        Port = 5443,
                        CertificatePath = certificatePath,
                        CertificatePasswordEnvironmentVariable =
                            "ARCANUM_CERT_PASSWORD",
                    },
                },
            };

            Result result = _validator.Validate(settings);

            Assert.True(result.IsSuccess);

        }
        finally
        {

            File.Delete(certificatePath);

        }

    }

    [Fact]
    public void Validate_HttpsPemWithMissingPrivateKey_ReturnsFailure()
    {

        string certificatePath = Path.GetTempFileName();

        string missingKey = Path.Combine(Path.GetTempPath(), $"arcanum-missing-{Guid.NewGuid():N}.key");

        try
        {

            ArcanumSettings settings = new()
            {
                Host = new HostSettings
                {
                    Port = 5001,
                    Https = new HttpsSettings
                    {
                        Enabled = true,
                        Port = 5443,
                        CertificatePath = certificatePath,
                        PrivateKeyPath = missingKey,
                    },
                },
            };

            Result result = _validator.Validate(settings);

            Assert.True(result.IsFailure);

            Assert.Contains(result.Error.Details!, static e => e.Pointer == "host.https.privateKeyPath");

        }
        finally
        {

            File.Delete(certificatePath);

        }

    }

    [Fact]
    public void Validate_HttpsErrorsNeverIncludePassword()
    {

        ArcanumSettings settings = new()
        {
            Host = new HostSettings
            {
                Port = 5001,
                Https = new HttpsSettings
                {
                    Enabled = true,
                    Port = 5001,
                    CertificatePath = null,
                    CertificatePasswordEnvironmentVariable =
                        "ARCANUM_CERT_PASSWORD",
                },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.DoesNotContain(
            result.Error.Details!,
            static e => e.Detail.Contains(
                "ARCANUM_CERT_PASSWORD",
                StringComparison.Ordinal));

    }

    [Fact]
    public void Validate_MissingProviderType_DefaultsToOpenAICompatible_Succeeds()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Endpoint = "http://localhost:11434/v1",
                    Models = ["mistral:latest"],
                },
            ],
        };

        Assert.Equal(AiProviderKind.OpenAICompatible, settings.Providers![0].Type);

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_UndefinedProviderType_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "broken",
                    Type = (AiProviderKind)99,
                    Endpoint = "http://localhost:11434/v1",
                    Models = ["mistral:latest"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "providers[0].type");

    }

    [Fact]
    public void Validate_EmbeddingsEnabled_RequiresOpenAICompatibleProvider()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "http://localhost:11434/v1",
                    Models = ["nomic-embed-text"],
                },
            ],
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "ollama",
                    Model = "nomic-embed-text",
                },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void RejectObsoleteKeys_RootLlamaCpp_ReturnsMigrationError()
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:LlamaCpp:ServerExecutablePath"] = "/tmp/llama",
            })
            .Build();

        Result result = _validator.RejectObsoleteKeys(configuration);

        Assert.True(result.IsFailure);

        Assert.Equal("Configuration.ValidationFailed", result.Error.Code);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "llamaCpp");

        Assert.Contains(result.Error.Details!, static e => e.Detail.Contains("OpenAICompatible", StringComparison.Ordinal));

    }

    [Fact]
    public void RejectObsoleteKeys_RootCache_ReturnsMigrationError()
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Cache:Enabled"] = "true",
            })
            .Build();

        Result result = _validator.RejectObsoleteKeys(configuration);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "cache");

        Assert.Equal(
            ConfigurationValidator.ObsoleteCacheMigrationMessage,
            Assert.Single(result.Error.Details!, static e => e.Pointer == "cache").Detail);

    }

    [Fact]
    public void RejectObsoleteKeys_RemovedProviderAndCertificateFields_ReturnAllErrors()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Host:Https:CertificatePassword"] = "secret",
                ["Arcanum:Integrations:CommLink:WebhookUrl"] = "https://hooks.example.test/secret",
                ["Arcanum:Providers:0:ApiKey"] = "secret",
                ["Arcanum:Providers:0:Tokenization:Type"] = "unknownFallback",
                ["Arcanum:Providers:0:PromptCaching:ControlMode"] = "none",
                ["Arcanum:Providers:0:SupportsPromptCaching"] = "true",
                ["Arcanum:Providers:0:Models:0:Name"] = "model",
                ["Arcanum:Providers:0:Models:0:Tokenization:Type"] = "unknownFallback",
                ["Arcanum:Providers:0:Models:0:PromptCaching:ControlMode"] = "none",
            })
            .Build();

        Result result = _validator.RejectObsoleteKeys(configuration);

        Assert.True(result.IsFailure);
        string[] pointers = result.Error.Details!
            .Select(static error => error.Pointer)
            .ToArray();
        Assert.Contains("host.https.certificatePassword", pointers);
        Assert.Contains("integrations.commLink.webhookUrl", pointers);
        Assert.Contains("providers[0].apiKey", pointers);
        Assert.Contains("providers[0].tokenization", pointers);
        Assert.Contains("providers[0].promptCaching", pointers);
        Assert.Contains("providers[0].supportsPromptCaching", pointers);
        Assert.Contains("providers[0].models[0].tokenization", pointers);
        Assert.Contains("providers[0].models[0].promptCaching", pointers);
    }

    [Fact]
    public void RejectObsoleteKeys_GroupsNestedObsoleteAndUnknownPathsWhileAllowingDynamicKeys()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Host:Https:CertificatePassword"] = "secret",
                ["Arcanum:Host:Https:RetiredTlsSwitch"] = "true",
                ["Arcanum:Providers:0:Name"] = "provider",
                ["Arcanum:Providers:0:Models:0:Name"] = "model",
                ["Arcanum:Providers:0:Models:0:PromptCaching:ControlMode"] = "none",
                ["Arcanum:Providers:0:Models:0:UnknownCapability"] = "true",
                ["Arcanum:Cost:Pricing:ModelPricing:model-a:InputPer1M"] = "1",
                ["Arcanum:Cost:Pricing:ModelPricing:model-a:UnknownRate"] = "2",
                ["Arcanum:Cost:Pricing:ModelPricing:mistral:latest:InputPer1M"] = "1",
                ["Arcanum:Integrations:WorkspaceChecks:CustomProfiles:custom-build:ExecutableId"] = "dotnet",
                ["Arcanum:Integrations:WorkspaceChecks:CustomProfiles:custom-build:Kind"] = "build",
                ["Arcanum:Integrations:WorkspaceChecks:CustomProfiles:custom-build:Parser"] = "msBuild",
                ["Arcanum:Integrations:WorkspaceChecks:CustomProfiles:custom-build:FixedArguments:0"] = "build",
                ["Arcanum:Integrations:WorkspaceChecks:CustomProfiles:custom-build:Options:configuration:UnknownOption"] = "x",
                ["Arcanum:Daemon:Jobs:0:Name"] = "daily",
                ["Arcanum:Daemon:Jobs:0:UnexpectedSchedule"] = "midnight",
            })
            .Build();

        Result result = _validator.RejectObsoleteKeys(configuration);

        Assert.True(result.IsFailure);
        string[] pointers = result.Error.Details!
            .Select(static error => error.Pointer)
            .ToArray();
        Assert.Contains("host.https.certificatePassword", pointers);
        Assert.Contains("host.https.RetiredTlsSwitch", pointers);
        Assert.Contains("providers[0].models[0].promptCaching", pointers);
        Assert.Contains("providers[0].models[0].UnknownCapability", pointers);
        Assert.Contains("cost.pricing.modelPricing[model-a].UnknownRate", pointers);
        Assert.Contains(
            "integrations.workspaceChecks.customProfiles[custom-build].options[configuration].UnknownOption",
            pointers);
        Assert.Contains("daemon.jobs[0].UnexpectedSchedule", pointers);
        Assert.DoesNotContain(
            pointers,
            static pointer => pointer is "cost.pricing.modelPricing[model-a]"
                or "cost.pricing.modelPricing[mistral:latest]"
                or "integrations.workspaceChecks.customProfiles[custom-build]"
                or "integrations.workspaceChecks.customProfiles[custom-build].options[configuration]");
        Assert.DoesNotContain(
            pointers,
            static pointer => pointer.StartsWith(
                "cost.pricing.modelPricing[mistral",
                StringComparison.Ordinal));
    }

    [Fact]
    public void RejectObsoleteKeys_RootModerations_ReturnsMigrationError()
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Moderations:Enabled"] = "true",
            })
            .Build();

        Result result = _validator.RejectObsoleteKeys(configuration);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "moderations");

        Assert.Contains(result.Error.Details!, static e => e.Detail.Contains("501", StringComparison.Ordinal));

    }

    [Fact]
    public void RejectObsoleteKeys_ProviderLlamaCppAndModelMap_ReturnsMigrationErrors()
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Providers:0:Name"] = "local",
                ["Arcanum:Providers:0:LlamaCpp:ModelMap:mistral"] = "https://example.test/m.gguf",
                ["Arcanum:Providers:1:Name"] = "mapped",
                ["Arcanum:Providers:1:ModelMap:mistral"] = "https://example.test/m.gguf",
            })
            .Build();

        Result result = _validator.RejectObsoleteKeys(configuration);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "providers[0].llamaCpp");

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "providers[1].modelMap");

    }

    [Fact]
    public void RejectObsoleteKeys_ProviderTypeLlamaCppServer_ReturnsMigrationError()
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Providers:0:Name"] = "local",
                ["Arcanum:Providers:0:Type"] = "LlamaCppServer",
                ["Arcanum:Providers:0:Models:0"] = "mistral",
            })
            .Build();

        Result result = _validator.RejectObsoleteKeys(configuration);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "providers[0].type");

        Assert.Contains(
            result.Error.Details!,
            static e => e.Detail.Contains(ConfigurationValidator.ObsoleteLlamaCppServerTypeMessage, StringComparison.Ordinal));

    }

    [Theory]
    [InlineData("""{"llamaCpp":{"serverExecutablePath":"/tmp/x"}}""")]
    [InlineData("""{"Cache":{"Enabled":true}}""")]
    [InlineData("""{"Providers":[{"name":"local","llamaCpp":{"modelMap":{"m":"https://x"}}}]}""")]
    [InlineData("""{"providers":[{"name":"local","ModelMap":{"m":"https://x"}}]}""")]
    [InlineData("""{"providers":[{"name":"local","type":"LlamaCppServer","models":["m"]}]}""")]
    [InlineData("""{"Providers":[{"Name":"local","Type":"llamacppserver","Models":["m"]}]}""")]
    [InlineData("""{"host":{"https":{"certificatePassword":"secret"}}}""")]
    [InlineData("""{"integrations":{"commLink":{"webhookUrl":"https://hooks.example.test/secret"}}}""")]
    [InlineData("""{"providers":[{"name":"old","apiKey":"secret","tokenization":{},"promptCaching":{},"supportsPromptCaching":true,"models":[{"name":"m","tokenization":{},"promptCaching":{}}]}]}""")]
    public void RejectObsoleteJsonKeys_ObsoleteShapes_ReturnMigrationError(string json)
    {

        using JsonDocument document = JsonDocument.Parse(json);

        Result result = _validator.RejectObsoleteJsonKeys(document.RootElement);

        Assert.True(result.IsFailure);

        Assert.Equal("Configuration.ValidationFailed", result.Error.Code);

        Assert.NotEmpty(result.Error.Details!);

    }

    [Fact]
    public void RejectObsoleteJsonKeys_GroupsNestedObsoleteAndUnknownPathsWhileAllowingDynamicKeys()
    {
        const string json =
            """
            {
              "host": {
                "https": {
                  "certificatePassword": "secret",
                  "retiredTlsSwitch": true
                }
              },
              "providers": [
                {
                  "name": "provider",
                  "models": [
                    {
                      "name": "model",
                      "promptCaching": { "controlMode": "none" },
                      "unknownCapability": true
                    }
                  ]
                }
              ],
              "cost": {
                "pricing": {
                  "modelPricing": {
                    "model-a": {
                      "inputPer1M": 1,
                      "unknownRate": 2
                    }
                  }
                }
              },
              "integrations": {
                "workspaceChecks": {
                  "customProfiles": {
                    "custom-build": {
                      "executableId": "dotnet",
                      "kind": "build",
                      "parser": "msBuild",
                      "fixedArguments": ["build"],
                      "options": {
                        "configuration": {
                          "unknownOption": "x"
                        }
                      }
                    }
                  }
                }
              },
              "daemon": {
                "jobs": [
                  {
                    "name": "daily",
                    "unexpectedSchedule": "midnight"
                  }
                ]
              }
            }
            """;
        using JsonDocument document = JsonDocument.Parse(json);

        Result result = _validator.RejectObsoleteJsonKeys(document.RootElement);

        Assert.True(result.IsFailure);
        string[] pointers = result.Error.Details!
            .Select(static error => error.Pointer)
            .ToArray();
        Assert.Contains("host.https.certificatePassword", pointers);
        Assert.Contains("host.https.retiredTlsSwitch", pointers);
        Assert.Contains("providers[0].models[0].promptCaching", pointers);
        Assert.Contains("providers[0].models[0].unknownCapability", pointers);
        Assert.Contains("cost.pricing.modelPricing[model-a].unknownRate", pointers);
        Assert.Contains(
            "integrations.workspaceChecks.customProfiles[custom-build].options[configuration].unknownOption",
            pointers);
        Assert.Contains("daemon.jobs[0].unexpectedSchedule", pointers);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"wrong-root\"")]
    [InlineData("""{"Arcanum":{"providers":[]}}""")]
    public void RejectObsoleteJsonKeys_WrongPutRoot_ReturnsFailure(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        Result result = _validator.RejectObsoleteJsonKeys(document.RootElement);

        Assert.True(result.IsFailure);
        Assert.Equal("Configuration.ValidationFailed", result.Error.Code);
    }

    [Fact]
    public void RejectObsoleteJsonKeys_OpenAICompatibleOllamaShape_Succeeds()
    {

        const string json =
            """
            {
              "providers": [
                {
                  "name": "Local Ollama",
                  "type": "OpenAICompatible",
                  "endpoint": "http://localhost:11434/v1",
                  "models": ["mistral:latest"]
                }
              ]
            }
            """;

        using JsonDocument document = JsonDocument.Parse(json);

        Result result = _validator.RejectObsoleteJsonKeys(document.RootElement);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void RejectObsoleteJsonKeys_RetainedDynamicCollectionKeys_Succeed()
    {
        const string json =
            """
            {
              "providers": [
                {
                  "name": "provider",
                  "models": [
                    {
                      "name": "model",
                      "reasoning": {
                        "controlSupport": "None",
                        "wireDialect": "Standard"
                      }
                    }
                  ]
                }
              ],
              "cost": {
                "pricing": {
                  "modelPricing": {
                    "model/a": {
                      "inputPer1M": 1,
                      "outputPer1M": 2,
                      "cachedPer1M": 0
                    }
                  }
                }
              },
              "integrations": {
                "workspaceChecks": {
                  "customProfiles": {
                    "custom-build": {
                      "executableId": "dotnet",
                      "kind": "build",
                      "parser": "msBuild",
                      "fixedArguments": ["build"],
                      "options": {
                        "configuration": {
                          "allowedValues": {
                            "release": ["--configuration", "Release"]
                          }
                        }
                      }
                    }
                  }
                }
              },
              "daemon": {
                "jobs": [
                  {
                    "name": "daily",
                    "intervalMinutes": 60,
                    "targetSpell": "daily-report",
                    "enabled": true
                  }
                ]
              }
            }
            """;
        using JsonDocument document = JsonDocument.Parse(json);

        Result result = _validator.RejectObsoleteJsonKeys(document.RootElement);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void CodingTools_defaults_retain_only_physical_result_shape_bounds()
    {
        CodingToolsSettings settings = ArcanumRuntimeDefaults.CodingTools;

        Assert.Null(
            typeof(WorkspaceCheckSettings).GetProperty("TimeoutSeconds"));

        Assert.Null(
            typeof(WorkspaceSearchSettings).GetProperty("MaxMatches"));

        Assert.Equal(
            settings.Search.MaxPreviewChars,
            ArcanumSettingClamps.WorkspaceSearchMaxPreviewChars(
                settings.Search.MaxPreviewChars));
        Assert.Equal(
            settings.Patch.MaxPatchBytes,
            ArcanumSettingClamps.WorkspacePatchMaxPatchBytes(settings.Patch.MaxPatchBytes));
    }

    [Fact]
    public void RuntimeDefaults_CodingToolValuesRemainWithinPhysicalClamps()
    {
        CodingToolsSettings defaults = ArcanumRuntimeDefaults.CodingTools;
        WorkspaceCheckSettings check = ArcanumRuntimeDefaults.WorkspaceChecks;

        Assert.Equal(
            defaults.Search.MaxPatternChars,
            ArcanumSettingClamps.WorkspaceSearchMaxPatternChars(defaults.Search.MaxPatternChars));
        Assert.Equal(
            defaults.Search.RegexTimeoutMilliseconds,
            ArcanumSettingClamps.WorkspaceSearchRegexTimeoutMilliseconds(
                defaults.Search.RegexTimeoutMilliseconds));
        Assert.Equal(
            defaults.Patch.MaxPatchBytes,
            ArcanumSettingClamps.WorkspacePatchMaxPatchBytes(defaults.Patch.MaxPatchBytes));
        Assert.Equal(
            defaults.Patch.RecoveryTimeoutMilliseconds,
            ArcanumSettingClamps.WorkspacePatchRecoveryTimeoutMilliseconds(
                defaults.Patch.RecoveryTimeoutMilliseconds));
        Assert.Equal(
            check.MaxDiagnostics,
            ArcanumSettingClamps.WorkspaceCheckMaxDiagnostics(check.MaxDiagnostics));
    }

    [Fact]
    public void Validate_CustomWorkspaceCheckProfiles_RejectsReservedIdsAndOpenExecutableReferences()
    {
        ArcanumSettings settings = new()
        {
            Integrations = new IntegrationSettings
            {
                WorkspaceChecks = new WorkspaceCheckIntegrationSettings
                {
                    CustomProfiles = new Dictionary<string, WorkspaceCheckProfileSettings>
                    {
                        ["dotnet-build"] = new()
                        {
                            ExecutableId = "shell",
                            Kind = WorkspaceCheckKind.Build,
                            Parser = WorkspaceCheckDiagnosticParserKind.MsBuild,
                            FixedArguments = ["build"],
                        },
                    },
                },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "integrations.workspaceChecks.customProfiles[dotnet-build]");
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer.EndsWith(".executableId", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CustomWorkspaceCheckProfiles_RejectsInvalidKindsTokensOptionsAndDuplicates()
    {
        Dictionary<string, WorkspaceCheckProfileSettings> profiles = new(StringComparer.Ordinal)
        {
            ["bad profile"] = new()
            {
                ExecutableId = "dotnet",
                Kind = (WorkspaceCheckKind)99,
                Parser = (WorkspaceCheckDiagnosticParserKind)99,
                FixedArguments = ["build", "danger.ps1"],
            },
            ["custom-test"] = new()
            {
                ExecutableId = "dotnet",
                Kind = WorkspaceCheckKind.Test,
                Parser = WorkspaceCheckDiagnosticParserKind.MsBuild,
                Target = "../escape.csproj",
                FixedArguments = ["build", "--configuration"],
                Options = new Dictionary<string, WorkspaceCheckProfileOptionSettings>
                {
                    ["configuration"] = new()
                    {
                        AllowedValues = new Dictionary<string, string[]>
                        {
                            ["debug"] = ["--configuration", "Debug"],
                            ["release"] = ["--configuration", "Release"],
                        },
                    },
                },
            },
            ["CUSTOM-TEST"] = new()
            {
                ExecutableId = "dotnet",
                Kind = WorkspaceCheckKind.Build,
                Parser = WorkspaceCheckDiagnosticParserKind.MsBuild,
                FixedArguments = ["build"],
            },
        };
        ArcanumSettings settings = new()
        {
            Integrations = new IntegrationSettings
            {
                WorkspaceChecks = new WorkspaceCheckIntegrationSettings
                {
                    CustomProfiles = profiles,
                },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "integrations.workspaceChecks.customProfiles[bad profile].kind");
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "integrations.workspaceChecks.customProfiles[bad profile].parser");
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer.EndsWith(".fixedArguments[1]", StringComparison.Ordinal));
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "integrations.workspaceChecks.customProfiles[custom-test].parser");
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "integrations.workspaceChecks.customProfiles[custom-test].target");
        Assert.Contains(
            result.Error.Details!,
            static e => e.Detail.Contains("duplicated case-insensitively", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CustomWorkspaceCheckProfiles_RejectsPhysicalCollectionLimit()
    {
        int maxProfiles = ArcanumRuntimeDefaults.WorkspaceChecks.MaxCustomProfiles;
        Dictionary<string, WorkspaceCheckProfileSettings> profiles =
            Enumerable.Range(0, maxProfiles + 1).ToDictionary(
                static index => $"custom-{index}",
                static _ => new WorkspaceCheckProfileSettings
                {
                    ExecutableId = WorkspaceCheckCatalogDefaults.DotNetExecutableId,
                    Kind = WorkspaceCheckKind.Build,
                    Parser = WorkspaceCheckDiagnosticParserKind.MsBuild,
                    FixedArguments = ["build"],
                },
                StringComparer.OrdinalIgnoreCase);
        ArcanumSettings settings = new()
        {
            Integrations = new IntegrationSettings
            {
                WorkspaceChecks = new WorkspaceCheckIntegrationSettings
                {
                    CustomProfiles = profiles,
                },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Error.Details!,
            static e => e.Pointer == "integrations.workspaceChecks.customProfiles");
    }

    [Fact]
    public void Validate_ConfiguredWorkspaceCheckExecutableInsideWorkspace_ReturnsFailure()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"arcanum-check-validator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        string executable = Path.Combine(workspace, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(executable, "not a real executable");

        try
        {
            ArcanumSettings settings = new()
            {
                Workspaces = new WorkspaceSettings { DefaultRoot = workspace },
                Integrations = new IntegrationSettings
                {
                    WorkspaceChecks = new WorkspaceCheckIntegrationSettings
                    {
                        ExecutableCatalog = new WorkspaceCheckExecutableCatalogSettings
                        {
                            DotNet = new WorkspaceCheckExecutableSettings { Path = executable },
                        },
                    },
                },
            };

            Result result = _validator.Validate(settings);

            Assert.True(result.IsFailure);
            Assert.Contains(
                result.Error.Details!,
                static e => e.Pointer == "integrations.workspaceChecks.executableCatalog.dotNet.path"
                    && e.Detail.Contains("outside the source workspace", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Validate_BlankDaemonJobName_ReturnsFailure()
    {

        ArcanumSettings settings = SettingsWithDaemonJobs(
            new UnseenServantJob { Name = "  ", TargetSpell = "daily-digest" });

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "daemon.jobs[0].name");

    }

    [Fact]
    public void Validate_DuplicateDaemonJobName_ReturnsFailure()
    {

        ArcanumSettings settings = SettingsWithDaemonJobs(
            new UnseenServantJob { Name = "digest", TargetSpell = "daily-digest" },
            new UnseenServantJob { Name = "Digest", TargetSpell = "weekly-digest" });

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "daemon.jobs[1].name");

    }

    [Fact]
    public void Validate_DistinctDaemonJobNames_ReturnsSuccess()
    {

        ArcanumSettings settings = SettingsWithDaemonJobs(
            new UnseenServantJob { Name = "digest", TargetSpell = "daily-digest" },
            new UnseenServantJob { Name = "sweep", TargetSpell = "weekly-digest" });

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_NullDaemonSection_DoesNotThrow()
    {

        ArcanumSettings settings = SettingsWithDaemonJobs();

        settings.Daemon = null!;

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    private static ArcanumSettings SettingsWithDaemonJobs(params UnseenServantJob[] jobs) =>
        new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = ["llama3"],
                },
            ],
            Daemon = new DaemonSettings { Jobs = [.. jobs] },
        };

    private static ArcanumSettings SettingsWithReasoning(ReasoningWireDialect? wireDialect, int? maxBudgetTokens) =>
        new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "reasoning-provider",
                    Type = AiProviderKind.OpenAICompatible,
                    Models =
                    [
                        new ModelEntry("reasoner")
                        {
                            Reasoning = wireDialect is null && maxBudgetTokens is null
                                ? null
                                : new ModelReasoningSettings
                                {
                                    WireDialect = wireDialect,
                                    MaxBudgetTokens = maxBudgetTokens,
                                },
                        },
                    ],
                },
            ],
        };

}
