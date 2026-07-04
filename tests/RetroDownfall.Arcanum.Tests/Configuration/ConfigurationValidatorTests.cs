using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ConfigurationValidatorTests
{

    private readonly ConfigurationValidator _validator = new();

    [Fact]
    public void Validate_ValidOllamaProvider_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.Ollama,
                    Models = ["llama3"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_OllamaProviderWithoutModels_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.Ollama,
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
    public void Validate_LlamaCppWithoutModelsOrMap_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "local",
                    Type = AiProviderKind.LlamaCppServer,
                    Models = [],
                    LlamaCpp = new ProviderLlamaCppSettings { ModelMap = new Dictionary<string, string>() },
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.NotNull(result.Error.Details);

        Assert.Contains(result.Error.Details!, static e => e.Detail.Contains("local", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(result.Error.Details!, static e => e.Detail.Contains("modelMap", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public void Validate_LlamaCppWithModelMapOnly_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "local",
                    Type = AiProviderKind.LlamaCppServer,
                    Models = [],
                    LlamaCpp = new ProviderLlamaCppSettings
                    {
                        ModelMap = new Dictionary<string, string>
                        {
                            ["tiny"] = "https://example.com/tiny.gguf",
                        },
                    },
                },
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
                    Type = AiProviderKind.Ollama,
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
                    Type = AiProviderKind.Ollama,
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
    public void Validate_DefaultModelWithTagMatch_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {
            DefaultModel = "llama3",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.Ollama,
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
                    Type = AiProviderKind.Ollama,
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
                    Type = AiProviderKind.Ollama,
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
                    Type = AiProviderKind.Ollama,
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
                    Type = AiProviderKind.Ollama,
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
                    Type = AiProviderKind.Ollama,
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
    public void Validate_LlamaCppWithModelsOnly_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [

                new ProviderSettings
                {

                    Name = "local",

                    Type = AiProviderKind.LlamaCppServer,

                    Models = ["tiny"],

                },

            ],

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_FastModelWithTagMatch_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {

            FastModel = "llama3",

            Providers =
            [

                new ProviderSettings
                {

                    Name = "ollama",

                    Type = AiProviderKind.Ollama,

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

                    Type = AiProviderKind.Ollama,

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

                    Type = AiProviderKind.Ollama,

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

                        Type = AiProviderKind.Ollama,

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

                    Type = AiProviderKind.Ollama,

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

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] },

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

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] },

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

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = null! },

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

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] },

            ],

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_LlamaPortSumExceeds65535_ReturnsFailure()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] },

            ],

            LlamaCpp = new LlamaCppSettings { PortStart = 40_000, PortRange = 30_000 },

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static e => e.Pointer == "llamaCpp.portRange");

    }

    [Fact]
    public void Validate_LlamaPortSumWithinRange_ReturnsSuccess()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] },

            ],

            LlamaCpp = new LlamaCppSettings { PortStart = 50_000, PortRange = 1_000 },

        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_EmbeddingsDisabled_ReturnsSuccess_WithNoProviderOrModel()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] }],
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
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] }],
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
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] }],
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
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] }],
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
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] }],
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
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] }],
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
            Providers = [new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] }],
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

}
