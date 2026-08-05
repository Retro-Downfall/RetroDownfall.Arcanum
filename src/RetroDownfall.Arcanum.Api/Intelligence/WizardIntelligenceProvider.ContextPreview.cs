using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

using RetroDownfall.Arcanum.Infrastructure.Intelligence;

using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

using RetroDownfall.Arcanum.Infrastructure.Workspaces;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class WizardIntelligenceProvider
{

    private static readonly AsyncLocal<Action<ContextPreviewAuxiliaryCall>?> PreviewAuxiliaryObserver = new();

    public async Task<Result<ContextPreviewResult>> PreviewContextAsync(

        ContextPreviewRequest request,

        CancellationToken cancellationToken = default)

    {

        ArgumentNullException.ThrowIfNull(request);

        PingRequest turn = new(

            request.Prompt ?? string.Empty,

            request.Model,

            request.WorkingDirectory ?? string.Empty,

            SessionId: request.SessionId,

            AttachedFiles: request.AttachedFiles,

            OverrideSpellName: request.OverrideSpellName,

            Temperature: request.Temperature,

            TopP: request.TopP,

            MaxOutputTokens: request.MaxOutputTokens,

            Stop: request.Stop,

            Seed: request.Seed,

            ResponseFormat: request.ResponseFormat,

            PresencePenalty: request.PresencePenalty,

            FrequencyPenalty: request.FrequencyPenalty,

            CampaignId: request.CampaignId,

            AdditionalSystemPrompt: request.AdditionalSystemPrompt,

            ScryingFoci: request.ScryingFoci,

            DisableAllTools: request.DisableAllTools,

            UnattendedMode: request.UnattendedMode);

        Result preflight = PingRequestPreflightValidator.Validate(

            turn,

            settings.Value);

        if (preflight.IsFailure)

        {

            return Result<ContextPreviewResult>.Failure(preflight.Error);

        }

        ChatClientLease lease;

        try

        {

            lease = await chatClientFactory

                .ResolveClientAsync(turn.Model, cancellationToken)

                .ConfigureAwait(false);

        }
        catch (InvalidOperationException)

        {

            return Result<ContextPreviewResult>.Failure(

                new Error(ErrorCodes.Hub.Model, PublicModelResolutionFailureMessage));

        }

        using (lease)

        {

            List<ContextPreviewAuxiliaryCall> auxiliaryCalls = [];

            Action<ContextPreviewAuxiliaryCall>? previousObserver = PreviewAuxiliaryObserver.Value;

            PreviewAuxiliaryObserver.Value = auxiliaryCalls.Add;

            try

            {

                return await BuildContextPreviewAsync(

                    request,

                    turn,

                    lease,

                    auxiliaryCalls,

                    cancellationToken).ConfigureAwait(false);

            }
            finally

            {

                PreviewAuxiliaryObserver.Value = previousObserver;

            }

        }

    }

    private async Task<Result<ContextPreviewResult>> BuildContextPreviewAsync(

        ContextPreviewRequest previewRequest,

        PingRequest turn,

        ChatClientLease lease,

        List<ContextPreviewAuxiliaryCall> auxiliaryCalls,

        CancellationToken cancellationToken)

    {

        Session? thread = await inferenceContextBuilder

            .LoadThreadAsync(turn, cancellationToken)

            .ConfigureAwait(false);

        if (turn.SessionId is not null && thread is null)

        {

            return Result<ContextPreviewResult>.Failure(

                new Error(ErrorCodes.Session.NotFound, "Session was not found."));

        }

        List<MeAiChatMessage> messages = InferenceContextBuilder.BuildInitialMeAiChatMessages(

            turn,

            thread,

            turn.Prompt);

        SessionContextPinMaterialization pins =

            sessionContextPinMaterializer is not null && turn.SessionId is { } pinSessionId

                ? await sessionContextPinMaterializer

                    .MaterializeAsync(pinSessionId, turn.WorkingDirectory, cancellationToken)

                    .ConfigureAwait(false)

                : new SessionContextPinMaterialization([], 0, 0);

        InferenceContextBuilder.AppendContentsToLastMessage(messages, pins.Contents);

        string? codexContent = await CodexReader

            .ReadCodexAsync(

                turn.WorkingDirectory,

                ArcanumSettingClamps.EffectiveCodexMaxSizeBytes(settings.Value),

                cancellationToken)

            .ConfigureAwait(false);

        ResolvedSpell? resolvedSpell = null;

        string routingMode;

        bool hasExplicitSpell = !string.IsNullOrWhiteSpace(turn.OverrideSpellName)

            || !string.IsNullOrWhiteSpace(turn.OverrideSpellPath);

        if (previewRequest.NoRetrieval

            && !hasExplicitSpell)

        {

            routingMode = "disabledByNoRetrieval";

        }
        else

        {

            string? spellRoot = Infrastructure.Security.WorkspacePathPolicy.TryNormalizeWorkspace(

                turn.WorkingDirectory,

                out string? normalizedSpellRoot,

                out _)

                ? normalizedSpellRoot

                : null;

            Result<ResolvedSpell?> routed = await ResolveRoutedSpellAsync(

                turn,

                lease.ChatClient,

                lease.Provider,

                lease.ResolvedModel,

                spellRoot,

                cancellationToken,

                ObserveSemanticRoutingEmbedding).ConfigureAwait(false);

            if (routed.IsFailure)

            {

                return Result<ContextPreviewResult>.Failure(routed.Error);

            }

            resolvedSpell = routed.Value;

            routingMode = previewRequest.NoRetrieval

                ? "explicitOverride"

                : "production";

        }

        Embedding<float>? queryEmbedding = null;

        SemanticContextChunk[]? semanticContext = null;

        SagaMemory[]? sagaMemories = null;

        SessionAttachmentRetrievedChunk[]? attachmentContext = null;

        TapestryContextNode[]? tapestryContext = null;

        IReadOnlyList<LexiconEntryDto>? lexiconEntries = null;

        if (!previewRequest.NoRetrieval)

        {

            queryEmbedding = await ResolveRagQueryEmbeddingAsync(turn, cancellationToken).ConfigureAwait(false);

            semanticContext = await RetrieveSemanticContextAsync(turn, queryEmbedding, cancellationToken).ConfigureAwait(false);

            sagaMemories = await RetrieveSagaMemoriesAsync(queryEmbedding, cancellationToken).ConfigureAwait(false);

            attachmentContext = await RetrieveSessionAttachmentContextAsync(

                turn,

                queryEmbedding,

                cancellationToken).ConfigureAwait(false);

            // The preview renders the same hierarchical context the real turn would, minus the ledger:
            // this path never mutates a turn ledger, so nodes are projected directly.
            tapestryContext = ProjectTapestryPreview(

                await RetrieveTapestryContextAsync(turn, queryEmbedding, cancellationToken)

                    .ConfigureAwait(false));

            lexiconEntries = await RetrieveLexiconEntriesAsync(

                turn,

                resolvedSpell?.Entities ?? [],

                lease.ChatClient,

                lease.Provider,

                lease.ResolvedModel,

                cancellationToken).ConfigureAwait(false);

        }

        AttachmentsSettings attachments = settings.Value.ResolveAttachments();

        int maxIndexItems = ArcanumSettingClamps.AttachmentsMaxIndexItemsInPrompt(

            attachments.MaxIndexItemsInPrompt);

        int maxIndexBytes = ArcanumSettingClamps.AttachmentsMaxIndexBytesInPrompt(

            attachments.MaxIndexBytesInPrompt);

        IReadOnlyList<SessionAttachmentIndexItem> attachmentIndex = turn.SessionId is { } attachmentSessionId

            ? await sessionAttachmentStore

                .BuildIndexAsync(attachmentSessionId, maxIndexItems, cancellationToken)

                .ConfigureAwait(false)

            : [];

        SystemPromptDocument document = SystemPromptBuilder.BuildDocument(

            turn,

            codexContent,

            resolvedSpell?.Primary,

            attachedFiles: turn.AttachedFiles,

            dependencySpells: resolvedSpell?.Resonants,

            maxResonantBytes: ArcanumSettingClamps.MaxResonantBytes(

                ArcanumRuntimeDefaults.Spells.MaxResonantBytes),

            semanticContext: semanticContext,

            sagaMemories: sagaMemories,

            lexiconEntries: lexiconEntries,

            maxLexiconInjectedBytes: ArcanumSettingClamps.LexiconMaxInjectedBytes(

                settings.Value.ResolveIntelligence().LexiconMaxInjectedBytes),

            sessionAttachmentsIndex: attachmentIndex,

            maxIndexItems: maxIndexItems,

            maxIndexBytes: maxIndexBytes,

            sessionAttachmentContext: attachmentContext,

            tapestryContext: tapestryContext);

        InferenceContextBuilder.PrependDynamicSystemMessage(messages, document.Render());

        List<ContextPreviewTool> excludedTools = [];

        List<AITool> candidateTools = await BuildToolSetWithMcpAsync(

            turn,

            resolvedSpell,

            turn.SessionId ?? Guid.NewGuid(),

            Guid.NewGuid(),

            cancellationToken,

            excludedTools).ConfigureAwait(false);

        IReadOnlyList<AITool> includedTools = ApplyToolPolicyFilters(turn, candidateTools);

        includedTools = turn.UnattendedMode

            ? FilterToolsForUnattended(includedTools)

            : includedTools;

        includedTools = FilterAskHumanUnlessAvailable(includedTools, humanInteractionAvailable: false);

        List<ContextPreviewTool> tools = BuildPreviewTools(

            turn,

            candidateTools,

            includedTools,

            excludedTools);

        ChatOptions options = CreateInferenceChatOptions(

            includedTools.Count > 0,

            includedTools,

            turn,

            lease);

        ModelCallContext callContext = BuildModelCallContext(lease, turn);

        int threshold = ComputeCompressionThreshold(

            lease.Provider.ContextWindowLimit,

            settings.Value.ResolveIntelligence().ContextWindowCompressionThreshold);

        int beforeCompression = ModelTokenEstimator.EstimateContext(

            new ModelTokenizationRequest(

                lease.Provider,

                lease.ResolvedModel,

                messages,

                options,

                callContext.ReservedAnswerTokens,

                callContext.ReservedReasoningTokens)).TotalTokens;

        (bool compressed, List<MeAiChatMessage> finalMessages) =

            inferenceContextBuilder.TryApplyContextCompressionIfNeeded(

                new ContextCompressionRequest

                {

                    Request = turn,

                    Messages = messages,

                    Thread = thread,

                    NewUserPrompt = turn.Prompt,

                    Lease = lease,

                    ChatOptions = options,

                    CodexContent = codexContent,

                    ActiveSpell = resolvedSpell?.Primary,

                    DependencySpells = resolvedSpell?.Resonants,

                    ReservedAnswerTokens = callContext.ReservedAnswerTokens,

                    ReservedReasoningTokens = callContext.ReservedReasoningTokens,

                    AppendedContents = pins.Contents,

                    SemanticContext = semanticContext,

                    SagaMemories = sagaMemories,

                    SessionAttachmentContext = attachmentContext,

                    TapestryContext = tapestryContext,

                    LexiconEntries = lexiconEntries,

                    SessionAttachmentsIndex = attachmentIndex,

                    MaxIndexItems = maxIndexItems,

                    MaxIndexBytes = maxIndexBytes,

                });

        ContextTokenBreakdown breakdown = ModelTokenEstimator.EstimateContext(

            new ModelTokenizationRequest(

                lease.Provider,

                lease.ResolvedModel,

                finalMessages,

                options,

                callContext.ReservedAnswerTokens,

                callContext.ReservedReasoningTokens));

        List<ContextPreviewSource> sources = BuildPreviewSources(

            breakdown,

            previewRequest,

            turn,

            finalMessages);

        ContextPreviewContent? content = previewRequest.ShowContent

            ? new ContextPreviewContent(

                finalMessages.FirstOrDefault(static message => message.Role == ChatRole.System)?.Text

                    ?? string.Empty,

                [.. finalMessages

                    .Where(static message => message.Role != ChatRole.System)

                    .Select(static message => new CoreChatMessage(

                        message.Role.ToString().ToLowerInvariant(),

                        message.Text ?? string.Empty))])

            : null;

        ContextPreviewResult result = new(

            lease.Provider.Name,

            lease.ResolvedModel,

            ArcanumSettingClamps.ContextWindowLimit(lease.Provider.ContextWindowLimit),

            resolvedSpell?.Primary?.Name,

            routingMode,

            previewRequest.NoRetrieval

                && resolvedSpell?.Primary is not null

                ? "The explicit Spell override was resolved while automatic routing and retrieval were skipped."

                : previewRequest.NoRetrieval

                ? "Automatic Spell routing was skipped because noRetrieval was requested."

                : resolvedSpell?.Primary is null

                    ? "Production routing completed without selecting a Spell."

                    : "Production routing selected the effective Spell.",

            [.. resolvedSpell?.Resonants.Select(static spell => spell.Name) ?? []],

            tools,

            sources,

            new ContextPreviewCompression(

                compressed,

                threshold,

                compressed

                    ? "Session history exceeded the production threshold and the available summary was applied."

                    : beforeCompression > threshold

                        ? "Threshold exceeded, but no usable session summary was available."

                        : "Context is below the production compression threshold."),

            auxiliaryCalls,

            new ContextPreviewTokenSummary(

                breakdown.InputTokens,

                breakdown.TotalTokens,

                breakdown.ReservedAnswerTokens,

                breakdown.OverallClassification,

                breakdown.Profile.ProfileId),

            content);

        return Result<ContextPreviewResult>.Success(result);

    }

    private static int ComputeCompressionThreshold(int contextWindowLimit, int thresholdPercent)

    {

        int limit = ArcanumSettingClamps.ContextWindowLimit(contextWindowLimit);

        int percent = ArcanumSettingClamps.ContextWindowCompressionThreshold(thresholdPercent);

        return int.CreateSaturating((long)limit * percent / 100L);

    }

    private void ObserveSemanticRoutingEmbedding(string purpose, int inputCount)

    {

        EmbeddingSettings embeddingSettings = settings.Value.ResolveEmbeddings();

        PreviewAuxiliaryObserver.Value?.Invoke(

            new ContextPreviewAuxiliaryCall(

                purpose,

                true,

                embeddingSettings.Provider,

                embeddingSettings.Model,

                null,

                TokenEstimateClassification.Unknown,

                $"The semantic Spell router embedded {inputCount} input(s); this provider does not report token usage."));

    }

    private static List<ContextPreviewTool> BuildPreviewTools(

        PingRequest request,

        IReadOnlyList<AITool> candidates,

        IReadOnlyList<AITool> included,

        IReadOnlyList<ContextPreviewTool> excluded)

    {

        HashSet<string> includedNames = included

            .Select(static tool => tool.Name)

            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<ContextPreviewTool> result = [.. excluded];

        result.AddRange(candidates

            .Select(tool => new ContextPreviewTool(

                tool.Name,

                ResolveToolSource(tool),

                includedNames.Contains(tool.Name),

                includedNames.Contains(tool.Name)

                    ? "Advertised by the production tool policy."

                    : "Excluded by the effective turn tool policy or attended-interaction requirement."))

            .ToList());

        if (ShouldDisableMcpTools(request))

        {

            result.Add(new ContextPreviewTool(

                "mcp:*",

                "mcp",

                false,

                "MCP tools are disabled for this turn."));

        }

        result.Add(new ContextPreviewTool(

            "ask_human",

            "native",

            false,

            "Context preview has no live attended response channel."));

        return [.. result

            .GroupBy(static tool => tool.Name, StringComparer.OrdinalIgnoreCase)

            .Select(static group => group.First())

            .OrderBy(static tool => tool.Name, StringComparer.OrdinalIgnoreCase)];

    }

    private static string ResolveToolSource(AITool tool)

    {

        string typeName = tool.GetType().Name;

        if (typeName.Contains("Mcp", StringComparison.OrdinalIgnoreCase))

        {

            return "mcp";

        }

        return typeName.Contains("Spell", StringComparison.OrdinalIgnoreCase)

            ? "spell"

            : "native";

    }

    private static List<ContextPreviewSource> BuildPreviewSources(

        ContextTokenBreakdown breakdown,

        ContextPreviewRequest request,

        PingRequest turn,

        IReadOnlyList<MeAiChatMessage> messages)

    {

        string? systemContent = request.ShowContent

            ? messages.FirstOrDefault(static message => message.Role == ChatRole.System)?.Text

            : null;

        List<ContextPreviewSource> result = [];

        foreach (ContextTokenComponent component in breakdown.Components)

        {

            bool retrievalSource = component.Source is

                ContextTokenSource.WorkspaceRag

                or ContextTokenSource.AttachmentRag

                or ContextTokenSource.TapestryRag

                or ContextTokenSource.LexiconSaga;

            bool included = component.Estimate.TokenCount > 0;

            string reason = ResolveSourceReason(

                component.Source,

                included,

                retrievalSource,

                request,

                turn);

            string? content = request.ShowContent

                && component.Source == ContextTokenSource.SystemCodexSpell

                    ? systemContent

                    : null;

            result.Add(new ContextPreviewSource(

                component.Source,

                component.Estimate.TokenCount,

                component.Estimate.Classification,

                included,

                reason,

                content));

        }

        return result;

    }

    private static string ResolveSourceReason(

        ContextTokenSource source,

        bool included,

        bool retrievalSource,

        ContextPreviewRequest request,

        PingRequest turn)

    {

        if (request.NoRetrieval && retrievalSource)

        {

            return "Skipped because noRetrieval was requested.";

        }

        if (included)

        {

            return "Included by production context assembly.";

        }

        return source switch

        {

            ContextTokenSource.History when turn.SessionId is null =>

                "No Session was selected, so no history is available.",

            ContextTokenSource.ExplicitAttachments when turn.SessionId is null =>

                "No Session was selected, so explicit attachments are unavailable.",

            ContextTokenSource.AttachmentRag when turn.SessionId is null =>

                "No Session was selected, so attachment retrieval is unavailable.",

            ContextTokenSource.WorkspaceRag when string.IsNullOrWhiteSpace(turn.WorkingDirectory) =>

                "No Workspace was selected, so workspace retrieval is unavailable.",

            _ => "No effective content was available for this source.",

        };

    }

}
