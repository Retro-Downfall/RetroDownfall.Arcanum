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

        Assert.Contains(result.Error.Details!, static e => e.Detail.Contains("Endpoint", StringComparison.Ordinal));

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
    public void Validate_MaxJsonRpcLineBytesBelowToolOutputCap_ReturnsFailure()
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
            Intelligence = new IntelligenceSettings
            {
                ToolOutputCapBytes = 2_097_152,
            },
            Mcp = new McpSettings
            {
                MaxJsonRpcLineBytes = 1_048_576,
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.NotNull(result.Error.Details);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "mcp.maxJsonRpcLineBytes");

    }

    [Fact]
    public void Validate_MaxJsonRpcLineBytesAtLeastToolOutputCap_ReturnsSuccess()
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
            Intelligence = new IntelligenceSettings
            {
                ToolOutputCapBytes = 1_048_576,
            },
            Mcp = new McpSettings
            {
                MaxJsonRpcLineBytes = 2_228_224,
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_RequestTimeoutBelowExecuteCommandTimeout_ReturnsFailure()
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
            Intelligence = new IntelligenceSettings
            {
                ExecuteCommandTimeoutSeconds = 120,
            },
            Mcp = new McpSettings
            {
                RequestTimeoutSeconds = 60,
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.NotNull(result.Error.Details);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "mcp.requestTimeoutSeconds");

    }

    [Fact]
    public void Validate_RequestTimeoutAtLeastExecuteCommandTimeout_ReturnsSuccess()
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
            Intelligence = new IntelligenceSettings
            {
                ExecuteCommandTimeoutSeconds = 30,
            },
            Mcp = new McpSettings
            {
                RequestTimeoutSeconds = 60,
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

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

            Campaigns = new CampaignsSettings
            {

                AllowedRoots = ["relative/path"],

            },

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "campaigns.allowedRoots");

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

            Spells = new SpellSettings
            {

                AllowedWorkspaceRoots = [Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())],

            },

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "spells.allowedWorkspaceRoots");

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

                Perception = new PerceptionSettings
                {

                    AllowedWorkspaceRoots = [tempDir],

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

            Host = new HostSettings
            {

                Workspace = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()),

            },

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "host.workspace");

    }

    [Fact]
    public void Validate_NullIntelligence_DoesNotThrow()
    {

        ArcanumSettings settings = new()
        {

            Intelligence = null!,

            Providers =
            [

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] },

            ],

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_NullMcp_DoesNotThrow()
    {

        ArcanumSettings settings = new()
        {

            Mcp = null!,

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
    public void Validate_NullPathAllowlistSubObjects_DoesNotThrow()
    {

        ArcanumSettings settings = new()
        {

            Campaigns = null!,

            Spells = null!,

            Perception = null!,

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
            Embeddings = new EmbeddingSettings { Enabled = false },
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
            Embeddings = new EmbeddingSettings { Enabled = true, Model = "nomic-embed-text" },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "embeddings.provider");

    }

    [Fact]
    public void Validate_EmbeddingsEnabledWithoutModel_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "ollama" },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "embeddings.model");

    }

    [Fact]
    public void Validate_EmbeddingsEnabledWithUnknownProvider_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "does-not-exist", Model = "nomic-embed-text" },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "embeddings.provider");

        Assert.Contains(result.Error.Details!, static e => e.Detail.Contains("does-not-exist", StringComparison.Ordinal));

    }

    [Fact]
    public void Validate_EmbeddingsEnabledWithValidProviderAndModel_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "ollama", Model = "nomic-embed-text" },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Theory]
    [InlineData(nameof(EmbeddingSettings.SessionSearchEnabled), "embeddings.sessionSearchEnabled")]
    [InlineData(nameof(EmbeddingSettings.CodebaseRetrievalEnabled), "embeddings.codebaseRetrievalEnabled")]
    [InlineData(nameof(EmbeddingSettings.SagaEnabled), "embeddings.sagaEnabled")]
    [InlineData(nameof(EmbeddingSettings.SemanticSpellRoutingEnabled), "embeddings.semanticSpellRoutingEnabled")]
    public void Validate_FeatureFlagEnabledWithoutEmbeddingsEnabled_ReturnsFailure(string flagName, string expectedPointer)
    {

        EmbeddingSettings embeddings = flagName switch
        {
            nameof(EmbeddingSettings.SessionSearchEnabled) => new EmbeddingSettings { SessionSearchEnabled = true },
            nameof(EmbeddingSettings.CodebaseRetrievalEnabled) => new EmbeddingSettings { CodebaseRetrievalEnabled = true },
            nameof(EmbeddingSettings.SagaEnabled) => new EmbeddingSettings { SagaEnabled = true },
            nameof(EmbeddingSettings.SemanticSpellRoutingEnabled) => new EmbeddingSettings { SemanticSpellRoutingEnabled = true },
            _ => throw new ArgumentOutOfRangeException(nameof(flagName)),
        };

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Embeddings = embeddings,
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, e => e.Pointer == expectedPointer);

    }

    [Fact]
    public void Validate_AllFeatureFlagsEnabledWithEmbeddingsEnabled_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Embeddings = new EmbeddingSettings
            {
                Enabled = true,
                Provider = "ollama",
                Model = "nomic-embed-text",
                SessionSearchEnabled = true,
                CodebaseRetrievalEnabled = true,
                SagaEnabled = true,
                SemanticSpellRoutingEnabled = true,
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
    public void Validate_ScryingMaxImageBytesOutOfClampRange_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Scrying = new ScryingSettings { MaxImageBytes = 10 },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "scrying.maxImageBytes");

    }

    [Fact]
    public void Validate_ScryingMaxImagesPerRequestOutOfClampRange_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Scrying = new ScryingSettings { MaxImagesPerRequest = 0 },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "scrying.maxImagesPerRequest");

    }

    [Fact]
    public void Validate_ScryingEnabledWithEmptyAllowedMimeTypes_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Scrying = new ScryingSettings { Enabled = true, AllowedMimeTypes = [] },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "scrying.allowedMimeTypes");

    }

    [Fact]
    public void Validate_ScryingDisabledWithEmptyAllowedMimeTypes_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] }],
            Scrying = new ScryingSettings { Enabled = false, AllowedMimeTypes = [] },
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
                        CertificatePassword = "secret",
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
                    CertificatePassword = "top-secret-password",
                },
            },
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.DoesNotContain(result.Error.Details!, static e => e.Detail.Contains("top-secret-password", StringComparison.Ordinal));

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
            Embeddings = new EmbeddingSettings
            {
                Enabled = true,
                Provider = "ollama",
                Model = "nomic-embed-text",
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
    public void RejectObsoleteJsonKeys_ObsoleteShapes_ReturnMigrationError(string json)
    {

        using JsonDocument document = JsonDocument.Parse(json);

        Result result = _validator.RejectObsoleteJsonKeys(document.RootElement);

        Assert.True(result.IsFailure);

        Assert.Equal("Configuration.ValidationFailed", result.Error.Code);

        Assert.NotEmpty(result.Error.Details!);

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

}
