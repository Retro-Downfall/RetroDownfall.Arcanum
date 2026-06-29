using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Core.Primitives;


namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class InferenceErrorMapper
{

    public static int ResolveStatusCode(string errorCode) =>
        errorCode switch
        {
            ErrorCodes.Validation.InvalidPrompt or ErrorCodes.Validation.AttachedFiles or ErrorCodes.Validation.InvalidBody =>
                StatusCodes.Status400BadRequest,
            ErrorCodes.Hub.ToolLoop =>
                StatusCodes.Status503ServiceUnavailable,
            ErrorCodes.Hub.Timeout =>
                StatusCodes.Status503ServiceUnavailable,
            ErrorCodes.Hub.Model or ErrorCodes.Ollama.Pull or ErrorCodes.Ollama.ListModels =>
                StatusCodes.Status404NotFound,
            ErrorCodes.Spell.NotFound or ErrorCodes.Prompt.NotFound or ErrorCodes.Campaign.NotFound =>
                StatusCodes.Status404NotFound,
            ErrorCodes.Spell.PathNotAllowed =>
                StatusCodes.Status403Forbidden,
            _ =>
                StatusCodes.Status500InternalServerError,
        };

}
