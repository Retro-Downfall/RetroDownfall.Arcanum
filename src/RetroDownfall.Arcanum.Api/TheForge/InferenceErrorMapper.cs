using Microsoft.AspNetCore.Http;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class InferenceErrorMapper
{

    public static int ResolveStatusCode(string errorCode) =>
        errorCode switch
        {
            "Validation.InvalidPrompt" or "Validation.AttachedFiles" or "Validation.InvalidBody" =>
                StatusCodes.Status400BadRequest,
            "Hub.ToolLoop" =>
                StatusCodes.Status503ServiceUnavailable,
            "Hub.Timeout" =>
                StatusCodes.Status503ServiceUnavailable,
            "Hub.Model" or "Ollama.Pull" or "Ollama.ListModels" =>
                StatusCodes.Status404NotFound,
            "Spell.NotFound" or "Prompt.NotFound" =>
                StatusCodes.Status404NotFound,
            "Spell.PathNotAllowed" =>
                StatusCodes.Status403Forbidden,
            _ =>
                StatusCodes.Status500InternalServerError,
        };

}
