using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.TheForge;

namespace RetroDownfall.Arcanum.Tests.Tower;

public sealed class PromptRendererTests
{

    private static readonly string NameSchema = """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" }
          },
          "required": ["name"]
        }
        """;

    [Fact]
    public void Render_ValidParameters_SubstitutesAndCountsTokens()
    {

        Prompt prompt = new()
        {
            Template = "Hello {{name}}",
            ParameterSchema = NameSchema,
        };

        CountingTokenCounter counter = new();

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(counter);

        Result<PromptRenderResultDto> result = renderer.Render(
            prompt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = "Arcanum",
            });

        Assert.True(result.IsSuccess);

        Assert.Equal("Hello \"Arcanum\"", result.Value!.RenderedText);

        Assert.Equal(42, result.Value.TokenCount);

    }

    [Fact]
    public void Render_NullParameters_TreatedAsEmptyDictionary()
    {

        Prompt prompt = new()
        {
            Template = "Static prompt",
            ParameterSchema = null,
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Result<PromptRenderResultDto> result = renderer.Render(prompt, parameters: null);

        Assert.True(result.IsSuccess);

        Assert.Equal("Static prompt", result.Value!.RenderedText);

    }

    [Fact]
    public void Render_NoSchemaWithParameters_FailsUnknownParameter()
    {

        Prompt prompt = new()
        {
            Template = "Hello",
            ParameterSchema = null,
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Result<PromptRenderResultDto> result = renderer.Render(
            prompt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["extra"] = "value",
            });

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.UnknownParameter", result.Error.Code);

    }

    [Fact]
    public void Render_UnknownParameter_Fails()
    {

        Prompt prompt = new()
        {
            Template = "Hello {{name}}",
            ParameterSchema = NameSchema,
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Result<PromptRenderResultDto> result = renderer.Render(
            prompt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = "ok",
                ["evil"] = "nope",
            });

        Assert.True(result.IsFailure);

        Assert.Contains("evil", result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void Render_MissingRequiredParameter_FailsWithMissingParameterCode()
    {

        Prompt prompt = new()
        {
            Template = "Hello {{name}}",
            ParameterSchema = NameSchema,
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Result<PromptRenderResultDto> result = renderer.Render(prompt, new Dictionary<string, string>());

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.MissingParameter", result.Error.Code);

    }

    [Fact]
    public void Render_InvalidSchemaJson_FailsInvalidParameterSchema()
    {

        Prompt prompt = new()
        {
            Template = "Hello",
            ParameterSchema = "{not-json",
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Result<PromptRenderResultDto> result = renderer.Render(prompt, new Dictionary<string, string>());

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.InvalidParameterSchema", result.Error.Code);

    }

    [Fact]
    public void Render_SchemaWithoutPropertiesObject_FailsInvalidParameterSchema()
    {

        Prompt prompt = new()
        {
            Template = "Hello",
            ParameterSchema = """{"type":"object"}""",
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Result<PromptRenderResultDto> result = renderer.Render(prompt, new Dictionary<string, string>());

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.InvalidParameterSchema", result.Error.Code);

    }

    [Fact]
    public void Render_SchemaRootNotObject_AllowsParametersWithoutValidation()
    {

        Prompt prompt = new()
        {
            Template = "Value: {{value}}",
            ParameterSchema = """["unexpected"]""",
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Result<PromptRenderResultDto> result = renderer.Render(
            prompt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["value"] = "free-form",
            });

        Assert.True(result.IsSuccess);

        Assert.Equal("Value: \"free-form\"", result.Value!.RenderedText);

    }

    [Fact]
    public void Render_UnmatchedPlaceholder_LeavesPlaceholderIntact()
    {

        Prompt prompt = new()
        {
            Template = "Hello {{name}} and {{other}}",
            ParameterSchema = NameSchema,
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Result<PromptRenderResultDto> result = renderer.Render(
            prompt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = "world",
            });

        Assert.True(result.IsSuccess);

        Assert.Contains("{{other}}", result.Value!.RenderedText, StringComparison.Ordinal);

    }

    [Fact]
    public void ResolveDefaultParameters_ValidDefaults_ReturnsValidatedDictionary()
    {

        Prompt prompt = new()
        {
            ParameterSchema = NameSchema,
            DefaultParameters = """{"name":"default-user"}""",
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Result<Dictionary<string, string>> result = renderer.ResolveDefaultParameters(prompt);

        Assert.True(result.IsSuccess);

        Assert.Equal("default-user", result.Value!["name"]);

    }

    [Fact]
    public void ResolveDefaultParameters_MissingRequiredDefault_FailsRequiredParameterMissing()
    {

        Prompt prompt = new()
        {
            ParameterSchema = NameSchema,
            DefaultParameters = "{}",
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Result<Dictionary<string, string>> result = renderer.ResolveDefaultParameters(prompt);

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.RequiredParameterMissing", result.Error.Code);

    }

    [Fact]
    public void ResolveDefaultParameters_NoDefaultsAndNoSchema_ReturnsEmptyDictionary()
    {

        Prompt prompt = new()
        {
            ParameterSchema = null,
            DefaultParameters = null,
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Result<Dictionary<string, string>> result = renderer.ResolveDefaultParameters(prompt);

        Assert.True(result.IsSuccess);

        Assert.Empty(result.Value!);

    }

    private sealed class ZeroTokenCounter : IManaMeter
    {

        public int CountTokens(string text) => 0;

    }

    private sealed class CountingTokenCounter : IManaMeter
    {

        public int CountTokens(string text) => 42;

    }

}
