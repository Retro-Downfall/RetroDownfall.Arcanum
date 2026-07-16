using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Core.Primitives;


namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class ArcanumErrorMapper
{

    public static int ResolveStatusCode(string errorCode) =>
        errorCode switch
        {
            ErrorCodes.Validation.InvalidPrompt or ErrorCodes.Validation.AttachedFiles or ErrorCodes.Validation.InvalidBody or ErrorCodes.Validation.InvalidQuery or ErrorCodes.Validation.InvalidProviderType =>
                StatusCodes.Status400BadRequest,

            // FeatureDisabled means an operator turned Embeddings off in config, not that the caller
            // lacks permission — 503 (retry later / ask an operator to enable it) fits better than the
            // 403 this used to share with genuine access-control failures below.
            ErrorCodes.Hub.ToolLoop or ErrorCodes.Hub.Timeout or ErrorCodes.Api.TooManyConnections or ErrorCodes.Embeddings.ProviderUnavailable or ErrorCodes.Embeddings.FeatureDisabled =>
                StatusCodes.Status503ServiceUnavailable,

            ErrorCodes.Hub.Model =>
                StatusCodes.Status404NotFound,

            ErrorCodes.Spell.NotFound or ErrorCodes.Prompt.NotFound or ErrorCodes.Campaign.NotFound or ErrorCodes.Session.NotFound or ErrorCodes.Session.EntryNotFound or ErrorCodes.Grimoire.LoreNotFound or ErrorCodes.Apprentice.NotFound or ErrorCodes.Workspace.NotFound or ErrorCodes.Mcp.ServerNotFound or ErrorCodes.Daemon.NotFound or ErrorCodes.Intelligence.HumanPromptNotFound or ErrorCodes.ProvingGrounds.SpellNotFound or ErrorCodes.ProvingGrounds.PromptNotFound or ErrorCodes.Workspace.FileNotFound or ErrorCodes.Workspace.ReplacementNotFound or ErrorCodes.Saga.NotFound or ErrorCodes.Files.NotFound or ErrorCodes.Batches.NotFound or ErrorCodes.Batches.InputFileNotFound =>
                StatusCodes.Status404NotFound,

            ErrorCodes.Spell.PathNotAllowed or ErrorCodes.Campaign.PathNotAllowed or ErrorCodes.Workspace.PathNotAllowed or ErrorCodes.Workspace.FileWriteDisabled or ErrorCodes.Workspace.AccessDenied or ErrorCodes.WebBrowsing.SsrfBlocked =>
                StatusCodes.Status403Forbidden,

            ErrorCodes.Workspace.FileTooLarge or ErrorCodes.Scrying.ImageTooLarge or ErrorCodes.Files.TooLarge =>
                StatusCodes.Status413PayloadTooLarge,

            ErrorCodes.Scrying.VisionNotSupported or ErrorCodes.Scrying.TooManyImages or ErrorCodes.Scrying.UnsupportedMimeType or ErrorCodes.Files.InvalidMimeType or ErrorCodes.Batches.InvalidEndpoint or ErrorCodes.WebBrowsing.InvalidUrl or ErrorCodes.WebBrowsing.TooLarge or ErrorCodes.ClientTools.Disabled or ErrorCodes.ClientTools.TooMany or ErrorCodes.ClientTools.InvalidSchema or ErrorCodes.Guardrails.PiiDetected or ErrorCodes.Guardrails.Blocked =>
                StatusCodes.Status400BadRequest,

            ErrorCodes.Scrying.FeatureDisabled =>
                StatusCodes.Status403Forbidden,

            ErrorCodes.Apprentice.AlreadyRunning or ErrorCodes.Apprentice.Running or ErrorCodes.Apprentice.NotPaused or ErrorCodes.Apprentice.CannotReweave or ErrorCodes.Apprentice.NotEscalated or ErrorCodes.Apprentice.MaxReached or ErrorCodes.Apprentice.ConclaveDisabled or ErrorCodes.Apprentice.ConclaveDepthExceeded or ErrorCodes.Apprentice.ConclaveBreadthExceeded or ErrorCodes.Session.ForkDepthExceeded or ErrorCodes.Session.TooManyPinned =>
                StatusCodes.Status409Conflict,

            ErrorCodes.Campaign.InvalidPath or ErrorCodes.Campaign.MaxReached or ErrorCodes.Workspace.NameEmpty or ErrorCodes.Spell.NoWorkspace or ErrorCodes.Spell.InvalidWorkspace or ErrorCodes.Prompt.CodexPathNotContained or ErrorCodes.Prompt.InvalidRequest or ErrorCodes.Mcp.AmbiguousServer or ErrorCodes.Mcp.MissingWorkspace or ErrorCodes.Apprentice.Disabled or ErrorCodes.Apprentice.InvalidGuidance or ErrorCodes.Apprentice.InvalidPlan or ErrorCodes.Apprentice.InvalidGoal or ErrorCodes.Apprentice.InvalidWorkspace or ErrorCodes.Apprentice.PendingQueueFull or ErrorCodes.ProvingGrounds.InvalidTrial or ErrorCodes.ProvingGrounds.TooManyInquisitors or ErrorCodes.ProvingGrounds.WorkspaceNotAllowed or ErrorCodes.Security.BlockedOutboundUrl or ErrorCodes.Security.IdempotencyKeyTooLong or ErrorCodes.Session.Archived or ErrorCodes.Session.InvalidStatus or ErrorCodes.Session.TooManyEntries or ErrorCodes.Session.EntryTooLarge or ErrorCodes.Session.EmptyContent or ErrorCodes.Session.MemoryManagementDisabled or ErrorCodes.Spell.InvalidName or ErrorCodes.Spell.NameCollision or ErrorCodes.Spell.BuiltinReadOnly or ErrorCodes.Spell.DuplicateVersion or ErrorCodes.Spell.InvalidVersion or ErrorCodes.Prompt.DuplicateVersion or ErrorCodes.Prompt.InvalidName or ErrorCodes.Prompt.InvalidVersion or ErrorCodes.Workspace.DirectoryNotEmpty or ErrorCodes.Workspace.ReplacementAmbiguous or ErrorCodes.Workspace.PathIsDirectory or ErrorCodes.Workspace.PathIsFile or ErrorCodes.Workspace.SymbolicLinkEscape or ErrorCodes.Workspace.PathTraversal or ErrorCodes.Saga.NotEmpty or ErrorCodes.StructuredOutput.ValidationFailed or ErrorCodes.StructuredOutput.SchemaInvalid or ErrorCodes.Embeddings.ConfirmationRequired =>
                StatusCodes.Status400BadRequest,

            ErrorCodes.ProvingGrounds.InferenceFailed or ErrorCodes.Workspace.WriteFailed or ErrorCodes.Workspace.DeleteFailed or ErrorCodes.Saga.SearchFailed =>
                StatusCodes.Status500InternalServerError,

            ErrorCodes.CommLink.Suppressed =>
                StatusCodes.Status502BadGateway,

            ErrorCodes.RateLimit.TooManyRequests =>
                StatusCodes.Status429TooManyRequests,

            ErrorCodes.Budget.Exceeded =>
                StatusCodes.Status429TooManyRequests,

            ErrorCodes.Security.MissingApiKey =>
                StatusCodes.Status401Unauthorized,

            ErrorCodes.Connection.Timeout or ErrorCodes.WebBrowsing.Timeout =>
                StatusCodes.Status504GatewayTimeout,

            ErrorCodes.Connection.Unreachable =>
                StatusCodes.Status503ServiceUnavailable,

            ErrorCodes.Sending.AgentUnreachable or ErrorCodes.Sending.AgentCardInvalid =>
                StatusCodes.Status502BadGateway,

            ErrorCodes.Sending.TaskTimeout =>
                StatusCodes.Status504GatewayTimeout,

            ErrorCodes.Sending.TaskRejected =>
                StatusCodes.Status400BadRequest,

            ErrorCodes.Sending.Disabled or ErrorCodes.Sending.AgentNotAllowed =>
                StatusCodes.Status403Forbidden,

            ErrorCodes.Sending.MaxTasksReached =>
                StatusCodes.Status429TooManyRequests,

            _ =>
                StatusCodes.Status500InternalServerError,
        };

    /// <summary>
    /// Maps an error code, preserving endpoints that previously treated unmapped codes as 400 Bad Request.
    /// Explicit 500 mappings (e.g. inference failures) are not downgraded.
    /// </summary>
    public static int ResolveStatusCodeDefaultBadRequest(string errorCode)
    {

        if (errorCode is ErrorCodes.ProvingGrounds.InferenceFailed
            or ErrorCodes.Workspace.WriteFailed
            or ErrorCodes.Workspace.DeleteFailed
            or ErrorCodes.Saga.SearchFailed
            or ErrorCodes.Hub.Error)
        {

            return ResolveStatusCode(errorCode);

        }

        int mapped = ResolveStatusCode(errorCode);

        return mapped == StatusCodes.Status500InternalServerError
            ? StatusCodes.Status400BadRequest
            : mapped;

    }

}
