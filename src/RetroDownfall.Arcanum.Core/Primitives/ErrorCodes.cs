namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// Wire-stable dotted error codes shared across Api, Infrastructure, and Cli layers.
/// Values are contract strings — do not rename without updating tests and JSON envelopes.
/// </summary>
public static class ErrorCodes
{

    /// <summary>Validation — client input rejected before domain work.</summary>
    public static class Validation
    {

        public const string InvalidPrompt = "Validation.InvalidPrompt";

        public const string AttachedFiles = "Validation.AttachedFiles";

        public const string InvalidBody = "Validation.InvalidBody";

        /// <summary>A JSON route received a body whose <c>Content-Type</c> is missing or is not JSON.</summary>
        public const string UnsupportedMediaType = "Validation.UnsupportedMediaType";

        public const string InvalidQuery = "Validation.InvalidQuery";

        public const string InvalidProviderType = "Validation.InvalidProviderType";

        public const string InvalidReasoningEffort = "Validation.InvalidReasoningEffort";

        public const string InvalidReasoningOutput = "Validation.InvalidReasoningOutput";

        public const string ReasoningEffortAndBudgetMutuallyExclusive =
            "Validation.ReasoningEffortAndBudgetMutuallyExclusive";

        public const string InvalidReasoningBudget = "Validation.InvalidReasoningBudget";

        public const string UnsupportedReasoningControl = "Validation.UnsupportedReasoningControl";

        public const string ReasoningBudgetExceedsModelLimit =
            "Validation.ReasoningBudgetExceedsModelLimit";

        public const string UnsupportedReasoningOutput = "Validation.UnsupportedReasoningOutput";

        /// <summary>A lore key was empty or exceeded the 256-character bound.</summary>
        public const string InvalidKey = "Validation.InvalidKey";

        /// <summary>A lore upsert omitted the key or the value.</summary>
        public const string InvalidLore = "Validation.InvalidLore";

        /// <summary>A CommLink send omitted the title or the body.</summary>
        public const string InvalidFields = "Validation.InvalidFields";

        /// <summary>An Unseen Servant initiative route received an empty job name.</summary>
        public const string InvalidJobName = "Validation.InvalidJobName";

        /// <summary>A human-response submission omitted <c>promptId</c> or <c>answer</c>.</summary>
        public const string InvalidHumanResponse = "Validation.InvalidHumanResponse";

        /// <summary>The requested override spell name matches no spell in the catalog.</summary>
        public const string SpellOverride = "Validation.SpellOverride";

    }

    /// <summary>Hub — intelligence provider / inference pipeline failures.</summary>
    public static class Hub
    {
        public const string ContextBudgetExceeded = "Hub.ContextBudgetExceeded";

        public const string RepetitionDetected = "Hub.RepetitionDetected";

        public const string TurnLimitExceeded = "Hub.TurnLimitExceeded";

        public const string NoProgressDetected = "Hub.NoProgressDetected";

        public const string Model = "Hub.Model";

        public const string Error = "Hub.Error";

        /// <summary>Last-resort code for an exception that escaped every endpoint.</summary>
        public const string Unhandled = "Hub.Unhandled";

    }

    /// <summary>Campaign — forge workspace registration and paths.</summary>
    public static class Campaign
    {

        public const string NotFound = "Campaign.NotFound";

        public const string InvalidPath = "Campaign.InvalidPath";

        public const string PathNotAllowed = "Campaign.PathNotAllowed";

        public const string MaxReached = "Campaign.MaxReached";

        public const string InvalidName = "Campaign.InvalidName";

        public const string DuplicateName = "Campaign.DuplicateName";

        public const string DuplicatePath = "Campaign.DuplicatePath";

        /// <summary>The campaign's <c>.arcanum</c> directory could not be created on disk.</summary>
        public const string DirectoryCreateFailed = "Campaign.DirectoryCreateFailed";

        /// <summary>No import payload was supplied and no readable <c>campaign.json</c> was found.</summary>
        public const string ImportFailed = "Campaign.ImportFailed";

    }

    /// <summary>Session — grimoire conversation persistence.</summary>
    public static class Session
    {

        public const string NotFound = "Session.NotFound";

        public const string InvalidStatus = "Session.InvalidStatus";

        public const string Archived = "Session.Archived";

        public const string TooManyEntries = "Session.TooManyEntries";

        public const string EntryTooLarge = "Session.EntryTooLarge";

        public const string EmptyContent = "Session.EmptyContent";

        /// <summary>The source session's fork lineage reached the code-owned depth limit.</summary>
        public const string ForkDepthExceeded = "Session.ForkDepthExceeded";

        /// <summary>The optional <c>upToEntryId</c> fork cutoff does not identify an entry belonging to the source session.</summary>
        public const string EntryNotFound = "Session.EntryNotFound";

        /// <summary>Memory-management endpoints are disabled by <c>Arcanum:Features:MemoryManagement</c>.</summary>
        public const string MemoryManagementDisabled = "Session.MemoryManagementDisabled";

        /// <summary>Pinning an entry would exceed the code-owned per-session limit.</summary>
        public const string TooManyPinned = "Session.TooManyPinned";

        /// <summary>Explicit <c>POST /api/sessions/{id}/rest</c> could not enqueue Campaign Log consolidation.</summary>
        public const string RestQueueFull = "Session.RestQueueFull";

    }

    /// <summary>Attachment — standalone session-attachment lifecycle.</summary>
    public static class Attachment
    {

        public const string Disabled = "Attachment.Disabled";

        public const string InvalidRequest = "Attachment.InvalidRequest";

        public const string InvalidContent = "Attachment.InvalidContent";

        public const string InvalidReference = "Attachment.InvalidReference";

        public const string NotFound = "Attachment.NotFound";

        public const string SourceNotFound = "Attachment.SourceNotFound";

        public const string SourceUnavailable = "Attachment.SourceUnavailable";

        public const string TooLarge = "Attachment.TooLarge";

        public const string LimitExceeded = "Attachment.LimitExceeded";

    }

    /// <summary>Grimoire — lore and knowledge store.</summary>
    public static class Grimoire
    {

        public const string LoreNotFound = "Grimoire.LoreNotFound";

    }

    /// <summary>Apprentice — autonomous agent orchestration.</summary>
    public static class Apprentice
    {

        public const string NotFound = "Apprentice.NotFound";

        public const string Disabled = "Apprentice.Disabled";

        public const string AlreadyRunning = "Apprentice.AlreadyRunning";

        public const string Running = "Apprentice.Running";

        public const string NotPaused = "Apprentice.NotPaused";

        public const string CannotReweave = "Apprentice.CannotReweave";

        public const string InvalidGuidance = "Apprentice.InvalidGuidance";

        public const string NotEscalated = "Apprentice.NotEscalated";

        public const string PendingQueueFull = "Apprentice.PendingQueueFull";

        public const string MaxReached = "Apprentice.MaxReached";

        public const string InvalidPlan = "Apprentice.InvalidPlan";

        public const string InvalidGoal = "Apprentice.InvalidGoal";

        public const string InvalidWorkspace = "Apprentice.InvalidWorkspace";

        public const string ConclaveDisabled = "Apprentice.ConclaveDisabled";

        public const string InvalidName = "Apprentice.InvalidName";

    }

    /// <summary>Workspace — registered filesystem roots.</summary>
    public static class Workspace
    {

        public const string NotFound = "Workspace.NotFound";

        public const string NameEmpty = "Workspace.NameEmpty";

        public const string PathNotAllowed = "Workspace.PathNotAllowed";

        public const string AccessDenied = "Workspace.AccessDenied";

        public const string FileNotFound = "Workspace.FileNotFound";

        public const string FileTooLarge = "Workspace.FileTooLarge";

        public const string SymbolicLinkEscape = "Workspace.SymbolicLinkEscape";

        public const string PathTraversal = "Workspace.PathTraversal";

        public const string FileWriteDisabled = "Workspace.FileWriteDisabled";

        public const string WriteFailed = "Workspace.WriteFailed";

        public const string DeleteFailed = "Workspace.DeleteFailed";

        public const string DirectoryNotEmpty = "Workspace.DirectoryNotEmpty";

        public const string ReplacementNotFound = "Workspace.ReplacementNotFound";

        public const string ReplacementAmbiguous = "Workspace.ReplacementAmbiguous";

        public const string PathIsDirectory = "Workspace.PathIsDirectory";

        public const string PathIsFile = "Workspace.PathIsFile";

        public const string ContinuationInvalid = "Workspace.ContinuationInvalid";

        public const string ContinuationCheckpointMissing =
            "Workspace.ContinuationCheckpointMissing";

    }

    /// <summary>Perception — filesystem pattern snapshots.</summary>
    public static class Perception
    {

        /// <summary>The requested directory could not be resolved, or does not exist.</summary>
        public const string InvalidPath = "Perception.InvalidPath";

    }

    /// <summary>Spell — workspace spell files and execution.</summary>
    public static class Spell
    {

        public const string NotFound = "Spell.NotFound";

        public const string PathNotAllowed = "Spell.PathNotAllowed";

        public const string NoWorkspace = "Spell.NoWorkspace";

        public const string InvalidWorkspace = "Spell.InvalidWorkspace";

        public const string InvalidName = "Spell.InvalidName";

        public const string NameCollision = "Spell.NameCollision";

        public const string BuiltinReadOnly = "Spell.BuiltinReadOnly";

        public const string DuplicateVersion = "Spell.DuplicateVersion";

        public const string InvalidVersion = "Spell.InvalidVersion";

        public const string ContinuationInvalid = "Spell.ContinuationInvalid";

        public const string ContinuationQueryMismatch =
            "Spell.ContinuationQueryMismatch";

        public const string ContinuationCheckpointMissing =
            "Spell.ContinuationCheckpointMissing";

        public const string ContinuationFrameTooLarge =
            "Spell.ContinuationFrameTooLarge";

    }

    /// <summary>Prompt — named prompt templates.</summary>
    public static class Prompt
    {

        public const string NotFound = "Prompt.NotFound";

        public const string CodexPathNotContained = "Prompt.CodexPathNotContained";

        public const string DuplicateVersion = "Prompt.DuplicateVersion";

        public const string InvalidName = "Prompt.InvalidName";

        public const string InvalidVersion = "Prompt.InvalidVersion";

        public const string InvalidRequest = "Prompt.InvalidRequest";

    }

    /// <summary>Codex — the per-campaign CODEX context document.</summary>
    public static class Codex
    {

        /// <summary>The submitted CODEX body exceeds the configured UTF-8 byte ceiling.</summary>
        public const string ContentTooLarge = "Codex.ContentTooLarge";

    }

    /// <summary>Intelligence — cross-surface inference helpers.</summary>
    public static class Intelligence
    {

        public const string HumanPromptNotFound = "Intelligence.HumanPromptNotFound";

    }

    /// <summary>StructuredOutput — JSON schema validation and constrained decoding.</summary>
    public static class StructuredOutput
    {

        public const string ValidationFailed = "StructuredOutput.ValidationFailed";

        public const string SchemaInvalid = "StructuredOutput.SchemaInvalid";

    }

    /// <summary>Mcp — Model Context Protocol servers and transport.</summary>
    public static class Mcp
    {

        public const string AmbiguousServer = "Mcp.AmbiguousServer";

        public const string MissingWorkspace = "Mcp.MissingWorkspace";

        public const string ServerNotFound = "Mcp.ServerNotFound";

        /// <summary>Diagnostic MCP Invocation: the named server is not running.</summary>
        public const string ServerNotRunning = "Mcp.ServerNotRunning";

        /// <summary>Diagnostic MCP Invocation: the workspace-local MCP surface is not trusted.</summary>
        public const string WorkspaceNotTrusted = "Mcp.WorkspaceNotTrusted";

        /// <summary>Diagnostic MCP Invocation: the requested tool name was not found on any visible external server.</summary>
        public const string ToolNotFound = "Mcp.ToolNotFound";

        /// <summary>Diagnostic MCP Invocation: the requested tool belongs to the internal server or is a Forbidden Art and cannot be invoked from the diagnostic endpoint.</summary>
        public const string DiagnosticBlocked = "Mcp.DiagnosticBlocked";

        /// <summary>Diagnostic MCP Invocation: the tool name is provided by more than one visible external server; specify serverName.</summary>
        public const string AmbiguousTool = "Mcp.AmbiguousTool";

        /// <summary>Diagnostic MCP Invocation: the tool returned an error result.</summary>
        public const string ToolError = "Mcp.ToolError";

        /// <summary>Diagnostic MCP Invocation: the request exceeded the configured timeout.</summary>
        public const string DiagnosticTimeout = "Mcp.DiagnosticTimeout";

        /// <summary>Diagnostic MCP Invocation: the route exists only on the Development edition.</summary>
        public const string DiagnosticDisabled = "Mcp.DiagnosticDisabled";

    }

    /// <summary>Daemon — background job orchestration.</summary>
    public static class Daemon
    {

        public const string NotFound = "Daemon.NotFound";

        /// <summary>Cancellation was requested for an execution that is absent or already terminal.</summary>
        public const string NotRunning = "Daemon.NotRunning";

    }

    /// <summary>Execution — individual daemon execution records.</summary>
    public static class Execution
    {

        public const string NotFound = "Execution.NotFound";

    }

    /// <summary>CommLink — outbound webhook notifications. Suppressed outcomes are expected, not 5xx.</summary>
    public static class CommLink
    {

        public const string Suppressed = "CommLink.Suppressed";

    }

    /// <summary>Api — HTTP surface and streaming admission.</summary>
    public static class Api
    {

        public const string TooManyConnections = "Api.TooManyConnections";

    }

    /// <summary>RateLimit — request throttling.</summary>
    public static class RateLimit
    {

        public const string TooManyRequests = "RateLimit.TooManyRequests";

    }

    /// <summary>Budget — daily cost spend enforcement.</summary>
    public static class Budget
    {

        public const string Exceeded = "Budget.Exceeded";

    }

    /// <summary>Data lifecycle planning and destructive execution.</summary>
    public static class Data
    {

        public const string InvalidRequest = "Data.InvalidRequest";

        public const string PlanChanged = "Data.PlanChanged";

        public const string Blocked = "Data.Blocked";

        public const string Conflict = "Data.Conflict";

        public const string NotFound = "Data.NotFound";

        public const string ConfirmationRequired = "Data.ConfirmationRequired";

        public const string ReconciliationFailed = "Data.ReconciliationFailed";

    }

    /// <summary>Connection — client transport failures.</summary>
    public static class Connection
    {

        public const string Timeout = "Connection.Timeout";

        public const string Unreachable = "Connection.Unreachable";

    }

    /// <summary>Auth — request authentication outcomes emitted by the API-key filter (DESIGN §11.3).</summary>
    public static class Auth
    {

        /// <summary>
        /// The only 401 the server itself emits: <c>ApiKeyEndpointFilter</c> rejected a missing,
        /// ambiguous, oversized, or non-matching API key. Distinct from
        /// <see cref="Security.MissingApiKey"/>, which clients synthesize locally when no key is
        /// configured and no request was ever sent.
        /// </summary>
        public const string Unauthorized = "Auth.Unauthorized";

    }

    /// <summary>Security — authentication and outbound URL policy.</summary>
    public static class Security
    {

        /// <summary>
        /// Client-synthesized only: the CLI and The Forge emit this when no API key is available
        /// locally, so the request is never sent. The server never returns it — a rejected key
        /// comes back as <see cref="Auth.Unauthorized"/>.
        /// </summary>
        public const string MissingApiKey = "Security.MissingApiKey";

        public const string BlockedOutboundUrl = "Security.BlockedOutboundUrl";

        /// <summary>The <c>Idempotency-Key</c> request header exceeds the maximum allowed length.</summary>
        public const string IdempotencyKeyTooLong = "Security.IdempotencyKeyTooLong";

        /// <summary>The request carries more than one <c>Idempotency-Key</c> header value.</summary>
        public const string IdempotencyKeyAmbiguous = "Security.IdempotencyKeyAmbiguous";

        /// <summary>Same idempotency key reused with a different request fingerprint.</summary>
        public const string IdempotencyConflict = "Security.IdempotencyConflict";

        /// <summary>Another process currently owns a live claim for this idempotency key.</summary>
        public const string IdempotencyInProgress = "Security.IdempotencyInProgress";

    }

    /// <summary>Files — OpenAI-compatible <c>/v1/files</c> upload storage.</summary>
    public static class Files
    {

        public const string NotFound = "Files.NotFound";

        public const string TooLarge = "Files.TooLarge";

        public const string InvalidMimeType = "Files.InvalidMimeType";

    }

    /// <summary>Batches — OpenAI-compatible <c>/v1/batches</c> asynchronous bulk chat completion.</summary>
    public static class Batches
    {

        public const string NotFound = "Batches.NotFound";

        public const string InvalidEndpoint = "Batches.InvalidEndpoint";

        public const string InputFileNotFound = "Batches.InputFileNotFound";

    }

    /// <summary>Embeddings — The Weave (embedding substrate) and Divination (semantic search).</summary>
    public static class Embeddings
    {

        public const string ProviderUnavailable = "Embeddings.ProviderUnavailable";

        public const string FeatureDisabled = "Embeddings.FeatureDisabled";

        public const string ConfirmationRequired = "Embeddings.ConfirmationRequired";

    }

    /// <summary>Provider rows in <c>Arcanum:Providers</c>.</summary>
    public static class Provider
    {

        public const string NotFound = "Provider.NotFound";

    }

    /// <summary>ProvingGrounds — spell/prompt/plan validation trials.</summary>
    public static class ProvingGrounds
    {

        public const string InvalidTrial = "ProvingGrounds.InvalidTrial";

        public const string WorkspaceNotAllowed = "ProvingGrounds.WorkspaceNotAllowed";

        public const string SpellNotFound = "ProvingGrounds.SpellNotFound";

        public const string PromptNotFound = "ProvingGrounds.PromptNotFound";

        public const string InferenceFailed = "ProvingGrounds.InferenceFailed";

    }

    /// <summary>Saga — RAG Phase 4 long-term associative memory.</summary>
    public static class Saga
    {

        public const string NotFound = "Saga.NotFound";

        public const string NotEmpty = "Saga.NotEmpty";

        public const string SearchFailed = "Saga.SearchFailed";

    }

    /// <summary>Lexicon — structured agent-directed entity memory (replaces model-facing Lore).</summary>
    public static class Lexicon
    {

        public const string InvalidName = "Lexicon.InvalidName";

        public const string InvalidFact = "Lexicon.InvalidFact";

        public const string NotFound = "Lexicon.NotFound";

        public const string WriteFailed = "Lexicon.WriteFailed";

        public const string SearchFailed = "Lexicon.SearchFailed";

    }

    /// <summary>Scrying — vision/multimodality capability gate and image validation.</summary>
    public static class Scrying
    {

        public const string VisionNotSupported = "Scrying.VisionNotSupported";

        public const string ImageTooLarge = "Scrying.ImageTooLarge";

        public const string TooManyImages = "Scrying.TooManyImages";

        public const string UnsupportedMimeType = "Scrying.UnsupportedMimeType";

        /// <summary>The caller's image payload is not well-formed base64.</summary>
        public const string InvalidImageData = "Scrying.InvalidImageData";

        public const string FeatureDisabled = "Scrying.FeatureDisabled";

    }

    /// <summary>Sending — A2A (Agent-to-Agent) protocol interoperability surface for The Conclave.</summary>
    public static class Sending
    {

        public const string AgentUnreachable = "Sending.AgentUnreachable";

        public const string AgentCardInvalid = "Sending.AgentCardInvalid";

        public const string TaskRejected = "Sending.TaskRejected";

        public const string Disabled = "Sending.Disabled";

        public const string AgentNotAllowed = "Sending.AgentNotAllowed";

        public const string MaxTasksReached = "Sending.MaxTasksReached";

        /// <summary>
        /// The peer's Agent Card cannot produce any output modality this dispatch will accept. Raised
        /// before the remote task is created, so nothing is left running on the far side (issue #65).
        /// </summary>
        public const string ModalityMismatch = "Sending.ModalityMismatch";

        /// <summary>The peer's Agent Card advertises no skill with the requested id (issue #65).</summary>
        public const string SkillNotAdvertised = "Sending.SkillNotAdvertised";

        /// <summary>
        /// The A2A push-notification surface is not enabled on this instance
        /// (<c>Arcanum:Integrations:A2A:PushNotifications</c>) — issue #67.
        /// </summary>
        public const string PushNotificationsDisabled = "Sending.PushNotificationsDisabled";

        /// <summary>
        /// A push-notification callback was refused: an unusable URL, one the allowlist does not vouch
        /// for, or one the outbound URL guard blocked (issue #67).
        /// </summary>
        public const string PushNotificationRejected = "Sending.PushNotificationRejected";

    }

    /// <summary>WebBrowsing — built-in <c>browse_web</c> tool errors.</summary>
    public static class WebBrowsing
    {

        public const string SsrfBlocked = "WebBrowsing.SsrfBlocked";

        public const string TooLarge = "WebBrowsing.TooLarge";

        public const string Timeout = "WebBrowsing.Timeout";

        public const string InvalidUrl = "WebBrowsing.InvalidUrl";

    }

    /// <summary>WebResearch — native synthesized search and direct URL-reading failures.</summary>
    public static class WebResearch
    {

        public const string MissingCredential = "WebResearch.MissingCredential";

        public const string AuthenticationOrCreditsFailed =
            "WebResearch.AuthenticationOrCreditsFailed";

        public const string QuotaExhausted = "WebResearch.QuotaExhausted";

        public const string RateLimited = "WebResearch.RateLimited";

        public const string RequestRejected = "WebResearch.RequestRejected";

        public const string ProviderUnavailable = "WebResearch.ProviderUnavailable";

        public const string InvalidResponse = "WebResearch.InvalidResponse";

        public const string Timeout = "WebResearch.Timeout";

        public const string InvalidUrl = "WebResearch.InvalidUrl";

        public const string SsrfBlocked = "WebResearch.SsrfBlocked";

        public const string RedirectLimitExceeded = "WebResearch.RedirectLimitExceeded";

        public const string BotProtected = "WebResearch.BotProtected";

        public const string JavaScriptRequired = "WebResearch.JavaScriptRequired";

        public const string EmptyContent = "WebResearch.EmptyContent";

        public const string UnsupportedContent = "WebResearch.UnsupportedContent";

        public const string ResponseTooLarge = "WebResearch.ResponseTooLarge";

        public const string UnsupportedOperation = "WebResearch.UnsupportedOperation";

        public const string JavaScriptRenderingUnavailable =
            "WebResearch.JavaScriptRenderingUnavailable";

        public const string BudgetExceeded = "WebResearch.BudgetExceeded";

    }

    /// <summary>ClientTools — client-supplied tool forwarding errors.</summary>
    public static class ClientTools
    {

        public const string Disabled = "ClientTools.Disabled";

        public const string TooMany = "ClientTools.TooMany";

        public const string InvalidSchema = "ClientTools.InvalidSchema";

    }

    /// <summary>Guardrails — content filter (PII / toxicity / topic) violations (Tier 3 Phase 4).</summary>
    public static class Guardrails
    {

        /// <summary>Personally-identifiable information (email/phone/SSN/credit-card) was detected in the
        /// input and the turn was rejected before inference ran. Distinct from <see cref="Blocked"/> so
        /// callers and operators can distinguish "redact-and-retry" PII from policy blocks.</summary>
        public const string PiiDetected = "Guardrails.PiiDetected";

        /// <summary>A toxicity-blocklist hit or an allowed/blocked-topic rule matched, rejecting the turn.</summary>
        public const string Blocked = "Guardrails.Blocked";

    }

    /// <summary>Ward — operator approval gates for risky tool invocations.</summary>
    public static class Ward
    {

        public const string NotFound = "Ward.NotFound";

        public const string AlreadyResolved = "Ward.AlreadyResolved";

    }

    /// <summary>Sanctum — tool execution containment policy.</summary>
    public static class Sanctum
    {

        /// <summary>The submitted Sanctum configuration is internally inconsistent.</summary>
        public const string InvalidConfig = "Sanctum.InvalidConfig";

    }

}
