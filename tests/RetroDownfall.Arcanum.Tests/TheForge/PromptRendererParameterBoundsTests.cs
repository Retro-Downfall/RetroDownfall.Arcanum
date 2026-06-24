using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.TheForge;

namespace RetroDownfall.Arcanum.Tests.TheForge;

public sealed class PromptRendererParameterBoundsTests
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
    public void Render_RejectsParameterValueExceedingMaxChars()
    {

        ArcanumSettings settings = new()
        {
            Prompts = new PromptSettings { MaxParameterValueChars = 256 },
        };

        Prompt prompt = new()
        {
            Template = "Hello {{name}}",
            ParameterSchema = NameSchema,
        };

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new ZeroTokenCounter(), settings);

        Result<PromptRenderResultDto> result = renderer.Render(
            prompt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = new string('x', 257),
            });

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.ParameterValueTooLong", result.Error.Code);

    }

    private sealed class ZeroTokenCounter : IManaMeter
    {

        public int CountTokens(string text) => 0;

    }

}

