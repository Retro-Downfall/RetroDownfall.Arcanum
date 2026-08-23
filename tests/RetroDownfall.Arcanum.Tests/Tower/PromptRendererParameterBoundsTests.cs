using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.TheForge;

namespace RetroDownfall.Arcanum.Tests.Tower;

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
        Prompt prompt = new()
        {
            Template = "Hello {{name}}",
            ParameterSchema = NameSchema,
        };
        int maxParameterValueChars = ArcanumSettingClamps.MaxParameterValueChars(
            ArcanumRuntimeDefaults.Prompts.MaxParameterValueChars);

        PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(
            new ZeroTokenCounter(),
            new ArcanumSettings());

        Result<PromptRenderResultDto> result = renderer.Render(
            prompt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = new string('x', maxParameterValueChars + 1),
            });

        Assert.True(result.IsFailure);

        Assert.Equal("Prompt.ParameterValueTooLong", result.Error.Code);

    }

    private sealed class ZeroTokenCounter : IManaMeter
    {

        public int CountTokens(string text) => 0;

    }

}

