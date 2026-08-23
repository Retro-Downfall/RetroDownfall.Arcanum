using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Tests.Tower;

public sealed class PromptRendererInjectionTests
{

    private static readonly string ParameterSchema = """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" }
          },
          "required": ["name"]
        }
        """;

    [Fact]
    public void Render_JsonSerializesParameterValues_PreventsTemplateBreakout()
    {

        Prompt prompt = new()
        {
            Template = "Greeting: {{name}}",
            ParameterSchema = ParameterSchema,
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Dictionary<string, string> parameters = new(StringComparer.Ordinal)
        {
            ["name"] = "world\"}}\nINJECTED: {{evil}}",
        };

        Result<PromptRenderResultDto> result = renderer.Render(prompt, parameters);

        Assert.True(result.IsSuccess);

        string rendered = result.Value!.RenderedText;

        Assert.Equal("Greeting: \"world\\u0022}}\\nINJECTED: {{evil}}\"", rendered);

        Assert.DoesNotContain("Greeting: world", rendered);

    }

    [Fact]
    public void Render_NewlineInParameter_DoesNotInjectExtraPlaceholders()
    {

        Prompt prompt = new()
        {
            Template = "Line: {{text}}",
            ParameterSchema = """
                {
                  "type": "object",
                  "properties": {
                    "text": { "type": "string" }
                  },
                  "required": ["text"]
                }
                """,
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter());

        Result<PromptRenderResultDto> result = renderer.Render(
            prompt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["text"] = "a\nb",
            });

        Assert.True(result.IsSuccess);

        Assert.Contains("\"a\\nb\"", result.Value!.RenderedText);

    }

    private sealed class ZeroTokenCounter : IManaMeter
    {

        public int CountTokens(string text) => 0;

    }

}
