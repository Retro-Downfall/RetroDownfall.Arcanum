using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.TheForge;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

public sealed class InferenceErrorMapperTests
{

    [Theory]
    [InlineData("Validation.InvalidPrompt", StatusCodes.Status400BadRequest)]
    [InlineData("Validation.AttachedFiles", StatusCodes.Status400BadRequest)]
    [InlineData("Validation.InvalidBody", StatusCodes.Status400BadRequest)]
    [InlineData("Hub.ToolLoop", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("Hub.Timeout", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("Hub.Model", StatusCodes.Status404NotFound)]
    [InlineData("Ollama.Pull", StatusCodes.Status404NotFound)]
    [InlineData("Ollama.ListModels", StatusCodes.Status404NotFound)]
    [InlineData("Spell.NotFound", StatusCodes.Status404NotFound)]
    [InlineData("Prompt.NotFound", StatusCodes.Status404NotFound)]
    [InlineData("Campaign.NotFound", StatusCodes.Status404NotFound)]
    [InlineData("Spell.PathNotAllowed", StatusCodes.Status403Forbidden)]
    [InlineData("Hub.Error", StatusCodes.Status500InternalServerError)]
    [InlineData("Unknown.Code", StatusCodes.Status500InternalServerError)]
    public void ResolveStatusCode_MapsExpectedValue(string code, int expected)
    {
        int actual = InferenceErrorMapper.ResolveStatusCode(code);

        Assert.Equal(expected, actual);
    }

}
