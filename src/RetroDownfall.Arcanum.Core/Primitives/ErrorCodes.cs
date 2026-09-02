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

        /// <summary>
        /// The request body is larger than this server accepts, so it was never read to the end.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="InvalidBody"/> because the two ask the caller for different things:
        /// a malformed body is worth resending corrected, and an oversized one is not worth resending at
        /// all. That is the same reason <c>Attachment.TooLarge</c> is distinct from
        /// <c>Attachment.InvalidRequest</c>, and every other code on this installation's 413.
        /// </remarks>
        public const string BodyTooLarge = "Validation.BodyTooLarge";

        /// <summary>
        /// The request body arrived too slowly to be read, so the server stopped waiting for it.
        /// </summary>
        /// <remarks>
        /// Kestrel enforces a minimum data rate and answers 408 when a body falls under it. Distinct
        /// from <see cref="InvalidBody"/> because nothing is wrong with the body: it is worth resending
        /// unchanged on a better connection, which is the one thing a 400 would tell the caller not to do.
        /// </remarks>
        public const string BodyReadTimeout = "Validation.BodyReadTimeout";

        /// <summary>
        /// The request's headers or trailers exceed the total size this server accepts.
        /// </summary>
        /// <remarks>
        /// Reachable while reading a chunked body, because trailers arrive after it and count against
        /// the same ceiling. Distinct from <see cref="BodyTooLarge"/> because shrinking the body will
        /// not help, and from <see cref="InvalidBody"/> because the body may be perfectly well formed.
        /// </remarks>
        public const string RequestHeadersTooLarge = "Validation.RequestHeadersTooLarge";

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

        /// <summary>
        /// A tool call carried an argument body that has no stable identity: it is not valid JSON, so
        /// it cannot be canonicalized into the digest a Ward receipt and a disclosure receipt bind.
        /// </summary>
        /// <remarks>
        /// Evidence has to be computable before anything decides what a call may do, so this refuses
        /// the call ahead of classification and dispatch (§10.14).
        /// </remarks>
        public const string ProviderToolCallInvalid = "Hub.ProviderToolCallInvalid";

        /// <summary>
        /// A different turn already holds this Session's durable claim.
        /// </summary>
        /// <remarks>
        /// A conflict rather than a queue. Two turns appending to one Session concurrently would each
        /// see a history the other is about to change, and the second one to finish would publish an
        /// answer to a question that no longer describes the conversation (§10.13).
        /// </remarks>
        public const string SessionTurnBusy = "Hub.SessionTurnBusy";

        /// <summary>
        /// A claim was presented whose history watermark no longer matches the Session.
        /// </summary>
        public const string SessionHistoryChanged = "Hub.SessionHistoryChanged";

        /// <summary>
        /// A nonterminal turn was captured by a physical backup and terminalized on restore, so its
        /// claim can be reported but never resumed or replayed.
        /// </summary>
        public const string SessionTurnRestoredInterrupted = "Hub.SessionTurnRestoredInterrupted";

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

        /// <summary>
        /// This Campaign has no resolved physical root, so nothing scoped to it may run.
        /// </summary>
        /// <remarks>
        /// Deliberately not a fallback to path text. A Campaign whose registered root cannot be
        /// opened and proven is a Campaign whose Covenant scope and workspace tools would be aimed at
        /// a directory nobody verified, and guessing is how a scoped write lands somewhere else
        /// (§10.12).
        /// </remarks>
        public const string PathIdentityRequired = "Campaign.PathIdentityRequired";

    }

    /// <summary>Session — grimoire conversation persistence.</summary>
    public static class Session
    {

        public const string NotFound = "Session.NotFound";

        public const string InvalidStatus = "Session.InvalidStatus";

        /// <summary>
        /// The <c>format</c> of <c>GET /api/sessions/{id}/export</c> is missing or is not one of the
        /// documented values.
        /// </summary>
        /// <remarks>
        /// The wire vocabulary is the enum's own string names — <c>json</c> and <c>markdown</c> — and
        /// they are matched without regard to case. A route that only accepted the CLR spelling
        /// refused the exact value the CLI and the published contract both use.
        /// </remarks>
        public const string InvalidFormat = "Session.InvalidFormat";

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

        /// <summary>
        /// This Session predates immutable Campaign binding, so no authority can be derived from it
        /// until an authenticated operator resolves the binding (§10.12).
        /// </summary>
        public const string CampaignBindingRequired = "Session.CampaignBindingRequired";

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

        /// <summary>A durable Grimoire write did not commit. The transaction wrote nothing.</summary>
        public const string WriteFailed = "Grimoire.WriteFailed";

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

        /// <summary>A caller-supplied pattern snapshot violates its bounded semantic contract.</summary>
        public const string InvalidSnapshot = "Perception.InvalidSnapshot";

        /// <summary>
        /// The directory resolved outside <c>Arcanum:Security:PerceptionWorkspaceRoots</c>. Distinct
        /// from <see cref="InvalidPath"/> on purpose: the deny answer is returned before any existence
        /// probe, so a denied caller cannot use the endpoint as a filesystem existence oracle.
        /// </summary>
        public const string PathNotAllowed = "Perception.PathNotAllowed";

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

        /// <summary>
        /// A spell file could not be written. The envelope carries a fixed sanitized sentence and the
        /// exception detail stays in the server log, so no absolute server path reaches the caller.
        /// </summary>
        public const string WriteFailed = "Spell.WriteFailed";

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

        /// <summary>
        /// <c>CODEX.md</c> is a link resolving outside its campaign root or the Grimoire directory. A
        /// campaign root is frequently an untrusted repository, and a repository can ship that link.
        /// </summary>
        public const string PathNotContained = "Codex.PathNotContained";

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

        /// <summary>Diagnostic MCP Invocation: the requested tool belongs to the internal server or requires the Master tool execution pipeline.</summary>
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

    /// <summary>Operation — durable long-running operations (<c>/api/operations</c>).</summary>
    public static class Operation
    {

        public const string NotFound = "Operation.NotFound";

        /// <summary>The <c>state</c> filter named something that is not a durable operation state.</summary>
        public const string InvalidState = "Operation.InvalidState";

        /// <summary>
        /// The compare-and-set lost: the operation moved, is already terminal, or is not in a state
        /// this transition accepts. The caller can re-read and decide, so it is a conflict, not a fault.
        /// </summary>
        public const string StateConflict = "Operation.StateConflict";

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

        /// <summary>The operation failed and its durable history requires operator review.</summary>
        /// <remarks>
        /// Deliberately no longer the single answer for every unhappy retention ending. It kept
        /// "retry is safe", "the mutation committed and bytes are still quarantined" and "somebody
        /// has to look" under one code and one status, so a programmatic client could not tell them
        /// apart; the distinguishing detail survived only in the message text.
        /// </remarks>
        public const string ReconciliationFailed = "Data.ReconciliationFailed";

        /// <summary>
        /// The database mutation committed and quarantined bytes still need to be finalized.
        /// </summary>
        /// <remarks>
        /// The one ending where the data change is already durable and the remaining work is on
        /// disk. A caller must not treat it as "nothing happened": the rows are gone, and durable
        /// recovery or an operator finishes the cleanup.
        /// </remarks>
        public const string QuarantineRecoveryRequired = "Data.QuarantineRecoveryRequired";

        /// <summary>
        /// The data change applied, but the durable operation row could not be transitioned.
        /// </summary>
        /// <remarks>
        /// The only retention ending where an unconditional retry is safe: nothing is wrong with the
        /// data and nothing is left on disk, so the caller may simply ask again.
        /// </remarks>
        public const string OperationNotFinalized = "Data.OperationNotFinalized";

        public const string InventoryUnavailable = "Data.InventoryUnavailable";

        public const string CredentialInventoryUnavailable = "Data.CredentialInventoryUnavailable";

        public const string ResetInProgress = "Data.ResetInProgress";

        public const string RecoveryRequired = "Data.RecoveryRequired";

        public const string FileLocked = "Data.FileLocked";

        public const string WorkspaceOverlap = "Data.WorkspaceOverlap";

        public const string ControlPathUnavailable = "Data.ControlPathUnavailable";

        public const string ExternalRemediationRequired = "Data.ExternalRemediationRequired";

        public const string ExternalRemediationInvalid = "Data.ExternalRemediationInvalid";

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

        /// <summary>The caller's view of this memory's content is stale relative to what is stored now.</summary>
        public const string StaleContent = "Saga.StaleContent";

        /// <summary>
        /// A correction was asked for against a retired memory. Reinstate it first.
        /// </summary>
        /// <remarks>
        /// Emitted by correction alone. Retiring a memory that is already retired is not an error — the
        /// operator asked for a state and has it — but correcting one is: a retired memory is reinstated
        /// before it is corrected, and the store checks the retirement before it compares any content,
        /// so this is the answer whatever text the correction carried.
        /// </remarks>
        public const string AlreadyRetired = "Saga.AlreadyRetired";

        /// <summary>
        /// The embedding substrate cannot produce a vector right now, so the write was refused rather
        /// than leaving this memory's text and its vector disagreeing about what it says.
        /// </summary>
        public const string EmbeddingUnavailable = "Saga.EmbeddingUnavailable";

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

        /// <summary>
        /// An unexpected fault inside a native web tool adapter. Reported in the structured MCP tool
        /// result rather than on an HTTP route, so §8.23 gives it no status row.
        /// </summary>
        public const string InternalError = "WebResearch.InternalError";

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

    /// <summary>Ward — tool-call audit records and retained active-record compatibility.</summary>
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

    /// <summary>
    /// Covenant — the durable operator-and-agent profile, its authority boundary, and its tiers.
    /// </summary>
    /// <remarks>
    /// Deliberately content-free. Every message paired with one of these codes describes a decision,
    /// never a Covenant key, authored fragment, compiled fragment, or raw content hash: these codes
    /// travel through logs, metrics, and unauthenticated status surfaces where the content behind the
    /// decision has no authority to be (§10.12).
    /// </remarks>
    public static class Covenant
    {

        /// <summary>A Covenant tier is absent, damaged, closed, or otherwise not open for this work.</summary>
        public const string Unavailable = "Covenant.Unavailable";

        /// <summary>The requested scope, key, version, or lane head does not exist.</summary>
        public const string NotFound = "Covenant.NotFound";

        /// <summary>The supplied scope is malformed, uninitialized, or wrong for the operation.</summary>
        public const string InvalidScope = "Covenant.InvalidScope";

        /// <summary>The supplied key does not match the frozen key grammar.</summary>
        public const string InvalidKey = "Covenant.InvalidKey";

        /// <summary>
        /// The supplied content failed the compiler's Unicode, boundary, or byte-cost contract.
        /// </summary>
        public const string InvalidContent = "Covenant.InvalidContent";

        /// <summary>An opaque cursor failed authentication, bounds, or binding validation.</summary>
        public const string InvalidCursor = "Covenant.InvalidCursor";

        /// <summary>
        /// A cursor authenticated, but the dataset, canonical sequence, or accelerator state it bound
        /// has moved on.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="InvalidCursor"/> because the two answer different questions. This
        /// one was genuinely issued here and its page simply no longer exists; an invalid cursor
        /// cannot be trusted to say anything at all, including which query it belonged to (§10.11).
        /// </remarks>
        public const string StaleCursor = "Covenant.StaleCursor";

        /// <summary>
        /// The durable artifact behind this response was securely erased, so the recorded result can
        /// be reported but never returned.
        /// </summary>
        public const string ArtifactErased = "Covenant.ArtifactErased";

        /// <summary>
        /// The caller holds no authority for this effect, or holds authority a surface may never carry.
        /// </summary>
        public const string ForbiddenAuthority = "Covenant.ForbiddenAuthority";

        /// <summary>
        /// Operator authority cannot be issued at all — a tainted, unestablished, or closed installation.
        /// </summary>
        public const string OperatorAuthorityUnavailable = "Covenant.OperatorAuthorityUnavailable";

        /// <summary>A lease, snapshot, epoch, generation, or revision the caller froze has moved on.</summary>
        public const string StaleSnapshot = "Covenant.StaleSnapshot";

        /// <summary>An expected revision lost its compare-and-swap against the current head.</summary>
        public const string RevisionConflict = "Covenant.RevisionConflict";

        /// <summary>The operation contradicts the current lifecycle state of its subject.</summary>
        public const string LifecycleConflict = "Covenant.LifecycleConflict";

        /// <summary>A bounded resource — rows, bytes, versions, receipts, or capacity — is full.</summary>
        public const string CapacityExceeded = "Covenant.CapacityExceeded";

        /// <summary>Persisted state failed its own integrity contract and must not be used.</summary>
        public const string IntegrityFailure = "Covenant.IntegrityFailure";

        /// <summary>A maintenance, cleanup, or synchronization step did not complete.</summary>
        public const string MaintenanceFailed = "Covenant.MaintenanceFailed";

        /// <summary>Automatic recovery is refused; an authenticated operator operation is required.</summary>
        public const string ManualRecoveryRequired = "Covenant.ManualRecoveryRequired";

        /// <summary>
        /// A managed file that carries Covenant-derived content changed under Arcanum's handle, so
        /// only its operator can remove it.
        /// </summary>
        /// <remarks>
        /// Reported instead of deleting the changed file. Erasure that overwrote an operator's own
        /// later edit would be destroying evidence the operator owns in order to complete a promise
        /// Arcanum made about content it no longer controls (§10.16).
        /// </remarks>
        public const string ManualArtifactErasureRequired = "Covenant.ManualArtifactErasureRequired";

        /// <summary>
        /// Canonical rows are gone but local secure erasure has not finished proving every
        /// application-controlled artifact absent.
        /// </summary>
        public const string ErasureIncomplete = "Covenant.ErasureIncomplete";

        /// <summary>
        /// Two authority sources named different Campaigns, or a supplied path escaped the bound one.
        /// </summary>
        public const string CampaignBindingConflict = "Covenant.CampaignBindingConflict";

        /// <summary>
        /// The host was started with the unsandboxed escape hatch but without matching durable markers.
        /// </summary>
        public const string HostToolsTransitionRequired = "Covenant.HostToolsTransitionRequired";

        /// <summary>
        /// A no-context continuation required history that carries a Covenant-derived artifact.
        /// </summary>
        public const string SensitiveHistoryRequiresContext = "Covenant.SensitiveHistoryRequiresContext";

        /// <summary>
        /// A tool call would disclose Covenant-derived content to a declared sink, and the operator
        /// did not approve it.
        /// </summary>
        /// <remarks>
        /// A refusal rather than a redaction. Stripping the protected part and sending the rest would
        /// mean the operator's approval decided the shape of the disclosure rather than whether it
        /// happened (§10.14).
        /// </remarks>
        public const string SensitiveEgressRequiresApproval = "Covenant.SensitiveEgressRequiresApproval";

        /// <summary>
        /// A plaintext export would carry Covenant-derived content out of the installation, so it is
        /// refused before a single content byte.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="SensitiveEgressRequiresApproval"/> because no approval makes it
        /// proceed. A tool call to a declared sink is a disclosure an operator may authorize and
        /// Arcanum can then record against the turn that made it; a plaintext file is nonrevocable the
        /// moment it exists, and there is no receipt that unmakes it (§10.19.11).
        /// </remarks>
        public const string PlaintextExportRefused = "Covenant.PlaintextExportRefused";

        /// <summary>
        /// A Covenant MCP tool was invoked by a turn that carries no staging capability.
        /// </summary>
        /// <remarks>
        /// MCP-only. The operator API has authenticated authority and never reaches this code; a tool
        /// call reaches it whenever the feature, the tier, the invocation, or the tool policy stopped
        /// permitting a mutation between advertisement and dispatch (§10.14).
        /// </remarks>
        public const string IneligibleTurn = "Covenant.IneligibleTurn";

    }

}
