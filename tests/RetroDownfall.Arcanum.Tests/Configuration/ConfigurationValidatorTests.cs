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

}
