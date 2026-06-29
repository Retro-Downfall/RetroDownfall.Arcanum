using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Core.Primitives;


namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class ArcanumErrorMapper
{

    public static int ResolveStatusCode(string errorCode) =>
        errorCode switch
        {
            ErrorCodes.Validation.InvalidPrompt or ErrorCodes.Validation.AttachedFiles or ErrorCodes.Validation.InvalidBody =>
                StatusCodes.Status400BadRequest,

            ErrorCodes.Hub.ToolLoop or ErrorCodes.Hub.Timeout or ErrorCodes.Api.TooManyConnections =>
                StatusCodes.Status503ServiceUnavailable,

            ErrorCodes.Hub.Model or ErrorCodes.Ollama.Pull or ErrorCodes.Ollama.ListModels =>
                StatusCodes.Status404NotFound,

            ErrorCodes.Spell.NotFound or ErrorCodes.Prompt.NotFound or ErrorCodes.Campaign.NotFound or ErrorCodes.Session.NotFound or ErrorCodes.Grimoire.LoreNotFound or ErrorCodes.Apprentice.NotFound or ErrorCodes.Workspace.NotFound or ErrorCodes.Mcp.ServerNotFound or ErrorCodes.Daemon.NotFound or ErrorCodes.Intelligence.HumanPromptNotFound or ErrorCodes.ProvingGrounds.SpellNotFound or ErrorCodes.ProvingGrounds.PromptNotFound =>
                StatusCodes.Status404NotFound,

            ErrorCodes.Spell.PathNotAllowed or ErrorCodes.Campaign.PathNotAllowed or ErrorCodes.Workspace.PathNotAllowed =>
                StatusCodes.Status403Forbidden,

            ErrorCodes.Apprentice.AlreadyRunning or ErrorCodes.Apprentice.Running or ErrorCodes.Apprentice.NotPaused or ErrorCodes.Apprentice.CannotReweave or ErrorCodes.Apprentice.NotEscalated or ErrorCodes.Apprentice.MaxReached or ErrorCodes.Apprentice.ConclaveDisabled or ErrorCodes.Apprentice.ConclaveDepthExceeded or ErrorCodes.Apprentice.ConclaveBreadthExceeded =>
                StatusCodes.Status409Conflict,

            ErrorCodes.Campaign.InvalidPath or ErrorCodes.Campaign.MaxReached or ErrorCodes.Workspace.NameEmpty or ErrorCodes.Spell.NoWorkspace or ErrorCodes.Spell.InvalidWorkspace or ErrorCodes.Prompt.CodexPathNotContained or ErrorCodes.Mcp.AmbiguousServer or ErrorCodes.Mcp.MissingWorkspace or ErrorCodes.Llama.ModelNotCached or ErrorCodes.Apprentice.Disabled or ErrorCodes.Apprentice.InvalidGuidance or ErrorCodes.Apprentice.InvalidPlan or ErrorCodes.Apprentice.InvalidGoal or ErrorCodes.Apprentice.InvalidWorkspace or ErrorCodes.Apprentice.PendingQueueFull or ErrorCodes.ProvingGrounds.InvalidTrial or ErrorCodes.ProvingGrounds.TooManyInquisitors or ErrorCodes.ProvingGrounds.WorkspaceNotAllowed or ErrorCodes.Security.BlockedOutboundUrl =>
                StatusCodes.Status400BadRequest,

            ErrorCodes.ProvingGrounds.InferenceFailed =>
                StatusCodes.Status500InternalServerError,

            ErrorCodes.CommLink.Suppressed =>
                StatusCodes.Status502BadGateway,

            ErrorCodes.RateLimit.TooManyRequests =>
                StatusCodes.Status429TooManyRequests,

            ErrorCodes.Security.MissingApiKey =>
                StatusCodes.Status401Unauthorized,

            ErrorCodes.Connection.Timeout =>
                StatusCodes.Status504GatewayTimeout,

            _ =>
                StatusCodes.Status500InternalServerError,
        };

    /// <summary>
    /// Maps an error code, preserving endpoints that previously treated unmapped codes as 400 Bad Request.
    /// Explicit 500 mappings (e.g. inference failures) are not downgraded.
    /// </summary>
    public static int ResolveStatusCodeDefaultBadRequest(string errorCode)
    {

        if (errorCode is ErrorCodes.ProvingGrounds.InferenceFailed or ErrorCodes.Hub.Error or ErrorCodes.Ollama.Error)
        {

            return ResolveStatusCode(errorCode);

        }

        int mapped = ResolveStatusCode(errorCode);

        return mapped == StatusCodes.Status500InternalServerError
            ? StatusCodes.Status400BadRequest
            : mapped;

    }

}
