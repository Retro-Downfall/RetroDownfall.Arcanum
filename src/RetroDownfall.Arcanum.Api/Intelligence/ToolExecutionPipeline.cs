using System.Buffers;

using System.Diagnostics.CodeAnalysis;

using System.Globalization;

using System.Runtime.CompilerServices;

using System.Text;

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Sanctum;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Telemetry;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Workspaces;

using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Shared ward/Sanctum validation, tool invocation, and per-call message shaping used by both
/// buffered and streaming inference paths in <see cref="WizardIntelligenceProvider"/>.
/// </summary>
public sealed class ToolExecutionPipeline(
    IOptionsSnapshot<ArcanumSettings> settings,
    IWard ward,
    ISanctumGuard sanctumGuard,
    ISessionAttachmentStore sessionAttachmentStore,
    ILogger<ToolExecutionPipeline> logger,
    IToolResultMaterializer? toolResultMaterializer = null,
    GrimoireTurnWriter? grimoireTurnWriter = null,
    IAttachmentSourceResolver? attachmentSourceResolver = null,
    CovenantToolEgressGuard? covenantEgressGuard = null,
    ICovenantAuthoritySnapshotProvider? covenantAuthority = null)
{

    /// <summary>
    /// Synthesized tool result text when an unexpected (infrastructure-fault) exception is tolerated
    /// rather than failing the whole turn under the code-owned tolerant mode policy. The model sees
    /// this text as the tool's result and can decide how to proceed (retry, apologize, try a
    /// different approach) instead of the turn failing outright with <c>Hub.Error</c>. Exact wording
    /// is a contract with the model prompt, not just a log message — do not change casually.
    /// </summary>
    public static string PublicToolFailureMessage(string toolName) =>
        $"[Tool error: {toolName} failed with an internal error. The operator has been notified.]";

    private const string WardTimeoutReason =
        "The ward held until timeout — action was not allowed";

    /// <summary>
    /// Code-owned reason attached to a Ward the operator pre-authorized through
    /// <c>Arcanum:Security:Ward:AutoApprove</c>. It is a contract string clients and audit consumers
    /// may match on, and it deliberately carries no tool arguments, paths, or model text.
    /// </summary>
    private const string AutoApprovedReason =
        "Auto-approved by operator policy — containment checks still applied";

    private const string ApplyPatchSessionRequiredResult =
        "{\"status\":\"invalid_request\",\"code\":\"session_required\",\"message\":\"apply_patch requires a bound persisted session and assistant-turn invocation.\"}";

    public sealed class TurnContext
    {

        public Campaign? Campaign { get; init; }

        public string? CampaignId { get; init; }

        public string? WorkspaceRoot { get; init; }

        public Guid? PersistedSessionId { get; init; }

        public Guid? AssistantEntryId { get; init; }

        public string? InvocationId { get; init; }

        public string? ModelUsed { get; init; }

        public bool CampaignRequiresWard { get; init; }

        /// <summary>The invocation this turn runs under, or absent on a surface that has none.</summary>
        /// <remarks>
        /// Carried rather than re-derived, because <c>CanReadCovenant</c> and
        /// <c>CanStageCovenantMutation</c> are the only eligibility answers in the system and restating
        /// the truth table at this seam is how two callers come to disagree about what an unattended
        /// stateless preview may do.
        /// </remarks>
        public ArcanumInvocationContext? Invocation { get; init; }

        public bool SanctumEnabled { get; init; }

        public SanctumMode SanctumMode { get; init; }

        public IReadOnlyList<AITool> InferenceTools { get; init; } = [];

        public IReadOnlySet<Guid>? VisibleAttachmentIds { get; init; }

        /// <summary>
        /// Full <c>scripts/</c> roots the spell-script tool will resolve against (active spell + resonant
        /// dependencies). The Sanctum preflight validates every candidate root, not just the active spell's.
        /// </summary>
        public IReadOnlyList<string> SpellScriptRoots { get; init; } = [];

    }

    public sealed record WardedToolExecutionResult(
        string ResultText,
        IReadOnlyList<IntelligenceEvent> WardEvents,
        bool Denied = false,
        bool Failed = false);

    public sealed record ProcessedToolCall(
        string CallId,
        string ToolName,
        string ArgsSnapshot,
        string ResultText,
        IReadOnlyList<IntelligenceEvent> WardEvents,
        bool Failed = false,
        IReadOnlyList<AIContent>? AdditionalContextContents = null,
        bool ReceiptHandled = false,
        bool Denied = false,
        AttachmentRefreshEvent? AttachmentRefresh = null);

    public static List<FunctionCallContent> CollectActionableFunctionCalls(ChatResponse response)
    {

        return CollectFunctionCalls(response)
            .Where(static c => !c.InformationalOnly)
            .ToList();

    }

    private static readonly ConditionalWeakTable<FunctionCallContent, string> _fallbackCallIds = new();

    public string ResolveCallId(FunctionCallContent fcc)
    {

        if (!string.IsNullOrEmpty(fcc.CallId))
        {

            return fcc.CallId;

        }

        if (!_fallbackCallIds.TryGetValue(fcc, out string? fallbackId))
        {

            fallbackId = Guid.NewGuid().ToString("N");

            _fallbackCallIds.Add(fcc, fallbackId);

            logger.LogWarning(
                "Provider returned a tool call with an empty id for tool '{ToolName}'; assigning fallback id {FallbackId}.",
                fcc.Name,
                fallbackId);

        }

        return fallbackId;

    }

    public static string SerializeToolArgumentsForGrimoire(FunctionCallContent fcc)
    {

        if (fcc.Arguments is null || fcc.Arguments.Count == 0)
        {

            return string.Empty;

        }

        ArrayBufferWriter<byte> buffer = new(256);

        using (Utf8JsonWriter writer = new(buffer))
        {

            writer.WriteStartObject();

            foreach (KeyValuePair<string, object?> pair in fcc.Arguments)
            {

                if (string.Equals(
                        pair.Key,
                        SessionAttachmentToolAmbient.OpaqueInvocationTokenArgumentName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                writer.WritePropertyName(pair.Key);

                WriteArgumentValue(writer, pair.Value);

            }

            writer.WriteEndObject();

        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);

    }

    public static string FormatToolCallEventData(FunctionCallContent fcc, string argsSnapshot)
    {

        return string.IsNullOrEmpty(argsSnapshot) ? fcc.Name ?? string.Empty : $"{fcc.Name}: {argsSnapshot}";

    }

    /// <summary>
    /// The fixed <c>tool_name</c> label used when the model named a tool that is not in the
    /// advertised set. Echoing the model's own string there would make the label unbounded.
    /// </summary>
    internal const string UnregisteredToolMetricLabel = "unregistered";

    /// <summary>
    /// Records <c>arcanum_tool_invocations_total</c>. <paramref name="outcome"/> is one of
    /// <c>success</c>, <c>denied</c> (ward or Sanctum blocked the call), or <c>error</c> (the tool
    /// invocation itself threw, or the model named a tool that was never registered). The configured
    /// MCP/local tool set is finite and an unregistered name collapses to
    /// <see cref="UnregisteredToolMetricLabel"/>, so <paramref name="toolName"/> is a bounded label
    /// value by construction.
    /// </summary>
    /// <summary>
    /// A call that resolved to no registered function never ran, so it is an <c>error</c> rather
    /// than a <c>success</c> even though the pipeline hands the model a synthetic string result.
    /// </summary>
    private static string ResolveInvocationOutcome(bool isRegisteredTool, bool denied) =>
        denied ? "denied"
        : isRegisteredTool ? "success"
        : "error";

    private static void RecordToolInvocationMetric(string toolName, string outcome)
    {
        ArcanumMetrics.ToolInvocationsTotal.Add(
            1,
            new KeyValuePair<string, object?>("tool_name", toolName),
            new KeyValuePair<string, object?>("outcome", outcome));

    }

    /// <summary>
    /// Records <c>arcanum_ward_decisions_total</c>. Both labels are closed by construction — the
    /// configured tool set is finite and <paramref name="origin"/> is an enum — so no model-supplied
    /// text (arguments, paths, prompts, or free-text resolution reasons) reaches the metric.
    /// </summary>
    private static void RecordWardDecisionMetric(string toolName, WardResolutionOrigin origin)
    {
        ArcanumMetrics.WardDecisionsTotal.Add(
            1,
            new KeyValuePair<string, object?>("tool_name", toolName),
            new KeyValuePair<string, object?>("origin", WardResolutionOrigins.ToMetricLabel(origin)));

    }

    public static void AppendToolExchangeToMessages(
        List<MeAiChatMessage> chatMessages,
        FunctionCallContent fcc,
        string callId,
        string resultText,
        IReadOnlyList<TextReasoningContent>? reasoningContents = null)
    {

        FunctionCallContent normalizedCall = string.IsNullOrEmpty(fcc.CallId)
            ? new FunctionCallContent(callId, fcc.Name, fcc.Arguments)
            : fcc;

        List<AIContent> assistantContents = new((reasoningContents?.Count ?? 0) + 1);

        if (reasoningContents is not null)
        {
            // Preserve the provider's original objects (including opaque ProtectedData) only in
            // this in-memory, same-provider continuation chain.
            assistantContents.AddRange(reasoningContents);
        }

        assistantContents.Add(normalizedCall);

        chatMessages.Add(new MeAiChatMessage(ChatRole.Assistant, assistantContents));

        chatMessages.Add(
            new MeAiChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, resultText)]));

    }

    public async Task<ProcessedToolCall> ProcessSingleToolCallAsync(
        FunctionCallContent fcc,
        PingRequest request,
        ChatOptions chatOptions,
        ParsedSpell? activeSpell,
        string? sessionId,
        TurnContext turnContext,
        bool suppressInvocationFailures,
        CancellationToken cancellationToken,
        Func<IntelligenceEvent, CancellationToken, Task>? liveWardEmit = null,
        Func<ToolExecutionEvent, ValueTask>? observer = null,
        string? argumentsSnapshot = null,
        int toolRoundOrdinal = 0,
        int callOrdinal = 0)
    {

        string argsSnapshot =
            argumentsSnapshot ?? SerializeToolArgumentsForGrimoire(fcc);

        string? providerToolCallId = string.IsNullOrEmpty(fcc.CallId)
            ? null
            : fcc.CallId;
        string callId = ResolveCallId(fcc);

        string toolName = fcc.Name ?? string.Empty;

        // A model can name a tool that was never advertised. InvokeToolCallAsync answers such a call
        // with a synthetic "no local tool registered" string rather than invoking anything, so the
        // metric must not report it as a success, and the model's arbitrary string must not become
        // the label value.
        bool isRegisteredTool = ResolveRegisteredFunction(chatOptions, fcc.Name) is not null;

        string metricToolName = isRegisteredTool ? toolName : UnregisteredToolMetricLabel;

        bool isApplyPatch = string.Equals(
                toolName,
                ToolRiskClassifier.ApplyPatchToolName,
                StringComparison.OrdinalIgnoreCase);

        if (isApplyPatch
            && (turnContext.PersistedSessionId is not { } persistedSessionId
                || persistedSessionId == Guid.Empty
                || turnContext.AssistantEntryId is not { } assistantEntryId
                || assistantEntryId == Guid.Empty
                || string.IsNullOrWhiteSpace(turnContext.InvocationId)
                || string.IsNullOrWhiteSpace(turnContext.ModelUsed)
                || grimoireTurnWriter is null))
        {
            return new ProcessedToolCall(
                callId,
                toolName,
                argsSnapshot,
                ApplyPatchSessionRequiredResult,
                []);
        }

        ApplyPatchInvocationContext? applyPatchContext = null;

        if (isApplyPatch)
        {
            applyPatchContext = new ApplyPatchInvocationContext(
                turnContext.PersistedSessionId!.Value,
                turnContext.AssistantEntryId!.Value,
                new ToolInvocationIdentity(
                    turnContext.InvocationId!,
                    providerToolCallId,
                    toolRoundOrdinal,
                    callOrdinal,
                    toolName),
                argsSnapshot,
                turnContext.ModelUsed!,
                DateTimeOffset.UtcNow,
                new GrimoirePendingReceiptSink(grimoireTurnWriter!));
        }

        PersistedToolInvocationContext? persistedToolContext =
            turnContext.PersistedSessionId is { } boundSessionId
            && boundSessionId != Guid.Empty
            && turnContext.AssistantEntryId is { } boundAssistantEntryId
            && boundAssistantEntryId != Guid.Empty
                ? new PersistedToolInvocationContext(
                    boundSessionId,
                    boundAssistantEntryId)
                : null;

        using IDisposable? persistedToolScope =
            persistedToolContext is null
                ? null
                : PersistedToolInvocationAmbient.Begin(
                    persistedToolContext);

        using IDisposable? applyPatchScope = applyPatchContext is null
            ? null
            : ApplyPatchInvocationAmbient.Begin(applyPatchContext);

        WardedToolExecutionResult wardedExecution;

        // Owned here rather than inside ExecuteToolCallWithWardAsync so the tolerant catches below
        // can still report the frames a Ward already produced when the invocation itself throws.
        List<IntelligenceEvent> wardEvents = new(2);

        if (suppressInvocationFailures)
        {

            try
            {

                wardedExecution = await ExecuteToolCallWithWardAsync(
                    fcc,
                    request,
                    chatOptions,
                    activeSpell,
                    sessionId,
                    turnContext,
                    argsSnapshot,
                    metricToolName,
                    wardEvents,
                    cancellationToken,
                    liveWardEmit,
                    observer)
                    .ConfigureAwait(false);

                RecordToolInvocationMetric(
                    metricToolName,
                    ResolveInvocationOutcome(isRegisteredTool, wardedExecution.Denied));

            }
            catch (OperationCanceledException)
            {

                throw;

            }
            catch (Exception) when (
                applyPatchContext?.RequiresTurnFailure == true
                || applyPatchContext?.CancellationClassified == true)
            {
                RecordToolInvocationMetric(metricToolName, "error");
                throw;
            }
            catch (HumanPromptTimeoutException ex)
            {

                wardedExecution = new WardedToolExecutionResult(ex.Message, wardEvents, Failed: true);

                RecordToolInvocationMetric(metricToolName, "error");

            }
            catch (Exception ex)
            {

                logger.LogError(
                    "Tool {ToolName} failed during inference (tolerated by mode policy); exception type {ExceptionType}, call {ToolCallId}.",
                    toolName,
                    ex.GetType().FullName,
                    callId);

                wardedExecution = new WardedToolExecutionResult(PublicToolFailureMessage(toolName), wardEvents, Failed: true);

                RecordToolInvocationMetric(metricToolName, "error");

            }

        }
        else
        {

            // Unlike the streaming branch above, the buffered path does not suppress invocation
            // failures — an exception here still propagates to the caller unchanged. This try/catch
            // exists solely to record the "error" outcome symmetrically before rethrowing.
            try
            {

                wardedExecution = await ExecuteToolCallWithWardAsync(
                    fcc,
                    request,
                    chatOptions,
                    activeSpell,
                    sessionId,
                    turnContext,
                    argsSnapshot,
                    metricToolName,
                    wardEvents,
                    cancellationToken,
                    liveWardEmit,
                    observer)
                    .ConfigureAwait(false);

                RecordToolInvocationMetric(
                    metricToolName,
                    ResolveInvocationOutcome(isRegisteredTool, wardedExecution.Denied));

            }
            catch (OperationCanceledException)
            {

                throw;

            }
            catch (Exception)
            {

                RecordToolInvocationMetric(metricToolName, "error");

                throw;

            }

        }

        IReadOnlyList<AIContent>? additionalContext = null;

        string resultText = wardedExecution.ResultText;

        AttachmentRefreshEvent? attachmentRefresh = null;

        bool executedSuccessfully = !wardedExecution.Failed && !wardedExecution.Denied;

        if (executedSuccessfully
            && string.Equals(toolName, "attach_session_file", StringComparison.Ordinal)
            && SessionAttachmentToolAmbient.CurrentSessionId is { } ambientSessionId
            && SessionAttachmentToolInjection.TryParseAttachArguments(fcc.Arguments, out string logicalName, out int? version))
        {

            try
            {

                additionalContext = await SessionAttachmentToolInjection
                    .TryBuildContentsAsync(
                        sessionAttachmentStore,
                        ambientSessionId,
                        logicalName,
                        version,
                        settings.Value,
                        request.Model,
                        cancellationToken)
                    .ConfigureAwait(false);

            }
            catch (OperationCanceledException)
            {

                throw;

            }
            catch (Exception ex)
            {

                if (suppressInvocationFailures)
                {

                    logger.LogError(
                        "attach_session_file post-process failed during inference (tolerated by mode policy); exception type {ExceptionType}, call {ToolCallId}.",
                        ex.GetType().FullName,
                        callId);

                    RecordToolInvocationMetric(metricToolName, "error");

                    return new ProcessedToolCall(
                        callId,
                        toolName,
                        argsSnapshot,
                        PublicToolFailureMessage(toolName),
                        wardedExecution.WardEvents,
                        Failed: true,
                        AdditionalContextContents: null);

                }

                RecordToolInvocationMetric(metricToolName, "error");

                throw;

            }

        }
        else if (executedSuccessfully
            && string.Equals(toolName, "refresh_session_file", StringComparison.Ordinal))
        {
            try
            {
                RefreshPostProcess refresh = await ProcessRefreshSessionFileAsync(
                        fcc,
                        request,
                        turnContext,
                        cancellationToken)
                    .ConfigureAwait(false);
                resultText = refresh.ResultText;
                additionalContext = refresh.AdditionalContext;
                attachmentRefresh = refresh.Event;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (suppressInvocationFailures)
                {
                    logger.LogError(
                        "refresh_session_file post-process failed during inference (tolerated by mode policy); exception type {ExceptionType}, call {ToolCallId}.",
                        ex.GetType().FullName,
                        callId);
                    RecordToolInvocationMetric(metricToolName, "error");
                    return new ProcessedToolCall(
                        callId,
                        toolName,
                        argsSnapshot,
                        PublicToolFailureMessage(toolName),
                        wardedExecution.WardEvents,
                        Failed: true);
                }

                RecordToolInvocationMetric(metricToolName, "error");
                throw;
            }
        }

        return new ProcessedToolCall(
            callId,
            toolName,
            argsSnapshot,
            resultText,
            wardedExecution.WardEvents,
            wardedExecution.Failed,
            additionalContext,
            applyPatchContext?.ReceiptHandled == true,
            wardedExecution.Denied,
            attachmentRefresh);

    }

    private sealed record RefreshPostProcess(
        string ResultText,
        IReadOnlyList<AIContent>? AdditionalContext,
        AttachmentRefreshEvent? Event);

    private async Task<RefreshPostProcess> ProcessRefreshSessionFileAsync(
        FunctionCallContent call,
        PingRequest request,
        TurnContext turnContext,
        CancellationToken cancellationToken)
    {
        if (attachmentSourceResolver is null)
        {
            throw new InvalidOperationException("No attachment source resolver is available.");
        }

        if (SessionAttachmentToolAmbient.CurrentSessionId is not { } sessionId)
        {
            return RefreshError("session_required", "No current session is available for attachment refresh.");
        }

        if (!TryParseRefreshSelector(call.Arguments, out Guid? attachmentId, out string? logicalKey))
        {
            return RefreshError(
                "invalid_selector",
                "Exactly one of attachmentId or logicalKey is required.");
        }

        return await ProcessRefreshSessionFileCoreAsync(

                sessionId,

                attachmentId,

                logicalKey,

                request,

                turnContext,

                includeAdditionalContext: true,

                cancellationToken)

            .ConfigureAwait(false);

    }

    public async Task<Result<AttachmentRefreshEvent>> RefreshSessionAttachmentAsync(

        Guid sessionId,

        Guid attachmentId,

        Guid? campaignId,

        CancellationToken cancellationToken = default)

    {

        if (sessionId == Guid.Empty || attachmentId == Guid.Empty)

        {

            return Result<AttachmentRefreshEvent>.Failure(

                new Error(ErrorCodes.Attachment.InvalidRequest, "A session and attachment are required."));

        }

        if (!settings.Value.ResolveAttachments().Enabled)

        {

            return Result<AttachmentRefreshEvent>.Failure(

                new Error(

                    ErrorCodes.Attachment.Disabled,

                    "Session attachments are disabled."));

        }

        PingRequest request = new(

            Prompt: string.Empty,

            Model: settings.Value.DefaultModel,

            SessionId: sessionId,

            CampaignId: campaignId);

        TurnContext context = new()

        {

            CampaignId = campaignId?.ToString("D", CultureInfo.InvariantCulture),

            SanctumMode = SanctumMode.Strict,

        };

        RefreshPostProcess refreshed = await ProcessRefreshSessionFileCoreAsync(

                sessionId,

                attachmentId,

                logicalKey: null,

                request,

                context,

                includeAdditionalContext: false,

                cancellationToken)

            .ConfigureAwait(false);

        if (refreshed.Event is { } detail)

        {

            return Result<AttachmentRefreshEvent>.Success(detail);

        }

        RefreshSessionFileResultWire? failure = JsonSerializer.Deserialize(

            refreshed.ResultText,

            McpJsonSerializerContext.Default.RefreshSessionFileResultWire);

        return Result<AttachmentRefreshEvent>.Failure(

            new Error(

                MapAttachmentRefreshErrorCode(failure?.ErrorCode),

                failure?.Message ?? "The tracked attachment could not be refreshed."));

    }

    private static string MapAttachmentRefreshErrorCode(string? errorCode) =>

        errorCode switch

        {

            "attachment_not_visible" => ErrorCodes.Attachment.NotFound,

            "source_unavailable" => ErrorCodes.Attachment.SourceUnavailable,

            "image_policy_denied" or "invalid_content" => ErrorCodes.Attachment.InvalidContent,

            "attachment_budget_exhausted" => ErrorCodes.Attachment.LimitExceeded,

            "ambiguous_logical_key" => ErrorCodes.Attachment.InvalidRequest,

            _ => ErrorCodes.Attachment.InvalidReference,

        };

    private async Task<RefreshPostProcess> ProcessRefreshSessionFileCoreAsync(

        Guid sessionId,

        Guid? attachmentId,

        string? logicalKey,

        PingRequest request,

        TurnContext turnContext,

        bool includeAdditionalContext,

        CancellationToken cancellationToken)

    {

        IAttachmentSourceResolver sourceResolver = attachmentSourceResolver

            ?? throw new InvalidOperationException("No attachment source resolver is available.");

        IReadOnlyList<SessionAttachmentRecord> bound = await sessionAttachmentStore
            .ListBoundAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        IEnumerable<SessionAttachmentRecord> visible = turnContext.VisibleAttachmentIds is { } visibleIds
            ? bound.Where(row =>
                visibleIds.Contains(row.Id)
                || turnContext.AssistantEntryId is { } assistantEntryId
                    && row.EntryId == assistantEntryId)
            : bound;
        SessionAttachmentRecord? selected;

        if (attachmentId is { } id)
        {
            selected = visible.FirstOrDefault(row => row.Id == id);
        }
        else
        {
            List<string> matchingKeys = visible
                .Select(static row => row.LogicalKey)
                .Distinct(StringComparer.Ordinal)
                .Where(key => string.Equals(key, logicalKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matchingKeys.Count > 1)
            {
                return RefreshError(
                    "ambiguous_logical_key",
                    "The logicalKey selector is ambiguous in the current logical turn.");
            }

            selected = matchingKeys.Count == 1
                ? bound
                    .Where(row => string.Equals(row.LogicalKey, matchingKeys[0], StringComparison.Ordinal))
                    .OrderByDescending(static row => row.Version)
                    .FirstOrDefault()
                : null;
        }

        if (selected is null
            || selected.State != SessionAttachmentState.Bound
            || selected.SessionId != sessionId)
        {
            return RefreshError(
                "attachment_not_visible",
                "The selected attachment is not bound and visible to the current logical turn.");
        }

        SessionAttachmentRecord latest = bound
            .Where(row => string.Equals(row.LogicalKey, selected.LogicalKey, StringComparison.Ordinal))
            .OrderByDescending(static row => row.Version)
            .First();

        if (latest.Source is not { Kind: AttachmentSourceKind.WorkspaceFile } source
            || string.IsNullOrWhiteSpace(source.WorkspaceRelativePath)
            || string.IsNullOrWhiteSpace(source.WorkspaceIdentity))
        {
            return RefreshError(
                "source_unavailable",
                "The selected attachment is snapshot-only or lacks verified refreshable source metadata.");
        }

        long maxBytes = SessionAttachmentContentPolicy.ResolveMaximumReadBytes(settings.Value);

        async Task<bool> AuthorizePathAsync(string canonicalPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(turnContext.CampaignId))
            {
                return true;
            }

            SanctumResult result = await sanctumGuard.ValidatePathAsync(
                    turnContext.CampaignId,
                    canonicalPath,
                    "attachment source read",
                    "refresh_session_file",
                    ct)
                .ConfigureAwait(false);
            return result.Allowed || turnContext.SanctumMode == SanctumMode.AuditOnly;
        }

        AttachmentSourceResolution current = await sourceResolver.ResolveCurrentAsync(
                source,
                latest.ContentSha256,
                maxBytes,
                AuthorizePathAsync,
                cancellationToken)
            .ConfigureAwait(false);

        if (current.Metadata.Status is not (AttachmentSourceStatus.Refreshable or AttachmentSourceStatus.PriorVersion))
        {
            return RefreshError(
                "source_unavailable",
                current.Metadata.DiagnosticReason ?? "The attachment source could not be safely refreshed.");
        }

        string? contentError = ValidateRefreshedContent(
            current,
            settings.Value,
            out SessionAttachmentKind refreshedKind);

        if (contentError is not null)
        {
            return RefreshError("invalid_content", contentError);
        }

        if (includeAdditionalContext && refreshedKind == SessionAttachmentKind.Image)
        {
            SessionAttachmentRecord refreshedImage = latest with
            {
                MimeType = current.DetectedMimeType!,

                ByteLength = current.VerifiedBytes.Length,

                Kind = refreshedKind,
            };

            if (SessionAttachmentToolInjection.ValidateImageAttach(
                    refreshedImage,
                    refreshedImage.ByteLength,
                    settings.Value,
                    request.Model) is { } imagePolicyError)
            {
                return RefreshError("image_policy_denied", imagePolicyError);
            }
        }

        SessionAttachmentRefreshPersistence persisted;
        try
        {
            persisted = await sessionAttachmentStore.PersistRefreshedAsync(
                    latest,
                    turnContext.AssistantEntryId,
                    current,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return RefreshError("attachment_budget_exhausted", ex.Message);
        }

        IReadOnlyList<AIContent>? additional = includeAdditionalContext

            ? await SessionAttachmentToolInjection

                .TryBuildRefreshedContentsAsync(

                    sessionAttachmentStore,

                    persisted.Record,

                    settings.Value,

                    request.Model,

                    cancellationToken)

                .ConfigureAwait(false)

            : null;
        bool queued = additional is { Count: > 0 };
        AttachmentSourceMetadata persistedSource = persisted.Record.Source ?? current.Metadata;
        DateTimeOffset freshness = persistedSource.LastObservedWriteTime ?? DateTimeOffset.UtcNow;
        string safePath = SystemPromptBuilder.HardenAttachmentIndexName(
            persistedSource.WorkspaceRelativePath ?? persisted.Record.OriginalFileName);
        AttachmentRefreshEvent detail = new(
            persisted.Record.Id,
            persisted.Record.LogicalKey,
            persisted.Record.Version,
            persisted.NewVersionCreated,
            queued,
            safePath,
            persisted.Record.ContentSha256,
            persisted.Record.ByteLength,
            freshness);
        RefreshSessionFileResultWire wire = new(
            Success: true,
            persisted.Record.Id,
            persisted.Record.LogicalKey,
            persisted.Record.Version,
            persisted.NewVersionCreated,
            queued,
            safePath,
            persisted.Record.ContentSha256,
            persisted.Record.ByteLength,
            freshness);
        string text = JsonSerializer.Serialize(
            wire,
            McpJsonSerializerContext.Default.RefreshSessionFileResultWire);
        return new RefreshPostProcess(text, additional, detail);
    }

    private static RefreshPostProcess RefreshError(string code, string message)
    {
        RefreshSessionFileResultWire wire = new(
            Success: false,
            AttachmentId: null,
            LogicalKey: null,
            Version: null,
            NewVersionCreated: false,
            QueuedForInjection: false,
            SanitizedSourcePath: null,
            ContentSha256: null,
            ByteLength: null,
            SourceFreshnessTimestamp: null,
            ErrorCode: code,
            Message: message);
        return new RefreshPostProcess(
            JsonSerializer.Serialize(wire, McpJsonSerializerContext.Default.RefreshSessionFileResultWire),
            AdditionalContext: null,
            Event: null);
    }

    private static bool TryParseRefreshSelector(
        IDictionary<string, object?>? arguments,
        out Guid? attachmentId,
        out string? logicalKey)
    {
        attachmentId = null;
        logicalKey = null;
        if (arguments is null)
        {
            return false;
        }

        foreach ((string key, object? raw) in arguments)
        {
            if (string.Equals(key, "attachmentId", StringComparison.OrdinalIgnoreCase))
            {
                string? text = raw switch
                {
                    Guid guid => guid.ToString("D"),
                    JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
                    _ => raw?.ToString(),
                };
                if (Guid.TryParse(text, out Guid parsed) && parsed != Guid.Empty)
                {
                    attachmentId = parsed;
                }
            }
            else if (string.Equals(key, "logicalKey", StringComparison.OrdinalIgnoreCase))
            {
                logicalKey = raw switch
                {
                    JsonElement { ValueKind: JsonValueKind.String } json => json.GetString()?.Trim(),
                    _ => raw?.ToString()?.Trim(),
                };
            }
        }

        bool byId = attachmentId is not null;
        bool byLogical = !string.IsNullOrWhiteSpace(logicalKey);
        return byId != byLogical;
    }

    private static string? ValidateRefreshedContent(
        AttachmentSourceResolution current,
        ArcanumSettings settings,
        out SessionAttachmentKind refreshedKind)
    {
        if (string.IsNullOrWhiteSpace(current.DetectedMimeType))
        {
            refreshedKind = default;

            return "Attachment content type is required.";
        }

        refreshedKind = SessionAttachmentContentPolicy.Classify(current.DetectedMimeType);

        return SessionAttachmentContentPolicy.Validate(
            refreshedKind,
            current.VerifiedBytes,
            current.DetectedMimeType,
            settings);
    }

    private static List<FunctionCallContent> CollectFunctionCalls(ChatResponse response)
    {

        var results = new List<FunctionCallContent>();

        foreach (MeAiChatMessage message in response.Messages)
        {

            AppendFunctionCallsFromContents(message.Contents, results);

        }

        return results;

    }

    private static void AppendFunctionCallsFromContents(IList<AIContent>? contents, List<FunctionCallContent> sink)
    {

        if (contents is null)
        {

            return;

        }

        foreach (AIContent item in contents)
        {

            if (item is FunctionCallContent fcc)
            {

                sink.Add(fcc);

            }

        }

    }

    private static void WriteArgumentValue(Utf8JsonWriter writer, object? value)
    {

        switch (value)
        {

            case null:
                writer.WriteNullValue();
                break;

            case JsonElement je:
                je.WriteTo(writer);
                break;

            case string s:
                writer.WriteStringValue(s);
                break;

            case bool b:
                writer.WriteBooleanValue(b);
                break;

            case sbyte sb:
                writer.WriteNumberValue(sb);
                break;

            case byte by:
                writer.WriteNumberValue(by);
                break;

            case short sh:
                writer.WriteNumberValue(sh);
                break;

            case ushort us:
                writer.WriteNumberValue(us);
                break;

            case int i:
                writer.WriteNumberValue(i);
                break;

            case uint ui:
                writer.WriteNumberValue(ui);
                break;

            case long l:
                writer.WriteNumberValue(l);
                break;

            case ulong ul:
                writer.WriteNumberValue(ul);
                break;

            case double d:
                writer.WriteNumberValue(d);
                break;

            case float f:
                writer.WriteNumberValue(f);
                break;

            case decimal dec:
                writer.WriteNumberValue(dec);
                break;

            case DateTime dt:
                writer.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture));
                break;

            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture));
                break;

            case Guid g:
                writer.WriteStringValue(g.ToString("D", CultureInfo.InvariantCulture));
                break;

            case Uri uri:
                writer.WriteStringValue(uri.ToString());
                break;

            case IReadOnlyDictionary<string, object?> dict:
                writer.WriteStartObject();

                foreach (KeyValuePair<string, object?> kv in dict)
                {

                    writer.WritePropertyName(kv.Key);

                    WriteArgumentValue(writer, kv.Value);

                }

                writer.WriteEndObject();
                break;

            case System.Collections.IEnumerable enumerable:
                writer.WriteStartArray();

                foreach (object? item in enumerable)
                {

                    WriteArgumentValue(writer, item);

                }

                writer.WriteEndArray();
                break;

            default:
                writer.WriteStringValue(value.ToString());
                break;

        }

    }

    private static AIFunction? ResolveRegisteredFunction(ChatOptions chatOptions, string? functionName)
    {

        if (string.IsNullOrEmpty(functionName) || chatOptions.Tools is null)
        {

            return null;

        }

        foreach (AITool tool in chatOptions.Tools)
        {

            if (tool is AIFunction fn && string.Equals(fn.Name, functionName, StringComparison.Ordinal))
            {

                return fn;

            }

        }

        return null;

    }

    private static async Task<object?> InvokeToolCallAsync(
        FunctionCallContent fcc,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {

        AIFunction? func = ResolveRegisteredFunction(chatOptions, fcc.Name);

        if (func is null)
        {

            return $"No local tool registered for '{fcc.Name}'.";

        }

        AIFunctionArguments args = fcc.Arguments is { Count: > 0 }
            ? new AIFunctionArguments(fcc.Arguments)
            : [];

        object? output = await func
            .InvokeAsync(args, cancellationToken)
            .ConfigureAwait(false);

        return output;

    }

    /// <summary>
    /// <paramref name="wardEvents"/> is caller-owned so that buffered <c>warded</c>/<c>wardResolved</c>
    /// frames survive an exception out of the invocation: the tolerant catches in
    /// <see cref="ProcessSingleToolCallAsync"/> still have to report that a Ward was raised and how
    /// the operator resolved it.
    /// </summary>
    private async Task<WardedToolExecutionResult> ExecuteToolCallWithWardAsync(
        FunctionCallContent fcc,
        PingRequest request,
        ChatOptions chatOptions,
        ParsedSpell? activeSpell,
        string? sessionId,
        TurnContext turnContext,
        string argsSnapshot,
        string metricToolName,
        List<IntelligenceEvent> wardEvents,
        CancellationToken cancellationToken,
        Func<IntelligenceEvent, CancellationToken, Task>? liveWardEmit = null,
        Func<ToolExecutionEvent, ValueTask>? observer = null)
    {

        string toolName = fcc.Name ?? string.Empty;

        WardSettings wardSettings = settings.Value.ResolveWard();

        // Decision order (DESIGN §11.14): classify → hard deny → auto-approve → interactive. Deny
        // wins, so the unattended auto-deny check runs before the auto-approval allowlist is even
        // consulted; a tool matching both is denied in attended and unattended modes alike.
        if (IsWardCandidate(toolName, turnContext.CampaignRequiresWard, wardSettings)
            && request.UnattendedMode
            && wardSettings.AutoDenyInUnattendedMode)
        {

            RecordWardDecisionMetric(metricToolName, WardResolutionOrigin.AutoDenied);

            return new WardedToolExecutionResult(UnattendedDenyMessage(toolName), wardEvents, Denied: true);

        }

        // Before the not-a-Forbidden-Art exit, because with Wards switched off that exit sends a
        // retirement straight to invocation, where the handler refuses it with the wrong answer. The
        // Covenant policy denies it instead: switching Wards off removes the operator's only chance to
        // refuse, and silence is not consent to erase their own standing instructions.
        if (string.Equals(toolName, CovenantToolNames.RetireCovenant, StringComparison.Ordinal))
        {

            return await ExecuteCovenantRetirementAsync(
                    fcc,
                    request,
                    chatOptions,
                    activeSpell,
                    turnContext,
                    argsSnapshot,
                    wardEvents,
                    wardSettings,
                    sessionId,
                    cancellationToken,
                    liveWardEmit,
                    observer)
                .ConfigureAwait(false);

        }

        if (!IsForbiddenArt(request, toolName, turnContext.CampaignRequiresWard, wardSettings))
        {

            string recordWardId = Guid.NewGuid().ToString();

            using JsonDocument? recordArgsDocument = BuildWardArgumentsDocument(
                toolName,
                argsSnapshot);

            JsonElement? recordWardArguments = recordArgsDocument?.RootElement.Clone();

            IntelligenceEvent recordWardedEvent = new(
                IntelligenceEventType.Warded,
                toolName,
                null,
                null,
                null,
                recordWardId,
                toolName,
                recordWardArguments,
                null,
                null,
                DateTimeOffset.UtcNow,
                WardOrigin: WardResolutionOrigin.Ungated);

            await EmitWardEventAsync(recordWardedEvent, wardEvents, liveWardEmit, cancellationToken).ConfigureAwait(false);

            WardResolution recordResolution = ward.RecordAutomaticResolution(
                recordWardId,
                allowed: true,
                reason: null,
                WardResolutionOrigin.Ungated);

            RecordWardDecisionMetric(metricToolName, recordResolution.Origin);

            IntelligenceEvent recordResolvedEvent = new(
                IntelligenceEventType.WardResolved,
                toolName,
                null,
                null,
                null,
                recordWardId,
                toolName,
                null,
                recordResolution.Allowed,
                recordResolution.Reason,
                DateTimeOffset.UtcNow,
                WardOrigin: recordResolution.Origin);

            await EmitWardEventAsync(recordResolvedEvent, wardEvents, liveWardEmit, cancellationToken).ConfigureAwait(false);

            if (string.Equals(toolName, "ask_human", StringComparison.Ordinal) && observer is not null)
            {
                string promptText = TryExtractAskHumanPrompt(argsSnapshot);

                await observer(
                        new ToolHumanInputRequestedEvent(ResolveCallId(fcc), promptText))
                    .ConfigureAwait(false);
            }

            (string directResult, bool directDenied) = await InvokeToolCallWithSanctumAsync(
                fcc,
                activeSpell,
                chatOptions,
                turnContext,
                argsSnapshot,
                cancellationToken).ConfigureAwait(false);

            return new WardedToolExecutionResult(directResult, wardEvents, directDenied);

        }

        string wardId = Guid.NewGuid().ToString();

        JsonDocument? argsDocument = BuildWardArgumentsDocument(
            toolName,
            argsSnapshot);

        JsonElement? wardArguments = argsDocument?.RootElement.Clone();

        DateTimeOffset wardTimestamp = DateTimeOffset.UtcNow;

        bool autoApproved = ToolRiskClassifier.IsAutoApproved(toolName, wardSettings);

        IntelligenceEvent wardedEvent = new(
            IntelligenceEventType.Warded,
            toolName,
            null,
            null,
            null,
            wardId,
            toolName,
            wardArguments,
            null,
            null,
            wardTimestamp,
            WardOrigin: autoApproved ? WardResolutionOrigin.AutoApproved : null);

        await EmitWardEventAsync(wardedEvent, wardEvents, liveWardEmit, cancellationToken).ConfigureAwait(false);

        // An auto-approved call has nothing to wait for, so it must not raise the blocking approval
        // request that opens the Command Center Ward modal.
        if (observer is not null && !autoApproved)
        {
            await observer(
                    new ToolApprovalRequestedEvent(
                        wardId,
                        toolName,
                        wardArguments?.GetRawText() ?? argsSnapshot))
                .ConfigureAwait(false);
        }

        int timeoutSeconds = ArcanumSettingClamps.WardTimeoutSeconds(wardSettings.TimeoutSeconds);

        TimeSpan timeout = TimeSpan.FromSeconds(timeoutSeconds);

        WardResolution resolution;

        try
        {

            resolution = autoApproved
                ? ward.RecordAutomaticResolution(
                    wardId,
                    allowed: true,
                    AutoApprovedReason,
                    WardResolutionOrigin.AutoApproved)
                : await ward
                    .WardAsync(wardId, toolName, argsDocument, sessionId, timeout, cancellationToken)
                    .ConfigureAwait(false);

        }
        finally
        {

            argsDocument?.Dispose();

        }

        RecordWardDecisionMetric(metricToolName, resolution.Origin);

        if (autoApproved)
        {
            // Tool name, decision, and the code-owned reason only — never arguments, paths, or any
            // model-supplied text.
            logger.LogInformation(
                "Ward for tool {ToolName} was {WardDecision} by operator auto-approval policy: {WardReason}",
                toolName,
                resolution.Allowed ? "allowed" : "denied",
                resolution.Reason);
        }

        DateTimeOffset resolvedTimestamp = DateTimeOffset.UtcNow;

        IntelligenceEvent resolvedEvent = new(
            IntelligenceEventType.WardResolved,
            toolName,
            null,
            null,
            null,
            wardId,
            toolName,
            null,
            resolution.Allowed,
            resolution.Reason,
            resolvedTimestamp,
            WardOrigin: resolution.Origin);

        await EmitWardEventAsync(resolvedEvent, wardEvents, liveWardEmit, cancellationToken).ConfigureAwait(false);

        if (!resolution.Allowed)
        {

            string denialMessage = string.Equals(resolution.Reason, WardTimeoutReason, StringComparison.Ordinal)
                ? TimeoutDenyMessage(resolution.Reason)
                : OperatorDenyMessage(resolution.Reason);

            return new WardedToolExecutionResult(denialMessage, wardEvents, Denied: true);

        }

        if (string.Equals(toolName, "ask_human", StringComparison.Ordinal) && observer is not null)
        {
            string promptText = TryExtractAskHumanPrompt(argsSnapshot);

            await observer(
                    new ToolHumanInputRequestedEvent(ResolveCallId(fcc), promptText))
                .ConfigureAwait(false);
        }

        (string allowedResult, bool allowedDenied) = await InvokeToolCallWithSanctumAsync(
            fcc,
            activeSpell,
            chatOptions,
            turnContext,
            argsSnapshot,
            cancellationToken).ConfigureAwait(false);

        return new WardedToolExecutionResult(allowedResult, wardEvents, allowedDenied);

    }

    private static string TryExtractAskHumanPrompt(string argsSnapshot)
    {
        if (string.IsNullOrWhiteSpace(argsSnapshot))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsSnapshot);

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("question", out JsonElement question)
                && question.ValueKind == JsonValueKind.String)
            {
                return question.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Fall through — surface raw args when question cannot be parsed.
        }

        return argsSnapshot;
    }

    /// <summary>
    /// Returns the tool result text plus whether Sanctum blocked the call (<c>Denied: true</c>) — the
    /// caller's <see cref="WardedToolExecutionResult.Denied"/> flag (and, ultimately,
    /// <c>arcanum_tool_invocations_total{outcome="denied"}</c>) depends on this, since a Sanctum-strict
    /// block returns a synthetic result string with no corresponding <see cref="IntelligenceEvent"/>.
    /// </summary>
    /// <summary>
    /// Wards, discloses, and dispatches one agent-initiated Covenant retirement.
    /// </summary>
    /// <remarks>
    /// The one tool whose Ward shows the operator something other than the model's arguments. A
    /// retirement erases a standing instruction they wrote, so what they are asked to approve is the
    /// content that will disappear, the lane it lives in, the revision it is, and whether Global content
    /// starts applying in its place — all resolved from canonical state before the question is put.
    ///
    /// <para>Every refusal here happens before the Ward. A turn that may not stage, a target that does
    /// not exist, a tombstone, and a pinned head are each things the write authority would refuse
    /// afterwards, and asking somebody to authorize one of them is asking them to authorize nothing.</para>
    ///
    /// <para>The disclosure is committed before the dispatch and not before the Ward. A Ward is a
    /// question, not an effect; the staging is the effect, and a journal written after it cannot record
    /// the one case it exists for.</para>
    /// </remarks>
    private async Task<WardedToolExecutionResult> ExecuteCovenantRetirementAsync(
        FunctionCallContent fcc,
        PingRequest request,
        ChatOptions chatOptions,
        ParsedSpell? activeSpell,
        TurnContext turnContext,
        string argsSnapshot,
        List<IntelligenceEvent> wardEvents,
        WardSettings wardSettings,
        string? sessionId,
        CancellationToken cancellationToken,
        Func<IntelligenceEvent, CancellationToken, Task>? liveWardEmit,
        Func<ToolExecutionEvent, ValueTask>? observer)
    {

        string toolName = fcc.Name ?? string.Empty;

        if (CovenantToolStagingAmbient.Current is not { } staging
            || turnContext.Invocation is not { } invocation
            || covenantEgressGuard is null)
        {

            return CovenantRetirementDenied(
                "This turn carries no Covenant staging capability, so it cannot retire a standing preference.",
                wardEvents);

        }

        Result<ProviderToolCallClassification> classified = CovenantToolClassifier.ClassifyCovenantTool(
            toolName,
            Encoding.UTF8.GetBytes(argsSnapshot));

        if (classified.IsFailure)
        {

            return CovenantRetirementDenied(classified.Error.Message, wardEvents);

        }

        CovenantEgressWardDecision decision = CovenantEgressWardPolicy.Resolve(
            classified.Value,
            invocation,
            wardSettings);

        if (decision.IsDenied)
        {

            return CovenantRetirementDenied(
                decision.Authorization == CovenantEgressAuthorization.DeniedWardsDisabled
                    ? "Wards are switched off on this installation, so a Covenant retirement cannot be approved and is refused."
                    : "This turn may not stage a Covenant mutation.",
                wardEvents);

        }

        if (!TryReadRetirementTarget(argsSnapshot, out string normalizedKey, out CovenantLane lane))
        {

            return CovenantRetirementDenied(
                "A Covenant retirement names one preference key and a lane of Confirmed or Proposed.",
                wardEvents);

        }

        Result<CovenantRetirementPreflight> resolved = await staging.HeadProbe
            .ResolveRetirementPreflightAsync(lane, normalizedKey, cancellationToken)
            .ConfigureAwait(false);

        if (resolved.IsFailure)
        {

            return CovenantRetirementDenied(resolved.Error.Message, wardEvents);

        }

        CovenantRetirementPreflight preflight = resolved.Value;

        // The carve-out on configured auto-approval. A target this turn never carried is one the
        // operator has not seen in this conversation, so it reaches the interactive Ward rather than
        // approving itself; they may still legitimately want it retired, which is why it is a
        // downgrade rather than a refusal.
        bool admittedHere = WasAdmittedOnThisTurn(staging.ProducingAdmission, preflight);

        if (decision.Authorization == CovenantEgressAuthorization.ConfiguredAutoApproval && !admittedHere)
        {

            decision = new CovenantEgressWardDecision(
                CovenantEgressAuthorization.AttendedWardRequired,
                decision.RiskIdentity,
                CovenantAuthorizationMode.WardInteractive);

        }

        bool autoApproved = decision.Authorization == CovenantEgressAuthorization.ConfiguredAutoApproval;

        string wardId = Guid.NewGuid().ToString();

        // The disclosure, never the model's arguments. It is the whole point of resolving a preflight.
        JsonDocument disclosure = JsonDocument.Parse(
            JsonSerializer.Serialize(
                new CovenantRetirementDisclosureWire(
                    preflight.NormalizedKey,
                    preflight.Lane.ToString(),
                    preflight.TargetLaneRevision,
                    preflight.SanitizedAuthoredDisclosure,
                    preflight.GlobalFallbackApplies),
                ArcanumJsonContext.Default.CovenantRetirementDisclosureWire));

        using (disclosure)
        {

            JsonElement wardArguments = disclosure.RootElement.Clone();

            await EmitWardEventAsync(
                    new IntelligenceEvent(
                        IntelligenceEventType.Warded,
                        toolName,
                        null,
                        null,
                        null,
                        wardId,
                        toolName,
                        wardArguments,
                        null,
                        null,
                        DateTimeOffset.UtcNow,
                        WardOrigin: autoApproved ? WardResolutionOrigin.AutoApproved : null),
                    wardEvents,
                    liveWardEmit,
                    cancellationToken)
                .ConfigureAwait(false);

            WardResolution resolution;

            if (autoApproved)
            {

                resolution = ward.RecordAutomaticResolution(
                    wardId,
                    allowed: true,
                    AutoApprovedReason,
                    WardResolutionOrigin.AutoApproved);

            }
            else
            {

                if (observer is not null)
                {

                    await observer(
                            new ToolApprovalRequestedEvent(wardId, toolName, wardArguments.GetRawText()))
                        .ConfigureAwait(false);

                }

                resolution = await ward
                    .WardAsync(
                        wardId,
                        toolName,
                        disclosure,
                        sessionId,
                        TimeSpan.FromSeconds(ArcanumSettingClamps.WardTimeoutSeconds(wardSettings.TimeoutSeconds)),
                        cancellationToken)
                    .ConfigureAwait(false);

            }

            RecordWardDecisionMetric(toolName, resolution.Origin);

            await EmitWardEventAsync(
                    new IntelligenceEvent(
                        IntelligenceEventType.WardResolved,
                        toolName,
                        null,
                        null,
                        null,
                        wardId,
                        toolName,
                        wardArguments,
                        null,
                        null,
                        DateTimeOffset.UtcNow,
                        WardOrigin: resolution.Origin),
                    wardEvents,
                    liveWardEmit,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!resolution.Allowed)
            {

                return CovenantRetirementDenied(
                    "The operator did not approve retiring that standing preference.",
                    wardEvents);

            }

        }

        Result<CovenantToolWardReceipt> receipt = CovenantEgressWardPolicy.Accept(
            decision,
            classified.Value,
            CovenantWardDecision.Approved,
            staging.ProducingAdmission.Sensitivity,
            CovenantEgressDestination.Process,
            preflight.PreflightBodyDigest,
            checked((ulong)Math.Max(0, covenantAuthority?.Current?.AuthorityEpoch ?? 0)));

        if (receipt.IsFailure)
        {

            return CovenantRetirementDenied(receipt.Error.Message, wardEvents);

        }

        CovenantToolCapabilityNonce nonce = CovenantToolCapabilityNonce.Create();

        CovenantToolEgressAttempt attempt = new(
            ResolveInstallationIdentity(),
            staging.Collector.LogicalTurnId,
            PhysicalAttemptOrdinal: 1,
            staging.ProducingAdmission.Digest,
            nonce.ToDigest(),
            ResolveCallId(fcc),
            classified.Value.FrozenNameDigest,
            classified.Value.CanonicalArgumentDigest,
            CovenantEgressDestination.Process,

            // Nothing left the machine, and a staged tombstone publishes only if this turn's own answer
            // commits, so local erasure can still reach every byte of it.
            CovenantDisclosureRevocability.LocallyRevocable,
            preflight.PreflightBodyDigest,
            staging.ProducingAdmission.Sensitivity,
            receipt.Value.Digest,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Result<WardedToolExecutionResult> disclosed = await covenantEgressGuard
            .DiscloseThenAsync(
                attempt,
                async (_, effectToken) =>
                {

                    using IDisposable scope = CovenantToolStagingAmbient.Push(staging with
                    {
                        RetirementPreflight = preflight,
                        WardReceipt = receipt.Value,
                        Nonce = nonce,
                    });

                    (string text, bool denied) = await InvokeToolCallWithSanctumAsync(
                            fcc,
                            activeSpell,
                            chatOptions,
                            turnContext,
                            argsSnapshot,
                            effectToken)
                        .ConfigureAwait(false);

                    return Result<WardedToolExecutionResult>.Success(
                        new WardedToolExecutionResult(text, wardEvents, denied));

                },
                cancellationToken)
            .ConfigureAwait(false);

        return disclosed.IsFailure
            ? CovenantRetirementDenied(disclosed.Error.Message, wardEvents)
            : disclosed.Value;

    }

    /// <summary>
    /// Whether this turn's own admission carried the entry a retirement names.
    /// </summary>
    /// <remarks>
    /// Read off the producing admission's plan rather than off anything the model said. It decides only
    /// whether configured auto-approval applies; an unseen target still reaches an operator, because
    /// they may legitimately want a preference retired that this conversation never mentioned.
    /// </remarks>
    private static bool WasAdmittedOnThisTurn(
        CovenantAdmissionReceipt admission,
        CovenantRetirementPreflight preflight) =>
        admission.Plan.Decisions.Any(decision =>
            decision.Candidate.VersionId == preflight.VersionId
            && decision.Decision is CovenantPlanDecision.EligibleConfirmed or CovenantPlanDecision.EligibleProposed);

    private Guid ResolveInstallationIdentity() =>
        Guid.TryParse(covenantAuthority?.Current?.InstallationIdentity, out Guid installationId)
            ? installationId
            : Guid.Empty;

    /// <summary>
    /// Reads the key and lane a retirement names, on the same terms the handler reads them.
    /// </summary>
    private static bool TryReadRetirementTarget(
        string argsSnapshot,
        out string normalizedKey,
        out CovenantLane lane)
    {

        normalizedKey = string.Empty;

        lane = CovenantLane.Confirmed;

        try
        {

            using JsonDocument parsed = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argsSnapshot) ? "{}" : argsSnapshot);

            if (!parsed.RootElement.TryGetProperty("key", out JsonElement keyElement)
                || keyElement.ValueKind != JsonValueKind.String
                || keyElement.GetString() is not { Length: > 0 } rawKey
                || !parsed.RootElement.TryGetProperty("lane", out JsonElement laneElement)
                || laneElement.ValueKind != JsonValueKind.String
                || !Enum.TryParse(laneElement.GetString(), ignoreCase: false, out lane)
                || lane is not (CovenantLane.Confirmed or CovenantLane.Proposed))
            {

                return false;

            }

            normalizedKey = new CovenantKey(rawKey.Trim()).Value;

            return true;

        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {

            return false;

        }

    }

    private static WardedToolExecutionResult CovenantRetirementDenied(
        string message,
        List<IntelligenceEvent> wardEvents) =>
        new(message, wardEvents, Denied: true);

    private async Task<(string ResultText, bool Denied)> InvokeToolCallWithSanctumAsync(
        FunctionCallContent fcc,
        ParsedSpell? activeSpell,
        ChatOptions chatOptions,
        TurnContext turnContext,
        string argsSnapshot,
        CancellationToken cancellationToken)
    {

        SanctumEnforcementOutcome outcome = await EnforceSanctumAsync(
            fcc,
            turnContext,
            activeSpell,
            argsSnapshot,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Result.Allowed && outcome.Enabled && outcome.Mode == SanctumMode.Strict)
        {

            return (SanctumDenialMessage(outcome.Result), true);

        }

        // Sanctum cleared the initial URL above; the ward keeps every redirect hop under the same policy.
        using IDisposable? egressWard = BeginSanctumEgressWard(fcc, turnContext);

        object? rawResult = await InvokeToolCallAsync(
            fcc,
            chatOptions,
            cancellationToken).ConfigureAwait(false);

        if (rawResult is TrustedStructuredToolResult
            {
                Kind: TrustedStructuredToolResultKind.WorkspacePatch,
            } exactPatchResult
            && (exactPatchResult.ReceiptHandled
                || ApplyPatchInvocationAmbient.Current?.ReceiptHandled == true))
        {
            return (exactPatchResult.Text, false);
        }

        if (toolResultMaterializer is not null)
        {
            string toolName = fcc.Name ?? string.Empty;
            string materialized = MaterializeToolResultForModel(
                toolName,
                rawResult,
                toolResultMaterializer);

            return (materialized, false);
        }

        return (rawResult?.ToString() ?? string.Empty, false);

    }

    private sealed class GrimoirePendingReceiptSink(
        GrimoireTurnWriter writer) : IApplyPatchPendingReceiptSink
    {
        public ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken) =>
            writer.ProbeApplyPatchReceiptAsync(
                probe,
                cancellationToken);

        public ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken) =>
            writer.PreflightApplyPatchReceiptAsync(
                preflight,
                cancellationToken);

        public ValueTask<MandatoryToolInteractionAppendOutcome>
            PersistRecoveryReceiptAsync(
                ApplyPatchRecoveryReceipt receipt,
                CancellationToken cancellationToken) =>
            writer.PersistApplyPatchRecoveryReceiptAsync(
                receipt,
                cancellationToken);

        public ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken) =>
            writer.HandlePendingApplyPatchReceiptAsync(
                receipt,
                cancellationToken);
    }

    /// <summary>
    /// Reduces one tool result to the text the model is shown, under the one budget production has.
    /// </summary>
    /// <remarks>
    /// No budget parameter. It carried an optional one that the single production call site never
    /// passed and that nothing under <c>src</c> ever constructed, so every shipped turn materialized
    /// under the materializer's own default and the override existed only for suites that wanted to
    /// see truncation without producing enough output to earn it. A test budget nobody ships is a test
    /// that proves the truncation arithmetic and says nothing about whether the shipped ceiling is
    /// ever reached.
    /// </remarks>
    internal static string MaterializeToolResultForModel(
        string toolName,
        object? rawResult,
        IToolResultMaterializer materializer)
    {

        ArgumentNullException.ThrowIfNull(materializer);

        if (rawResult is TrustedStructuredToolResult
            {
                Kind: TrustedStructuredToolResultKind.WorkspacePatch,
                ReceiptHandled: true,
            } exactPatchResult)
        {
            return exactPatchResult.Text;
        }

        string rawText = rawResult?.ToString() ?? string.Empty;

        if (rawResult is TrustedStructuredToolResult trustedResult)
        {
            string? materialized = trustedResult.Kind switch
            {
                TrustedStructuredToolResultKind.WorkspaceSearch =>
                    TryMaterializeStructured(
                        toolName,
                        rawText,
                        materializer,
                        McpJsonSerializerContext.Default
                            .WorkspaceSearchToolResultEnvelope),
                TrustedStructuredToolResultKind.WorkspacePatch =>
                    TryMaterializeStructured(
                        toolName,
                        rawText,
                        materializer,
                        McpJsonSerializerContext.Default
                            .WorkspacePatchToolResultEnvelope),
                TrustedStructuredToolResultKind.WorkspaceCheck =>
                    TryMaterializeStructured(
                        toolName,
                        rawText,
                        materializer,
                        McpJsonSerializerContext.Default
                            .WorkspaceCheckToolResultEnvelope),
                _ => null,
            };

            if (materialized is not null)
            {
                return materialized;
            }
        }

        return materializer.Materialize(toolName, rawText).TextForModel;

    }

    private static string? TryMaterializeStructured<T>(
        string toolName,
        string rawText,
        IToolResultMaterializer materializer,
        JsonTypeInfo<T> jsonTypeInfo)
        where T : class, IStructuredToolResult<T>
    {
        try
        {
            T? structured = JsonSerializer.Deserialize(
                rawText,
                jsonTypeInfo);

            return structured is null
                ? null
                : materializer.MaterializeStructured(
                    toolName,
                    structured,
                    jsonTypeInfo).TextForModel;
        }
        catch (JsonException)
        {
            // A malformed trusted payload is treated as ordinary text.
            return null;
        }
    }

    private sealed record SanctumEnforcementOutcome(SanctumResult Result, SanctumMode Mode, bool Enabled);

    private async Task<SanctumEnforcementOutcome> EnforceSanctumAsync(
        FunctionCallContent fcc,
        TurnContext turnContext,
        ParsedSpell? activeSpell,
        string argsSnapshot,
        CancellationToken cancellationToken)
    {

        SanctumResult allowed = new() { Allowed = true };

        if (turnContext.Campaign is null)
        {

            return new SanctumEnforcementOutcome(allowed, SanctumMode.Strict, false);

        }

        if (!turnContext.SanctumEnabled)
        {

            return new SanctumEnforcementOutcome(allowed, turnContext.SanctumMode, false);

        }

        string campaignId = turnContext.CampaignId!;

        string toolName = fcc.Name ?? string.Empty;

        SanctumResult toolResult = await sanctumGuard
            .ValidateToolAsync(campaignId, toolName, cancellationToken)
            .ConfigureAwait(false);

        if (!toolResult.Allowed)
        {

            return new SanctumEnforcementOutcome(toolResult, turnContext.SanctumMode, true);

        }

        using JsonDocument? argsDocument = TryParseToolArgumentsDocument(argsSnapshot);

        JsonElement argsRoot = argsDocument?.RootElement ?? default;

        string workspaceRoot = turnContext.WorkspaceRoot!;

        SanctumResult? pathOrNetworkResult = await ValidateToolPathsAndNetworkAsync(
            campaignId,
            toolName,
            workspaceRoot,
            activeSpell,
            turnContext.SpellScriptRoots,
            argsRoot,
            cancellationToken).ConfigureAwait(false);

        if (pathOrNetworkResult is not null)
        {

            return new SanctumEnforcementOutcome(pathOrNetworkResult, turnContext.SanctumMode, true);

        }

        return new SanctumEnforcementOutcome(allowed, turnContext.SanctumMode, true);

    }

    /// <summary>
    /// Publishes a per-hop network ward for the native web tools when the turn runs inside an enabled
    /// Sanctum. The ward re-runs <see cref="ISanctumGuard.ValidateNetworkAsync"/>, so a redirect that
    /// leaves the campaign's allowed domains is both blocked and recorded as a breach.
    /// Returns <c>null</c> for every other tool and for unrestricted turns.
    /// </summary>
    private IDisposable? BeginSanctumEgressWard(FunctionCallContent fcc, TurnContext turnContext)
    {

        if (turnContext.Campaign is null || !turnContext.SanctumEnabled)
        {

            return null;

        }

        string toolName = fcc.Name ?? string.Empty;

        if (!string.Equals(toolName, "read_url", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(toolName, "browse_web", StringComparison.OrdinalIgnoreCase))
        {

            return null;

        }

        string campaignId = turnContext.CampaignId!;

        return SanctumEgressWardAmbient.Begin(async (Uri target, CancellationToken token) =>
        {

            SanctumResult result = await sanctumGuard
                .ValidateNetworkAsync(campaignId, target.AbsoluteUri, toolName, token)
                .ConfigureAwait(false);

            return result.Allowed;

        });

    }

    private async Task<SanctumResult?> ValidateToolPathsAndNetworkAsync(
        string campaignId,
        string toolName,
        string workspaceRoot,
        ParsedSpell? activeSpell,
        IReadOnlyList<string> spellScriptRoots,
        JsonElement argsRoot,
        CancellationToken cancellationToken)
    {

        switch (toolName.ToLowerInvariant())
        {

            case "execute_command":
            {

                string cwd = workspaceRoot;

                if (TryGetJsonStringProperty(argsRoot, "workingDirectory", out string? relativeCwd)
                    && !string.IsNullOrWhiteSpace(relativeCwd))
                {

                    if (!TryResolvePathUnderWorkspace(workspaceRoot, relativeCwd, out string? resolvedCwd))
                    {

                        return await sanctumGuard.ValidatePathAsync(
                            campaignId,
                            relativeCwd,
                            "working directory",
                            toolName,
                            cancellationToken).ConfigureAwait(false);

                    }

                    cwd = resolvedCwd;

                }

                SanctumResult cwdResult = await sanctumGuard
                    .ValidatePathAsync(campaignId, cwd, "working directory", toolName, cancellationToken)
                    .ConfigureAwait(false);

                if (!cwdResult.Allowed)
                {

                    return cwdResult;

                }

                break;

            }

            case ToolRiskClassifier.SearchWorkspaceToolName:
            {

                if (!TryGetJsonStringProperty(argsRoot, "root", out string? relativeRoot)
                    || string.IsNullOrWhiteSpace(relativeRoot))
                {

                    break;

                }

                if (!TryResolveSearchRootUnderWorkspace(
                        workspaceRoot,
                        relativeRoot,
                        out string? absoluteRoot))
                {

                    return await sanctumGuard.ValidatePathAsync(
                        campaignId,
                        relativeRoot,
                        "search root",
                        toolName,
                        cancellationToken).ConfigureAwait(false);

                }

                SanctumResult rootResult = await sanctumGuard
                    .ValidatePathAsync(
                        campaignId,
                        absoluteRoot,
                        "search root",
                        toolName,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!rootResult.Allowed)
                {

                    return rootResult;

                }

                break;

            }

            case ToolRiskClassifier.ApplyPatchToolName:
            {

                WorkspacePatchSettings patchSettings =
                    settings.Value.ResolveCodingTools().Patch;

                if (!TryParseApplyPatchManifest(
                        argsRoot,
                        patchSettings,
                        cancellationToken,
                        out UnifiedDiffManifest? manifest))
                {
                    return new SanctumResult
                    {
                        Allowed = false,
                        DenyReason =
                            "apply_patch target paths could not be validated within the active policy limits.",
                        Breach = new SanctumBreach
                        {
                            BreachId = Guid.NewGuid().ToString("N"),
                            CampaignId = campaignId,
                            ToolName = toolName,
                            BreachType = "PatchPathPreflight",
                            Detail =
                                "The patch manifest could not be parsed for complete path validation.",
                            Timestamp = DateTimeOffset.UtcNow,
                        },
                    };
                }

                foreach (string relativePath in manifest.NormalizedPaths)
                {
                    if (!WorkspaceRelativePath.TryResolve(
                            workspaceRoot,
                            relativePath,
                            out string? absolutePath,
                            out _))
                    {
                        return await sanctumGuard.ValidatePathAsync(
                            campaignId,
                            relativePath,
                            "patch path",
                            toolName,
                            cancellationToken).ConfigureAwait(false);
                    }

                    SanctumResult pathResult = await sanctumGuard
                        .ValidatePathAsync(
                            campaignId,
                            absolutePath,
                            "patch path",
                            toolName,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (!pathResult.Allowed)
                    {
                        return pathResult;
                    }
                }

                break;
            }

            case "write_file":
            case "replace_text_block":
            case "read_file_chunk":
            {

                if (!TryGetJsonStringProperty(argsRoot, "relativePath", out string? relativePath)
                    || string.IsNullOrWhiteSpace(relativePath))
                {

                    break;

                }

                if (!TryResolvePathUnderWorkspace(workspaceRoot, relativePath, out string? absolutePath))
                {

                    return await sanctumGuard.ValidatePathAsync(
                        campaignId,
                        relativePath,
                        "file path",
                        toolName,
                        cancellationToken).ConfigureAwait(false);

                }

                SanctumResult pathResult = await sanctumGuard
                    .ValidatePathAsync(campaignId, absolutePath, "file path", toolName, cancellationToken)
                    .ConfigureAwait(false);

                if (!pathResult.Allowed)
                {

                    return pathResult;

                }

                break;

            }

            case "run_spell_script":
            {

                // W3.5: validate the script path under EVERY candidate root the tool will resolve
                // against (active spell + Arcane Resonance dependencies), not just the active spell's
                // scripts root — a script that exists only under a resonant dependency was previously
                // executed without Sanctum pre-validating its path.
                if (spellScriptRoots.Count == 0)
                {

                    break;

                }

                if (!TryGetJsonStringProperty(argsRoot, "script_name", out string? scriptName)
                    || string.IsNullOrWhiteSpace(scriptName))
                {

                    break;

                }

                scriptName = scriptName.Trim();

                bool isPlainFileName = string.Equals(Path.GetFileName(scriptName), scriptName, StringComparison.Ordinal);

                foreach (string scriptsRoot in spellScriptRoots)
                {

                    string candidate = Path.GetFullPath(Path.Combine(scriptsRoot, scriptName));

                    SanctumResult scriptResult = await sanctumGuard
                        .ValidatePathAsync(campaignId, candidate, "script path", toolName, cancellationToken)
                        .ConfigureAwait(false);

                    if (!scriptResult.Allowed)
                    {

                        return scriptResult;

                    }

                    if (!isPlainFileName)
                    {

                        continue;

                    }

                    SanctumResult scriptsRootResult = await sanctumGuard
                        .ValidatePathAsync(campaignId, scriptsRoot, "working directory", toolName, cancellationToken)
                        .ConfigureAwait(false);

                    if (!scriptsRootResult.Allowed)
                    {

                        return scriptsRootResult;

                    }

                }

                break;

            }

            case "browse_web":
            case "read_url":
            {

                if (!TryGetJsonStringProperty(argsRoot, "url", out string? targetUrl)
                    || string.IsNullOrWhiteSpace(targetUrl))
                {

                    break;

                }

                SanctumResult networkResult = await sanctumGuard
                    .ValidateNetworkAsync(campaignId, targetUrl, toolName, cancellationToken)
                    .ConfigureAwait(false);

                if (!networkResult.Allowed)
                {

                    return networkResult;

                }

                break;

            }

        }

        return null;

    }

    /// <summary>
    /// Pure apply-patch preflight seam. It consumes only model arguments and operator limits;
    /// no workspace root or filesystem service is available to this method.
    /// </summary>
    internal static bool TryParseApplyPatchManifest(
        JsonElement arguments,
        WorkspacePatchSettings settings,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out UnifiedDiffManifest? manifest)
    {

        manifest = null;

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(
                "patch",
                out JsonElement patchElement)
            || patchElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        WorkspacePatchSettings normalizedSettings =
            ArcanumSettingClamps.NormalizeWorkspacePatchSettings(
                settings);

        UnifiedDiffParseResult parsed = UnifiedDiffParser.Parse(
            patchElement.GetString(),
            normalizedSettings,
            cancellationToken);

        manifest = parsed.Manifest;

        return parsed.Success && manifest is not null;

    }

    /// <summary>
    /// Resolves an explicit search root under the workspace for campaign-scoped Sanctum
    /// defense-in-depth. The search handler independently revalidates containment.
    /// </summary>
    internal static bool TryResolveSearchRootUnderWorkspace(
        string workspaceRoot,
        string relativeRoot,
        out string absolutePath)
    {

        absolutePath = string.Empty;

        try
        {

            string root = Path.GetFullPath(workspaceRoot.Trim());
            string trimmed = relativeRoot.Trim();

            if (trimmed is "." or "./" or @".\")
            {

                absolutePath = root;

                return true;

            }

            if (!WorkspaceRelativePath.TryResolve(
                    root,
                    trimmed,
                    out string? resolved,
                    out _)
                || !WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                    root,
                    resolved,
                    out string? finalPath))
            {

                return false;

            }

            absolutePath = finalPath ?? resolved;

            return true;

        }
        catch (Exception)
        {

            absolutePath = string.Empty;

            return false;

        }

    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <paramref name="workspaceRoot"/> and requires
    /// workspace containment (lexical + symlink walk). Defense-in-depth for Sanctum preflight;
    /// underlying tools still enforce their own sandbox checks.
    /// </summary>
    internal static bool TryResolvePathUnderWorkspace(string workspaceRoot, string relativePath, out string absolutePath)
    {

        absolutePath = string.Empty;

        try
        {

            string root = Path.GetFullPath(workspaceRoot.Trim());

            absolutePath = Path.IsPathRooted(relativePath)
                ? Path.GetFullPath(relativePath.Trim())
                : Path.GetFullPath(Path.Combine(root, relativePath.Trim()));

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, absolutePath, out string? resolved))
            {
                absolutePath = string.Empty;

                return false;
            }

            absolutePath = resolved ?? absolutePath;

            return true;

        }
        catch (Exception)
        {

            absolutePath = string.Empty;

            return false;

        }

    }

    private static bool TryGetJsonStringProperty(JsonElement root, string propertyName, out string? value)
    {

        value = null;

        if (root.ValueKind != JsonValueKind.Object)
        {

            return false;

        }

        if (!root.TryGetProperty(propertyName, out JsonElement property))
        {

            return false;

        }

        if (property.ValueKind != JsonValueKind.String)
        {

            return false;

        }

        value = property.GetString();

        return true;

    }

    private static string SanctumDenialMessage(SanctumResult result)
    {

        string breachType = result.Breach?.BreachType ?? "PolicyViolation";

        string reason = result.DenyReason ?? "This operation is not permitted in the current Sanctum.";

        return $"The Sanctum Guard has blocked this action: {breachType} — {reason}.\n"
            + "The Dungeon Master must update the Sanctum config to allow this operation.";

    }

    private static bool IsWardCandidate(string toolName, bool campaignRequiresWard, WardSettings wardSettings) =>
        RequiresWardForTool(toolName, campaignRequiresWard, wardSettings);

    private static async Task EmitWardEventAsync(
        IntelligenceEvent wardEvent,
        List<IntelligenceEvent> buffered,
        Func<IntelligenceEvent, CancellationToken, Task>? liveWardEmit,
        CancellationToken cancellationToken)
    {
        if (liveWardEmit is not null)
        {
            await liveWardEmit(wardEvent, cancellationToken).ConfigureAwait(false);
            return;
        }

        buffered.Add(wardEvent);
    }

    private static bool IsForbiddenArt(
        PingRequest request,
        string toolName,
        bool campaignRequiresWard,
        WardSettings wardSettings) =>
        RequiresWardForTool(toolName, campaignRequiresWard, wardSettings)
        && !(request.UnattendedMode && wardSettings.AutoDenyInUnattendedMode);

    private static bool RequiresWardForTool(string toolName, bool campaignRequiresWard, WardSettings wardSettings)
        => ToolRiskClassifier.RequiresWard(toolName, campaignRequiresWard, wardSettings);

    private static string UnattendedDenyMessage(string toolName) =>
        $"Forbidden art denied: unattended mode — this action requires an operator to resolve the ward";

    private static string OperatorDenyMessage(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "Operator denied this action."
            : $"Operator denied this action: {reason}";

    private static string TimeoutDenyMessage(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "Operator denied this action: The ward held until timeout — action was not allowed"
            : $"Operator denied this action: {reason}";

    private static JsonDocument? TryParseToolArgumentsDocument(string argsSnapshot)
    {

        if (string.IsNullOrWhiteSpace(argsSnapshot))
        {

            return null;

        }

        try
        {

            return JsonDocument.Parse(argsSnapshot);

        }
        catch (JsonException)
        {

            string encoded = JsonSerializer.Serialize(argsSnapshot, ArcanumJsonContext.Default.String);

            return JsonDocument.Parse($"{{\"raw\":{encoded}}}");

        }

    }

    private static JsonDocument? BuildWardArgumentsDocument(
        string toolName,
        string argsSnapshot)
    {

        JsonDocument? original = TryParseToolArgumentsDocument(argsSnapshot);
        string disclosure = ToolRiskClassifier.GetWardDisclosure(toolName);

        if (string.IsNullOrEmpty(disclosure))
        {

            return original;
        }

        using MemoryStream stream = new();

        using (Utf8JsonWriter writer = new(stream))
        {

            writer.WriteStartObject();

            if (original?.RootElement.ValueKind == JsonValueKind.Object)
            {

                foreach (JsonProperty property in original.RootElement
                             .EnumerateObject())
                {

                    if (!string.Equals(
                            property.Name,
                            "_arcanumRiskDisclosure",
                            StringComparison.Ordinal))
                    {

                        property.WriteTo(writer);
                    }

                }

            }
            else if (original is not null)
            {

                writer.WritePropertyName("arguments");
                original.RootElement.WriteTo(writer);
            }

            writer.WriteString(
                "_arcanumRiskDisclosure",
                disclosure);
            writer.WriteEndObject();
        }

        original?.Dispose();
        return JsonDocument.Parse(stream.ToArray());
    }

}
