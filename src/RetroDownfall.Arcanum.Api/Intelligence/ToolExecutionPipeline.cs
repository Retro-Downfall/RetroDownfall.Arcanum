using System.Buffers;

using System.Globalization;

using System.Runtime.CompilerServices;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Sanctum;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Telemetry;

using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

using RetroDownfall.Arcanum.Infrastructure.Workspaces;

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
    ILogger<ToolExecutionPipeline> logger)
{

    /// <summary>
    /// Synthesized tool result text when an unexpected (infrastructure-fault) exception is tolerated
    /// rather than failing the whole turn — see <c>Arcanum:Intelligence:TolerateToolFailures</c>
    /// (default <see langword="true"/>). The model sees this text as the tool's result and can decide
    /// how to proceed (retry, apologize, try a different approach) instead of the turn failing
    /// outright with <c>Hub.Error</c>. Exact wording is a contract with the model prompt, not just a
    /// log message — do not change casually.
    /// </summary>
    public static string PublicToolFailureMessage(string toolName) =>
        $"[Tool error: {toolName} failed with an internal error. The operator has been notified.]";

    private const string WardTimeoutReason =
        "The ward held until timeout — action was not allowed";

    public sealed class TurnContext
    {

        public Campaign? Campaign { get; init; }

        public string? CampaignId { get; init; }

        public string? WorkspaceRoot { get; init; }

        public bool CampaignRequiresWard { get; init; }

        public bool SanctumEnabled { get; init; }

        public SanctumMode SanctumMode { get; init; }

        public IReadOnlyList<AITool> InferenceTools { get; init; } = [];

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
        IReadOnlyList<AIContent>? AdditionalContextContents = null);

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
    /// Records <c>arcanum_tool_invocations_total</c>. <paramref name="outcome"/> is one of
    /// <c>success</c>, <c>denied</c> (ward or Sanctum blocked the call), or <c>error</c> (the tool
    /// invocation itself threw). The configured MCP/local tool set is finite, so <paramref name="toolName"/>
    /// is a bounded label value by construction.
    /// </summary>
    private static void RecordToolInvocationMetric(string toolName, string outcome)
    {

        ArcanumMetrics.ToolInvocationsTotal.Add(
            1,
            new KeyValuePair<string, object?>("tool_name", toolName),
            new KeyValuePair<string, object?>("outcome", outcome));

    }

    public static void AppendToolExchangeToMessages(
        List<MeAiChatMessage> chatMessages,
        FunctionCallContent fcc,
        string callId,
        string resultText)
    {

        FunctionCallContent normalizedCall = string.IsNullOrEmpty(fcc.CallId)
            ? new FunctionCallContent(callId, fcc.Name, fcc.Arguments)
            : fcc;

        chatMessages.Add(new MeAiChatMessage(ChatRole.Assistant, [normalizedCall]));

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
        CancellationToken cancellationToken)
    {

        string argsSnapshot = SerializeToolArgumentsForGrimoire(fcc);

        string callId = ResolveCallId(fcc);

        string toolName = fcc.Name ?? string.Empty;

        WardedToolExecutionResult wardedExecution;

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
                    cancellationToken)
                    .ConfigureAwait(false);

                RecordToolInvocationMetric(toolName, wardedExecution.Denied ? "denied" : "success");

            }
            catch (OperationCanceledException)
            {

                throw;

            }
            catch (HumanPromptTimeoutException ex)
            {

                wardedExecution = new WardedToolExecutionResult(ex.Message, [], Failed: true);

                RecordToolInvocationMetric(toolName, "error");

            }
            catch (HumanPromptCapExceededException ex)
            {

                wardedExecution = new WardedToolExecutionResult(ex.Message, [], Failed: true);

                RecordToolInvocationMetric(toolName, "error");

            }
            catch (Exception ex)
            {

                logger.LogError(ex, "Tool {ToolName} failed during inference (tolerated — Arcanum:Intelligence:TolerateToolFailures).", toolName);

                wardedExecution = new WardedToolExecutionResult(PublicToolFailureMessage(toolName), [], Failed: true);

                RecordToolInvocationMetric(toolName, "error");

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
                    cancellationToken)
                    .ConfigureAwait(false);

                RecordToolInvocationMetric(toolName, wardedExecution.Denied ? "denied" : "success");

            }
            catch (OperationCanceledException)
            {

                throw;

            }
            catch (Exception)
            {

                RecordToolInvocationMetric(toolName, "error");

                throw;

            }

        }

        IReadOnlyList<AIContent>? additionalContext = null;

        if (!wardedExecution.Failed
            && string.Equals(toolName, "attach_session_file", StringComparison.Ordinal)
            && SessionAttachmentToolAmbient.CurrentSessionId is { } ambientSessionId
            && SessionAttachmentToolInjection.TryParseAttachArguments(fcc.Arguments, out string logicalName, out int? version))
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

        return new ProcessedToolCall(
            callId,
            toolName,
            argsSnapshot,
            wardedExecution.ResultText,
            wardedExecution.WardEvents,
            wardedExecution.Failed,
            additionalContext);

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

    private static async Task<string> InvokeToolCallAsync(
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

        return output switch
        {

            null => string.Empty,
            string s => s,
            _ => output.ToString() ?? string.Empty,

        };

    }

    private async Task<WardedToolExecutionResult> ExecuteToolCallWithWardAsync(
        FunctionCallContent fcc,
        PingRequest request,
        ChatOptions chatOptions,
        ParsedSpell? activeSpell,
        string? sessionId,
        TurnContext turnContext,
        string argsSnapshot,
        CancellationToken cancellationToken)
    {

        string toolName = fcc.Name ?? string.Empty;

        WardSettings wardSettings = settings.Value.Ward ?? new WardSettings();

        if (IsWardCandidate(toolName, turnContext.CampaignRequiresWard, wardSettings)
            && request.UnattendedMode
            && wardSettings.AutoDenyInUnattendedMode)
        {

            return new WardedToolExecutionResult(UnattendedDenyMessage(toolName), [], Denied: true);

        }

        if (!IsForbiddenArt(request, toolName, turnContext.CampaignRequiresWard, wardSettings))
        {

            (string directResult, bool directDenied) = await InvokeToolCallWithSanctumAsync(
                fcc,
                activeSpell,
                chatOptions,
                turnContext,
                argsSnapshot,
                cancellationToken).ConfigureAwait(false);

            return new WardedToolExecutionResult(directResult, [], directDenied);

        }

        string wardId = Guid.NewGuid().ToString();

        JsonDocument? argsDocument = TryParseToolArgumentsDocument(argsSnapshot);

        JsonElement? wardArguments = argsDocument?.RootElement.Clone();

        DateTimeOffset wardTimestamp = DateTimeOffset.UtcNow;

        var wardEvents = new List<IntelligenceEvent>(2);

        wardEvents.Add(new IntelligenceEvent(
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
            wardTimestamp));

        int timeoutSeconds = ArcanumSettingClamps.WardTimeoutSeconds(wardSettings.TimeoutSeconds);

        TimeSpan timeout = TimeSpan.FromSeconds(timeoutSeconds);

        WardResolution resolution;

        try
        {

            resolution = await ward
                .WardAsync(wardId, toolName, argsDocument, sessionId, timeout, cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            argsDocument?.Dispose();

        }

        DateTimeOffset resolvedTimestamp = DateTimeOffset.UtcNow;

        wardEvents.Add(new IntelligenceEvent(
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
            resolvedTimestamp));

        if (!resolution.Allowed)
        {

            string denialMessage = string.Equals(resolution.Reason, WardTimeoutReason, StringComparison.Ordinal)
                ? TimeoutDenyMessage(resolution.Reason)
                : OperatorDenyMessage(resolution.Reason);

            return new WardedToolExecutionResult(denialMessage, wardEvents, Denied: true);

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

    /// <summary>
    /// Returns the tool result text plus whether Sanctum blocked the call (<c>Denied: true</c>) — the
    /// caller's <see cref="WardedToolExecutionResult.Denied"/> flag (and, ultimately,
    /// <c>arcanum_tool_invocations_total{outcome="denied"}</c>) depends on this, since a Sanctum-strict
    /// block returns a synthetic result string with no corresponding <see cref="IntelligenceEvent"/>.
    /// </summary>
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

        string resultText = await InvokeToolCallAsync(fcc, chatOptions, cancellationToken).ConfigureAwait(false);

        return (resultText, false);

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

    private async Task<SanctumResult?> ValidateToolPathsAndNetworkAsync(
        string campaignId,
        string toolName,
        string workspaceRoot,
        ParsedSpell? activeSpell,
        IReadOnlyList<string> spellScriptRoots,
        JsonElement argsRoot,
        CancellationToken cancellationToken)
    {

        switch (toolName)
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

            case "use_commlink":
            case "petition_dungeon_master":
            {

                string? webhookUrl = settings.Value.CommLink?.WebhookUrl;

                if (string.IsNullOrWhiteSpace(webhookUrl))
                {

                    break;

                }

                SanctumResult networkResult = await sanctumGuard
                    .ValidateNetworkAsync(campaignId, webhookUrl, toolName, cancellationToken)
                    .ConfigureAwait(false);

                if (!networkResult.Allowed)
                {

                    return networkResult;

                }

                break;

            }

            case "browse_web":
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

    private static bool TryResolvePathUnderWorkspace(string workspaceRoot, string relativePath, out string absolutePath)
    {

        absolutePath = string.Empty;

        try
        {

            string root = Path.GetFullPath(workspaceRoot.Trim());

            absolutePath = Path.IsPathRooted(relativePath)
                ? Path.GetFullPath(relativePath.Trim())
                : Path.GetFullPath(Path.Combine(root, relativePath.Trim()));

            return true;

        }
        catch (Exception)
        {

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

    private static bool IsForbiddenArt(
        PingRequest request,
        string toolName,
        bool campaignRequiresWard,
        WardSettings wardSettings) =>
        RequiresWardForTool(toolName, campaignRequiresWard, wardSettings)
        && !(request.UnattendedMode && wardSettings.AutoDenyInUnattendedMode);

    private static bool RequiresWardForTool(string toolName, bool campaignRequiresWard, WardSettings wardSettings)
    {

        if (!wardSettings.Enabled
            || !wardSettings.ForbiddenArts.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {

            return false;

        }

        if (string.Equals(toolName, "execute_command", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        return campaignRequiresWard;

    }

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

}
