using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Primitives;

internal static class ArcanumErrorMapper
{

    public static int ResolveStatusCode(string errorCode) =>
        errorCode switch
        {
            ErrorCodes.Validation.InvalidPrompt
                or ErrorCodes.Validation.AttachedFiles
                or ErrorCodes.Validation.InvalidBody
                or ErrorCodes.Validation.InvalidFields
                or ErrorCodes.Validation.InvalidQuery
                or ErrorCodes.Validation.InvalidProviderType
                or ErrorCodes.Validation.InvalidReasoningEffort
                or ErrorCodes.Validation.InvalidReasoningOutput
                or ErrorCodes.Validation.ReasoningEffortAndBudgetMutuallyExclusive
                or ErrorCodes.Validation.InvalidReasoningBudget
                or ErrorCodes.Validation.UnsupportedReasoningControl
                or ErrorCodes.Validation.ReasoningBudgetExceedsModelLimit
                or ErrorCodes.Validation.UnsupportedReasoningOutput =>
                StatusCodes.Status400BadRequest,

            // FeatureDisabled means an operator turned Embeddings off in config, not that the caller
            // lacks permission — 503 (retry later / ask an operator to enable it) fits better than the
            // 403 this used to share with genuine access-control failures below.
            ErrorCodes.Api.TooManyConnections
                or ErrorCodes.Embeddings.ProviderUnavailable
                or ErrorCodes.Embeddings.FeatureDisabled
                // A curation write refused because nothing could embed is the same answer as the
                // provider being down, and the operator's action is the same: fix the substrate and
                // ask again. It is not the caller's request that was wrong.
                or ErrorCodes.Saga.EmbeddingUnavailable
                or ErrorCodes.Session.RestQueueFull
                or ErrorCodes.WebResearch.MissingCredential
                or ErrorCodes.WebResearch.AuthenticationOrCreditsFailed
                or ErrorCodes.WebResearch.QuotaExhausted
                or ErrorCodes.WebResearch.ProviderUnavailable
                or ErrorCodes.WebResearch.UnsupportedOperation
                or ErrorCodes.WebResearch.JavaScriptRenderingUnavailable =>
                StatusCodes.Status503ServiceUnavailable,

            ErrorCodes.Hub.ContextBudgetExceeded =>
                StatusCodes.Status429TooManyRequests,

            // The Covenant contract (§10.16). Every arm below is deliberate: an unmapped Covenant
            // code reaches the operator as a 500, which says "Arcanum broke" about a decision the
            // installation made on purpose and the caller can act on.
            ErrorCodes.Covenant.InvalidScope
                or ErrorCodes.Covenant.InvalidKey
                or ErrorCodes.Covenant.InvalidContent
                or ErrorCodes.Covenant.InvalidCursor =>
                StatusCodes.Status400BadRequest,

            // 403 rather than 409: a tainted Session is not a state the caller can retry past. The
            // installation will not export it in plaintext at all, and saying "conflict" would invite
            // exactly the retry loop that has no successful ending.
            ErrorCodes.Covenant.ForbiddenAuthority
                or ErrorCodes.Covenant.SensitiveEgressRequiresApproval
                or ErrorCodes.Covenant.PlaintextExportRefused =>
                StatusCodes.Status403Forbidden,

            ErrorCodes.Covenant.NotFound =>
                StatusCodes.Status404NotFound,

            // 410 rather than 404: the durable receipt proves this existed and was erased, and a
            // caller replaying a committed mutation deserves that distinction.
            ErrorCodes.Covenant.ArtifactErased =>
                StatusCodes.Status410Gone,

            ErrorCodes.Covenant.RevisionConflict
                or ErrorCodes.Covenant.LifecycleConflict
                or ErrorCodes.Covenant.StaleSnapshot
                or ErrorCodes.Covenant.StaleCursor
                or ErrorCodes.Covenant.CapacityExceeded
                or ErrorCodes.Covenant.SensitiveHistoryRequiresContext
                or ErrorCodes.Covenant.CampaignBindingConflict
                or ErrorCodes.Hub.SessionTurnBusy
                or ErrorCodes.Hub.SessionHistoryChanged
                or ErrorCodes.Hub.SessionTurnRestoredInterrupted
                or ErrorCodes.Session.CampaignBindingRequired
                or ErrorCodes.Campaign.PathIdentityRequired =>
                StatusCodes.Status409Conflict,

            // Saga curation's two state refusals. StaleContent says the stored content moved out from
            // under the caller's view of it; AlreadyRetired reaches here only from a correction, whose
            // text did not change because the memory is retired. Both are 409 because re-reading and
            // deciding again is the action, and 400 would wrongly blame the request's shape.
            //
            // Retiring a memory that is already retired, and reinstating one that is not, are not
            // refused and have no codes: the operator asked for a state and has it, and
            // ISagaCurationService reports which of the two happened instead of answering no.
            ErrorCodes.Saga.StaleContent
                or ErrorCodes.Saga.AlreadyRetired =>
                StatusCodes.Status409Conflict,

            ErrorCodes.Covenant.Unavailable
                or ErrorCodes.Covenant.OperatorAuthorityUnavailable
                or ErrorCodes.Covenant.HostToolsTransitionRequired
                or ErrorCodes.Covenant.MaintenanceFailed
                or ErrorCodes.Covenant.ManualArtifactErasureRequired
                or ErrorCodes.Covenant.ManualRecoveryRequired
                or ErrorCodes.Covenant.ErasureIncomplete
                or ErrorCodes.Covenant.IntegrityFailure =>
                StatusCodes.Status503ServiceUnavailable,

            ErrorCodes.Hub.Model =>
                StatusCodes.Status404NotFound,

            ErrorCodes.Spell.NotFound or ErrorCodes.Prompt.NotFound or ErrorCodes.Campaign.NotFound or ErrorCodes.Session.NotFound or ErrorCodes.Session.EntryNotFound or ErrorCodes.Grimoire.LoreNotFound or ErrorCodes.Lexicon.NotFound or ErrorCodes.Apprentice.NotFound or ErrorCodes.Workspace.NotFound or ErrorCodes.Mcp.ServerNotFound or ErrorCodes.Mcp.ToolNotFound or ErrorCodes.Daemon.NotFound or ErrorCodes.Intelligence.HumanPromptNotFound or ErrorCodes.ProvingGrounds.SpellNotFound or ErrorCodes.ProvingGrounds.PromptNotFound or ErrorCodes.Workspace.FileNotFound or ErrorCodes.Workspace.ReplacementNotFound or ErrorCodes.Saga.NotFound or ErrorCodes.Files.NotFound or ErrorCodes.Batches.NotFound or ErrorCodes.Batches.InputFileNotFound =>
                StatusCodes.Status404NotFound,

            ErrorCodes.Provider.NotFound =>
                StatusCodes.Status404NotFound,

            // The durable-operation routes (§8.23). StateConflict is 409 rather than 400: the caller's
            // request was well formed and the operation simply moved, so re-reading and deciding again
            // is the action, not fixing the request.
            ErrorCodes.Operation.NotFound =>
                StatusCodes.Status404NotFound,

            ErrorCodes.Operation.StateConflict =>
                StatusCodes.Status409Conflict,

            ErrorCodes.Operation.InvalidState or ErrorCodes.Codex.PathNotContained =>
                StatusCodes.Status400BadRequest,

            ErrorCodes.Perception.InvalidPath or ErrorCodes.Perception.InvalidSnapshot =>
                StatusCodes.Status400BadRequest,

            ErrorCodes.Perception.PathNotAllowed =>
                StatusCodes.Status403Forbidden,

            ErrorCodes.Attachment.NotFound or ErrorCodes.Attachment.SourceNotFound =>
                StatusCodes.Status404NotFound,

            ErrorCodes.Spell.PathNotAllowed or ErrorCodes.Campaign.PathNotAllowed or ErrorCodes.Workspace.PathNotAllowed or ErrorCodes.Workspace.FileWriteDisabled or ErrorCodes.Workspace.AccessDenied or ErrorCodes.WebBrowsing.SsrfBlocked or ErrorCodes.WebResearch.SsrfBlocked or ErrorCodes.Mcp.DiagnosticBlocked or ErrorCodes.Mcp.WorkspaceNotTrusted =>
                StatusCodes.Status403Forbidden,

            ErrorCodes.Workspace.FileTooLarge or ErrorCodes.Scrying.ImageTooLarge or ErrorCodes.Files.TooLarge or ErrorCodes.WebResearch.ResponseTooLarge or ErrorCodes.Validation.BodyTooLarge =>
                StatusCodes.Status413PayloadTooLarge,

            // Kestrel answers 408 when a request body arrives under its minimum data rate. The body is
            // not wrong, so this is not a 400: it is worth resending unchanged on a better connection.
            ErrorCodes.Validation.BodyReadTimeout =>
                StatusCodes.Status408RequestTimeout,

            ErrorCodes.Attachment.TooLarge =>
                StatusCodes.Status413PayloadTooLarge,

            ErrorCodes.Scrying.VisionNotSupported or ErrorCodes.Scrying.TooManyImages or ErrorCodes.Scrying.UnsupportedMimeType or ErrorCodes.Scrying.InvalidImageData or ErrorCodes.Files.InvalidMimeType or ErrorCodes.Batches.InvalidEndpoint or ErrorCodes.WebBrowsing.InvalidUrl or ErrorCodes.WebBrowsing.TooLarge or ErrorCodes.WebResearch.InvalidUrl or ErrorCodes.WebResearch.RequestRejected or ErrorCodes.ClientTools.Disabled or ErrorCodes.ClientTools.TooMany or ErrorCodes.ClientTools.InvalidSchema or ErrorCodes.Guardrails.PiiDetected or ErrorCodes.Guardrails.Blocked =>
                StatusCodes.Status400BadRequest,

            ErrorCodes.Scrying.FeatureDisabled =>
                StatusCodes.Status403Forbidden,

            ErrorCodes.Attachment.Disabled =>
                StatusCodes.Status403Forbidden,

            ErrorCodes.Apprentice.AlreadyRunning or ErrorCodes.Apprentice.Running or ErrorCodes.Apprentice.NotPaused or ErrorCodes.Apprentice.CannotReweave or ErrorCodes.Apprentice.NotEscalated or ErrorCodes.Apprentice.MaxReached or ErrorCodes.Apprentice.ConclaveDisabled or ErrorCodes.Session.ForkDepthExceeded or ErrorCodes.Session.TooManyPinned or ErrorCodes.Security.IdempotencyConflict or ErrorCodes.Security.IdempotencyInProgress =>
                StatusCodes.Status409Conflict,

            ErrorCodes.Attachment.LimitExceeded =>
                StatusCodes.Status409Conflict,

            ErrorCodes.Campaign.InvalidPath or ErrorCodes.Campaign.MaxReached or ErrorCodes.Workspace.NameEmpty or ErrorCodes.Spell.NoWorkspace or ErrorCodes.Spell.InvalidWorkspace or ErrorCodes.Prompt.CodexPathNotContained or ErrorCodes.Prompt.InvalidRequest or ErrorCodes.Mcp.AmbiguousServer or ErrorCodes.Mcp.MissingWorkspace or ErrorCodes.Mcp.AmbiguousTool or ErrorCodes.Mcp.ServerNotRunning or ErrorCodes.Mcp.ToolError or ErrorCodes.Apprentice.Disabled or ErrorCodes.Apprentice.InvalidGuidance or ErrorCodes.Apprentice.InvalidPlan or ErrorCodes.Apprentice.InvalidGoal or ErrorCodes.Apprentice.InvalidWorkspace or ErrorCodes.Apprentice.PendingQueueFull or ErrorCodes.ProvingGrounds.InvalidTrial or ErrorCodes.ProvingGrounds.WorkspaceNotAllowed or ErrorCodes.Security.BlockedOutboundUrl or ErrorCodes.Security.IdempotencyKeyTooLong or ErrorCodes.Session.Archived or ErrorCodes.Session.InvalidStatus or ErrorCodes.Session.TooManyEntries or ErrorCodes.Session.EntryTooLarge or ErrorCodes.Session.EmptyContent or ErrorCodes.Session.MemoryManagementDisabled or ErrorCodes.Spell.InvalidName or ErrorCodes.Spell.NameCollision or ErrorCodes.Spell.BuiltinReadOnly or ErrorCodes.Spell.DuplicateVersion or ErrorCodes.Spell.InvalidVersion or ErrorCodes.Prompt.DuplicateVersion or ErrorCodes.Prompt.InvalidName or ErrorCodes.Prompt.InvalidVersion or ErrorCodes.Workspace.DirectoryNotEmpty or ErrorCodes.Workspace.ReplacementAmbiguous or ErrorCodes.Workspace.PathIsDirectory or ErrorCodes.Workspace.PathIsFile or ErrorCodes.Workspace.SymbolicLinkEscape or ErrorCodes.Workspace.PathTraversal or ErrorCodes.Saga.NotEmpty or ErrorCodes.StructuredOutput.ValidationFailed or ErrorCodes.StructuredOutput.SchemaInvalid or ErrorCodes.Embeddings.ConfirmationRequired =>
                StatusCodes.Status400BadRequest,

            ErrorCodes.Attachment.InvalidRequest
                or ErrorCodes.Attachment.InvalidContent
                or ErrorCodes.Attachment.InvalidReference
                or ErrorCodes.Attachment.SourceUnavailable =>
                StatusCodes.Status400BadRequest,

            ErrorCodes.ProvingGrounds.InferenceFailed or ErrorCodes.Workspace.WriteFailed or ErrorCodes.Workspace.DeleteFailed or ErrorCodes.Spell.WriteFailed or ErrorCodes.Saga.SearchFailed =>
                StatusCodes.Status500InternalServerError,

            ErrorCodes.CommLink.Suppressed =>
                StatusCodes.Status502BadGateway,

            ErrorCodes.RateLimit.TooManyRequests =>
                StatusCodes.Status429TooManyRequests,

            ErrorCodes.WebResearch.RateLimited
                or ErrorCodes.WebResearch.BudgetExceeded =>
                StatusCodes.Status429TooManyRequests,

            ErrorCodes.Budget.Exceeded =>
                StatusCodes.Status429TooManyRequests,

            // The server's own 401 is Auth.Unauthorized, written directly by ApiKeyEndpointFilter and
            // never routed through this mapper. Security.MissingApiKey is client-synthesized (CLI /
            // The Forge, when no key is configured locally); it is kept here so a Result carrying
            // either code still resolves to 401 rather than the default 500 arm.
            ErrorCodes.Auth.Unauthorized or ErrorCodes.Security.MissingApiKey =>
                StatusCodes.Status401Unauthorized,

            ErrorCodes.Connection.Timeout or ErrorCodes.WebBrowsing.Timeout or ErrorCodes.WebResearch.Timeout or ErrorCodes.Mcp.DiagnosticTimeout =>
                StatusCodes.Status504GatewayTimeout,

            ErrorCodes.Connection.Unreachable =>
                StatusCodes.Status503ServiceUnavailable,

            ErrorCodes.Sending.AgentUnreachable or ErrorCodes.Sending.AgentCardInvalid =>
                StatusCodes.Status502BadGateway,

            ErrorCodes.Sending.TaskRejected
                or ErrorCodes.Sending.ModalityMismatch
                or ErrorCodes.Sending.SkillNotAdvertised =>
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
            or ErrorCodes.Spell.WriteFailed
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
