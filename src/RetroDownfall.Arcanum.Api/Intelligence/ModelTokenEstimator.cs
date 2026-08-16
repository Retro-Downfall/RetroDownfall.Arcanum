using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Caching;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Model-aware, AOT-safe pre-call token estimator. Provider-reported usage is intentionally not
/// inferred here; callers reconcile that independent post-call authority onto the returned
/// <see cref="ContextTokenBreakdown"/>.
/// </summary>
public sealed class ModelTokenEstimator : IModelTokenEstimator
{
    private const int DefaultPerToolOverheadTokens = 8;

    private const int DefaultProviderFramingTokens = 3;

    private const int DefaultStopTokenOverheadTokens = 1;

    private static readonly BoundedLruCache<TextTokenCacheKey, int> TextTokenCache = new(4096);

    private readonly InferenceTokenizerResolver _tokenizerResolver;

    /// <summary>
    /// Tokenization profiles resolve from <see cref="ModelCapabilityCatalog"/> and the code-owned
    /// <see cref="ArcanumRuntimeDefaults.Intelligence"/> defaults, so no bound
    /// <c>Arcanum:…</c> settings accessor is taken — none of the tokenization knobs are projected
    /// onto <see cref="IntelligenceSettings"/> by <c>ResolveIntelligence</c>.
    /// </summary>
    public ModelTokenEstimator(InferenceTokenizerResolver tokenizerResolver) =>
        _tokenizerResolver = tokenizerResolver;

    public ResolvedModelTokenizationProfile ResolveProfile(
        ProviderSettings provider,
        string canonicalModel)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (ModelCapabilityCatalog.Resolve(provider, canonicalModel) is { } capabilities)
        {
            return capabilities.Tokenization;
        }

        IntelligenceSettings intelligence = ArcanumRuntimeDefaults.Intelligence;
        string fallbackTokenizer = string.IsNullOrWhiteSpace(intelligence.TokenizerEncoding)
            ? InferenceTokenizerResolver.DefaultEncodingName
            : intelligence.TokenizerEncoding.Trim();

        return new ResolvedModelTokenizationProfile
        {
            ProfileId = $"fallback:{provider.Type}:{provider.Name}:{canonicalModel}:{fallbackTokenizer}",
            Type = ModelTokenizationProfileType.UnknownFallback,
            TokenizerId = fallbackTokenizer,
            SafetyMarginPercent = ArcanumSettingClamps.EstimatedTokenSafetyMarginPercent(
                intelligence.EstimatedTokenSafetyMarginPercent),
            PerMessageOverheadTokens = ArcanumSettingClamps.PerMessageTemplateOverheadTokens(
                intelligence.PerMessageTemplateOverheadTokens),
            PerToolOverheadTokens = DefaultPerToolOverheadTokens,
            ProviderFramingTokens = DefaultProviderFramingTokens,
            StopTokenOverheadTokens = DefaultStopTokenOverheadTokens,
            UnknownImageReserveTokens = ResolveUnknownImageReserve(),
            Confidence = 0.5d,
        };
    }

    public TokenEstimate EstimateText(
        ProviderSettings provider,
        string canonicalModel,
        string? text)
    {
        ResolvedModelTokenizationProfile profile = ResolveUsableProfile(
            ResolveProfile(provider, canonicalModel),
            out Tokenizer tokenizer);
        TokenEstimate raw = CountText(text, profile, tokenizer);

        if (raw.Classification == TokenEstimateClassification.Exact || raw.TokenCount == 0)
        {
            return raw;
        }

        int margin = PercentageCeiling(raw.TokenCount, profile.SafetyMarginPercent);
        return raw with
        {
            TokenCount = SaturatingAdd(raw.TokenCount, margin),
            SafetyMarginTokens = margin,
        };
    }

    public ResolvedModelTokenizationProfile ResolveEffectiveProfile(
        ProviderSettings provider,
        string canonicalModel) =>
        ResolveUsableProfile(
            ResolveProfile(provider, canonicalModel),
            out _);

    public ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Provider);
        ArgumentNullException.ThrowIfNull(request.Messages);
        ArgumentNullException.ThrowIfNull(request.Options);

        ResolvedModelTokenizationProfile profile = ResolveUsableProfile(
            ResolveProfile(request.Provider, request.Model),
            out Tokenizer tokenizer);
        Dictionary<ContextTokenSource, MutableEstimate> estimates = [];
        List<int> messageTokenCounts = new(request.Messages.Count);
        int finalUserIndex = FindFinalUserIndex(request.Messages);

        for (int index = 0; index < request.Messages.Count; index++)
        {
            long beforeMessage = SumAll(estimates);
            ChatMessage message = request.Messages[index];
            bool currentPrompt = index == finalUserIndex;

            Add(
                estimates,
                ContextTokenSource.ProviderFraming,
                profile.PerMessageOverheadTokens,
                TokenEstimateClassification.Estimated,
                profile.Confidence);

            if (!string.IsNullOrWhiteSpace(message.AuthorName))
            {
                AddText(
                    estimates,
                    ContextTokenSource.ProviderFraming,
                    message.AuthorName,
                    profile,
                    tokenizer);
            }

            if (message.AdditionalProperties is { Count: > 0 } messageProperties)
            {
                AddText(
                    estimates,
                    ContextTokenSource.ProviderFraming,
                    FormatValue(messageProperties),
                    profile,
                    tokenizer,
                    forceEstimated: true);
            }

            if (message.RawRepresentation is { } rawMessage)
            {
                AddText(
                    estimates,
                    ContextTokenSource.ProviderFraming,
                    FormatValue(rawMessage),
                    profile,
                    tokenizer,
                    forceEstimated: true);
            }

            if (message.Role == ChatRole.System)
            {
                EstimateSystemMessage(
                    estimates,
                    message,
                    profile,
                    tokenizer,
                    request.SystemPromptAttribution);
                foreach (AIContent content in message.Contents)
                {
                    if (content is not TextContent)
                    {
                        EstimateContent(
                            estimates,
                            content,
                            currentPrompt: false,
                            profile,
                            tokenizer);
                    }
                }
            }
            else if (message.Contents.Count == 0 && !string.IsNullOrEmpty(message.Text))
            {
                AddText(
                    estimates,
                    currentPrompt ? ContextTokenSource.CurrentPrompt : ContextTokenSource.History,
                    message.Text,
                    profile,
                    tokenizer);
            }
            else
            {
                foreach (AIContent content in message.Contents)
                {
                    EstimateContent(
                        estimates,
                        content,
                        currentPrompt,
                        profile,
                        tokenizer);
                }
            }

            messageTokenCounts.Add(ContextTokenBreakdown.SaturatingInt(
                SumAll(estimates) - beforeMessage));
        }

        EstimateTools(estimates, request.Options.Tools, profile, tokenizer);
        EstimateStructuredOutput(estimates, request.Options.ResponseFormat, profile, tokenizer);

        if (request.Options.AdditionalProperties is { Count: > 0 } optionProperties)
        {
            AddText(
                estimates,
                ContextTokenSource.ProviderFraming,
                FormatValue(optionProperties),
                profile,
                tokenizer,
                forceEstimated: true);
        }

        if (request.Options.ToolMode is { } toolMode)
        {
            AddText(
                estimates,
                ContextTokenSource.ProviderFraming,
                toolMode.ToString(),
                profile,
                tokenizer,
                forceEstimated: true);
        }

        if (request.Options.StopSequences is { Count: > 0 })
        {
            foreach (string stop in request.Options.StopSequences)
            {
                AddText(estimates, ContextTokenSource.ProviderFraming, stop, profile, tokenizer);
            }
        }

        Add(
            estimates,
            ContextTokenSource.ProviderFraming,
            SaturatingAdd(profile.ProviderFramingTokens, profile.StopTokenOverheadTokens),
            TokenEstimateClassification.Estimated,
            profile.Confidence);

        long preMarginInput = SumInput(estimates);
        int safetyMargin = profile.Type == ModelTokenizationProfileType.ExactLocalTokenizer
            ? 0
            : PercentageCeiling(preMarginInput, profile.SafetyMarginPercent);
        Add(
            estimates,
            ContextTokenSource.SafetyMargin,
            safetyMargin,
            TokenEstimateClassification.Estimated,
            profile.Confidence);

        Add(
            estimates,
            ContextTokenSource.ReservedAnswer,
            Math.Max(0, request.ReservedAnswerTokens),
            TokenEstimateClassification.Reserved,
            confidence: 1d);
        Add(
            estimates,
            ContextTokenSource.ReservedReasoning,
            Math.Max(0, request.ReservedReasoningTokens),
            TokenEstimateClassification.Reserved,
            confidence: 1d);

        List<ContextTokenComponent> components = new(Enum.GetValues<ContextTokenSource>().Length);
        bool hasEstimatedInput = false;
        bool hasUnknownInput = false;

        foreach (ContextTokenSource source in Enum.GetValues<ContextTokenSource>())
        {
            MutableEstimate value = estimates.TryGetValue(source, out MutableEstimate? found)
                ? found
                : MutableEstimate.Empty(source);
            TokenEstimate estimate = new(
                ContextTokenBreakdown.SaturatingInt(value.Count),
                value.Classification,
                profile.ProfileId,
                value.Confidence,
                SafetyMarginTokens: source == ContextTokenSource.SafetyMargin
                    ? ContextTokenBreakdown.SaturatingInt(value.Count)
                    : 0);
            components.Add(new ContextTokenComponent(source, estimate));

            if (source is not ContextTokenSource.ReservedAnswer
                and not ContextTokenSource.ReservedReasoning
                && estimate.TokenCount > 0)
            {
                hasEstimatedInput |= estimate.Classification == TokenEstimateClassification.Estimated;
                hasUnknownInput |= estimate.Classification == TokenEstimateClassification.Unknown;
            }
        }

        int inputTokens = ContextTokenBreakdown.SaturatingInt(SumInput(estimates));
        int reservedTokens = SaturatingAdd(
            Math.Max(0, request.ReservedAnswerTokens),
            Math.Max(0, request.ReservedReasoningTokens));

        return new ContextTokenBreakdown
        {
            Provider = request.Provider.Name,
            Model = request.Model,
            Profile = profile,
            Components = components,
            MessageTokenCounts = messageTokenCounts,
            InputTokens = inputTokens,
            ReservedTokens = reservedTokens,
            ReservedAnswerTokens = Math.Max(0, request.ReservedAnswerTokens),
            ReservedReasoningTokens = Math.Max(0, request.ReservedReasoningTokens),
            TotalTokens = SaturatingAdd(inputTokens, reservedTokens),
            OverallClassification = hasUnknownInput
                ? TokenEstimateClassification.Unknown
                : hasEstimatedInput
                    ? TokenEstimateClassification.Estimated
                    : TokenEstimateClassification.Exact,
            SafetyMarginTokens = safetyMargin,
            PayloadFingerprint = ModelCallPayloadFingerprint.Compute(
                request.Messages,
                request.Options),
        };
    }

    private ResolvedModelTokenizationProfile ResolveUsableProfile(
        ResolvedModelTokenizationProfile profile,
        out Tokenizer tokenizer)
    {
        ResolvedInferenceTokenizer resolved = _tokenizerResolver.Resolve(profile.TokenizerId);
        tokenizer = resolved.Tokenizer;

        if (!resolved.UsedFallback)
        {
            return profile;
        }

        IntelligenceSettings intelligence = ArcanumRuntimeDefaults.Intelligence;
        return profile with
        {
            ProfileId = $"{profile.ProfileId}:fallback:{resolved.ActualEncoding}",
            Type = ModelTokenizationProfileType.UnknownFallback,
            TokenizerId = resolved.ActualEncoding,
            SafetyMarginPercent = ArcanumSettingClamps.EstimatedTokenSafetyMarginPercent(
                intelligence.EstimatedTokenSafetyMarginPercent),
            Confidence = Math.Min(profile.Confidence, 0.5d),
        };
    }

    /// <summary>
    /// Attributes one rendered system prompt across its sources.
    /// </summary>
    /// <remarks>
    /// Covenant spans are removed from the text before the heading-and-fence classifier runs and are
    /// charged from the typed partition instead. The classifier then sees exactly the line structure
    /// a pre-Covenant prompt would have had, so every existing source keeps its previous count and
    /// no untrusted line can claim a Covenant source by imitating its heading (§10.13).
    /// </remarks>
    private static void EstimateSystemMessage(
        Dictionary<ContextTokenSource, MutableEstimate> estimates,
        ChatMessage message,
        ResolvedModelTokenizationProfile profile,
        Tokenizer tokenizer,
        SystemPromptAttributionMap? attribution)
    {
        string text = message.Text ?? string.Empty;

        if (attribution is { HasCovenantContent: true }
            && string.Equals(attribution.Prompt, text, StringComparison.Ordinal))
        {
            AddText(
                estimates,
                ContextTokenSource.CovenantConfirmed,
                attribution.Concatenate(CovenantPromptAttribution.CovenantConfirmed),
                profile,
                tokenizer);
            AddText(
                estimates,
                ContextTokenSource.CovenantProposed,
                attribution.Concatenate(CovenantPromptAttribution.CovenantProposed),
                profile,
                tokenizer);

            text = attribution.ExcludingCovenant();
        }

        if (text.Length == 0)
        {
            return;
        }

        Dictionary<ContextTokenSource, MutableEstimate> sections = [];
        ContextTokenSource source = ContextTokenSource.SystemCodexSpell;
        int segmentStart = 0;
        int lineStart = 0;
        string? activeFence = null;

        while (lineStart < text.Length)
        {
            int newline = text.IndexOf('\n', lineStart);
            int lineEnd = newline < 0 ? text.Length : newline;
            ReadOnlySpan<char> line = text.AsSpan(lineStart, lineEnd - lineStart);
            ReadOnlySpan<char> trimmed = line.Trim();
            ContextTokenSource next = source;
            if (activeFence is not null)
            {
                if (trimmed.Equals(activeFence, StringComparison.Ordinal))
                {
                    activeFence = null;
                }
            }
            else if (TryReadFence(trimmed, out string? fence))
            {
                activeFence = fence;
            }
            else
            {
                next = ClassifySystemHeading(line, source);
            }

            if (next != source && lineStart > segmentStart)
            {
                AddText(
                    sections,
                    source,
                    text[segmentStart..lineStart],
                    profile,
                    tokenizer);
                segmentStart = lineStart;
            }

            source = next;
            lineStart = newline < 0 ? text.Length : newline + 1;
        }

        AddText(sections, source, text[segmentStart..], profile, tokenizer);

        TokenEstimate whole = CountText(text, profile, tokenizer);
        long sectionTotal = SumAll(sections);
        long excess = Math.Max(0, sectionTotal - whole.TokenCount);
        if (excess > 0)
        {
            ContextTokenSource[] reductionOrder =
            [
                ContextTokenSource.SystemCodexSpell,
                ContextTokenSource.TapestryRag,
                ContextTokenSource.LexiconSaga,
                ContextTokenSource.WorkspaceRag,
                ContextTokenSource.AttachmentRag,
                ContextTokenSource.ExplicitAttachments,
                ContextTokenSource.RefreshedFiles,
            ];
            foreach (ContextTokenSource candidate in reductionOrder)
            {
                if (!sections.TryGetValue(candidate, out MutableEstimate? section))
                {
                    continue;
                }

                long reduction = Math.Min(section.Count, excess);
                section.Count -= reduction;
                excess -= reduction;
                if (excess == 0)
                {
                    break;
                }
            }
        }
        else if (sectionTotal < whole.TokenCount)
        {
            Add(
                sections,
                ContextTokenSource.SystemCodexSpell,
                whole.TokenCount - sectionTotal,
                whole.Classification,
                whole.Confidence);
        }

        bool allocationEstimated = sections.Count > 1;
        foreach ((ContextTokenSource sectionSource, MutableEstimate section) in sections)
        {
            Add(
                estimates,
                sectionSource,
                section.Count,
                allocationEstimated
                    ? TokenEstimateClassification.Estimated
                    : section.Classification,
                allocationEstimated
                    ? Math.Min(section.Confidence, 0.9d)
                    : section.Confidence);
        }
    }

    private static ContextTokenSource ClassifySystemHeading(
        ReadOnlySpan<char> line,
        ContextTokenSource current)
    {
        ReadOnlySpan<char> heading = line.TrimStart();
        if (heading.StartsWith("### Lexicon", StringComparison.OrdinalIgnoreCase)
            || heading.StartsWith("### Saga", StringComparison.OrdinalIgnoreCase))
        {
            return ContextTokenSource.LexiconSaga;
        }

        if (heading.StartsWith("### Semantic Context", StringComparison.OrdinalIgnoreCase))
        {
            return ContextTokenSource.WorkspaceRag;
        }

        if (heading.StartsWith("### Hierarchical Context", StringComparison.OrdinalIgnoreCase))
        {
            return ContextTokenSource.TapestryRag;
        }

        if (heading.StartsWith("### Session Attachments Index", StringComparison.OrdinalIgnoreCase))
        {
            return ContextTokenSource.SystemCodexSpell;
        }

        if (heading.StartsWith("### Retrieved Session Attachment Context", StringComparison.OrdinalIgnoreCase))
        {
            return ContextTokenSource.AttachmentRag;
        }

        if (heading.StartsWith("### Attached Files", StringComparison.OrdinalIgnoreCase))
        {
            return ContextTokenSource.ExplicitAttachments;
        }

        return heading.StartsWith("## ", StringComparison.Ordinal)
            || heading.StartsWith("### ", StringComparison.Ordinal)
            ? ContextTokenSource.SystemCodexSpell
            : current;
    }

    private static bool TryReadFence(
        ReadOnlySpan<char> line,
        out string? fence)
    {
        int ticks = 0;
        while (ticks < line.Length && line[ticks] == '`')
        {
            ticks++;
        }

        if (ticks >= 3 && line[ticks..].Trim().IsEmpty)
        {
            fence = line[..ticks].ToString();
            return true;
        }

        fence = null;
        return false;
    }

    private static void EstimateContent(
        Dictionary<ContextTokenSource, MutableEstimate> estimates,
        AIContent content,
        bool currentPrompt,
        ResolvedModelTokenizationProfile profile,
        Tokenizer tokenizer)
    {
        ContextTokenSource metadataSource = ResolveContentSource(content, currentPrompt);

        if (content.AdditionalProperties is { Count: > 0 } contentProperties)
        {
            AddText(
                estimates,
                metadataSource,
                FormatValue(contentProperties),
                profile,
                tokenizer,
                forceEstimated: true);
        }

        if (content.RawRepresentation is { } rawContent)
        {
            AddText(
                estimates,
                metadataSource,
                FormatValue(rawContent),
                profile,
                tokenizer,
                forceEstimated: true);
        }

        switch (content)
        {
            case TextContent text:
                AddText(
                    estimates,
                    metadataSource,
                    text.Text,
                    profile,
                    tokenizer);
                return;

            case TextReasoningContent reasoning:
                AddText(
                    estimates,
                    ContextTokenSource.History,
                    reasoning.Text,
                    profile,
                    tokenizer);
                AddText(
                    estimates,
                    ContextTokenSource.History,
                    reasoning.ProtectedData,
                    profile,
                    tokenizer,
                    forceEstimated: true);
                return;

            case FunctionCallContent functionCall:
                AddText(estimates, ContextTokenSource.Tools, functionCall.CallId, profile, tokenizer);
                AddText(estimates, ContextTokenSource.Tools, functionCall.Name, profile, tokenizer);
                AddText(
                    estimates,
                    ContextTokenSource.Tools,
                    FormatValue(functionCall.Arguments),
                    profile,
                    tokenizer,
                    forceEstimated: true);
                Add(
                    estimates,
                    ContextTokenSource.ProviderFraming,
                    profile.PerToolOverheadTokens,
                    TokenEstimateClassification.Estimated,
                    profile.Confidence);
                return;

            case FunctionResultContent functionResult:
                AddText(estimates, ContextTokenSource.Tools, functionResult.CallId, profile, tokenizer);
                AddText(
                    estimates,
                    ContextTokenSource.Tools,
                    FormatValue(functionResult.Result),
                    profile,
                    tokenizer,
                    forceEstimated: true);
                Add(
                    estimates,
                    ContextTokenSource.ProviderFraming,
                    profile.PerToolOverheadTokens,
                    TokenEstimateClassification.Estimated,
                    profile.Confidence);
                return;

            case DataContent data:
                EstimateBinary(
                    estimates,
                    metadataSource,
                    data.MediaType,
                    data.Data.Length,
                    profile);
                return;

            case UriContent uri:
                if (IsImage(uri.MediaType))
                {
                    Add(
                        estimates,
                        metadataSource,
                        profile.UnknownImageReserveTokens,
                        TokenEstimateClassification.Unknown,
                        Math.Min(profile.Confidence, 0.5d));
                }
                else
                {
                    AddText(
                        estimates,
                        metadataSource,
                        uri.Uri?.ToString(),
                        profile,
                        tokenizer,
                        forceEstimated: true);
                }

                return;

            default:
                AddText(
                    estimates,
                    metadataSource,
                    content.ToString(),
                    profile,
                    tokenizer,
                    forceEstimated: true);
                return;
        }
    }

    private static void EstimateBinary(
        Dictionary<ContextTokenSource, MutableEstimate> estimates,
        ContextTokenSource source,
        string? mediaType,
        int byteLength,
        ResolvedModelTokenizationProfile profile)
    {
        int reserve = IsImage(mediaType)
            ? profile.UnknownImageReserveTokens
            : Math.Max(profile.UnknownImageReserveTokens, (byteLength + 2) / 3);
        Add(
            estimates,
            source,
            reserve,
            IsImage(mediaType)
                ? TokenEstimateClassification.Unknown
                : TokenEstimateClassification.Estimated,
            Math.Min(profile.Confidence, 0.5d));
    }

    private static ContextTokenSource ResolveContentSource(
        AIContent content,
        bool currentPrompt)
    {

        if (content.AdditionalProperties is { } properties
            && properties.TryGetValue("arcanum.context_source", out object? value))
        {

            if (string.Equals(value?.ToString(), "refreshedFile", StringComparison.Ordinal))
            {

                return ContextTokenSource.RefreshedFiles;

            }

            if (string.Equals(value?.ToString(), "explicitAttachment", StringComparison.Ordinal))
            {

                return ContextTokenSource.ExplicitAttachments;

            }

        }

        return content is FunctionCallContent or FunctionResultContent
            ? ContextTokenSource.Tools
            : content is DataContent or UriContent
                ? ContextTokenSource.ExplicitAttachments
                : currentPrompt
                    ? ContextTokenSource.CurrentPrompt
                    : ContextTokenSource.History;

    }

    private static void EstimateTools(
        Dictionary<ContextTokenSource, MutableEstimate> estimates,
        IList<AITool>? tools,
        ResolvedModelTokenizationProfile profile,
        Tokenizer tokenizer)
    {
        if (tools is null)
        {
            return;
        }

        foreach (AITool tool in tools)
        {
            Add(
                estimates,
                ContextTokenSource.Tools,
                profile.PerToolOverheadTokens,
                TokenEstimateClassification.Estimated,
                profile.Confidence);
            AddText(estimates, ContextTokenSource.Tools, tool.Name, profile, tokenizer);
            AddText(estimates, ContextTokenSource.Tools, tool.Description, profile, tokenizer);
            if (tool.AdditionalProperties is { Count: > 0 } toolProperties)
            {
                AddText(
                    estimates,
                    ContextTokenSource.Tools,
                    FormatValue(toolProperties),
                    profile,
                    tokenizer,
                    forceEstimated: true);
            }

            if (tool is AIFunction function)
            {
                AddText(
                    estimates,
                    ContextTokenSource.Tools,
                    function.JsonSchema.GetRawText(),
                    profile,
                    tokenizer);
            }
        }
    }

    private static void EstimateStructuredOutput(
        Dictionary<ContextTokenSource, MutableEstimate> estimates,
        ChatResponseFormat? responseFormat,
        ResolvedModelTokenizationProfile profile,
        Tokenizer tokenizer)
    {
        if (responseFormat is not ChatResponseFormatJson json)
        {
            return;
        }

        AddText(estimates, ContextTokenSource.StructuredOutput, json.SchemaName, profile, tokenizer);
        AddText(estimates, ContextTokenSource.StructuredOutput, json.SchemaDescription, profile, tokenizer);

        if (json.Schema is JsonElement schema)
        {
            AddText(
                estimates,
                ContextTokenSource.StructuredOutput,
                schema.GetRawText(),
                profile,
                tokenizer);
        }
    }

    private static void AddText(
        Dictionary<ContextTokenSource, MutableEstimate> estimates,
        ContextTokenSource source,
        string? text,
        ResolvedModelTokenizationProfile profile,
        Tokenizer tokenizer,
        bool forceEstimated = false)
    {
        TokenEstimate counted = CountText(text, profile, tokenizer);
        TokenEstimateClassification classification = forceEstimated
            ? TokenEstimateClassification.Estimated
            : counted.Classification;
        Add(
            estimates,
            source,
            counted.TokenCount,
            classification,
            forceEstimated ? Math.Min(counted.Confidence, 0.7d) : counted.Confidence);
    }

    private static TokenEstimate CountText(
        string? text,
        ResolvedModelTokenizationProfile profile,
        Tokenizer tokenizer)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new TokenEstimate(
                0,
                profile.Type == ModelTokenizationProfileType.ExactLocalTokenizer
                    ? TokenEstimateClassification.Exact
                    : TokenEstimateClassification.Estimated,
                profile.ProfileId,
                profile.Confidence);
        }

        TextTokenCacheKey key = new(
            profile.TokenizerId,
            ModelCallPayloadFingerprint.ComputeText(text));
        if (!TextTokenCache.TryGetValue(key, out int tokenCount))
        {
            tokenCount = tokenizer.CountTokens(text);
            TextTokenCache.Set(key, tokenCount);
        }

        if (profile.Type == ModelTokenizationProfileType.UnknownFallback)
        {
            tokenCount = Math.Max(tokenCount, Encoding.UTF8.GetByteCount(text));
        }

        return new TokenEstimate(
            tokenCount,
            profile.Type == ModelTokenizationProfileType.ExactLocalTokenizer
                ? TokenEstimateClassification.Exact
                : TokenEstimateClassification.Estimated,
            profile.ProfileId,
            profile.Type == ModelTokenizationProfileType.ExactLocalTokenizer
                ? 1d
                : profile.Confidence);
    }

    private static void Add(
        Dictionary<ContextTokenSource, MutableEstimate> estimates,
        ContextTokenSource source,
        long count,
        TokenEstimateClassification classification,
        double confidence)
    {
        if (!estimates.TryGetValue(source, out MutableEstimate? estimate))
        {
            estimate = new MutableEstimate(
                0,
                classification,
                Math.Clamp(confidence, 0d, 1d));
            estimates[source] = estimate;
        }

        estimate.Count = SaturatingAdd(estimate.Count, Math.Max(0, count));
        estimate.Classification = MergeClassification(estimate.Classification, classification);
        estimate.Confidence = Math.Min(estimate.Confidence, Math.Clamp(confidence, 0d, 1d));
    }

    private static TokenEstimateClassification MergeClassification(
        TokenEstimateClassification left,
        TokenEstimateClassification right)
    {
        if (left == TokenEstimateClassification.Reserved || right == TokenEstimateClassification.Reserved)
        {
            return TokenEstimateClassification.Reserved;
        }

        if (left == TokenEstimateClassification.Unknown || right == TokenEstimateClassification.Unknown)
        {
            return TokenEstimateClassification.Unknown;
        }

        if (left == TokenEstimateClassification.Estimated || right == TokenEstimateClassification.Estimated)
        {
            return TokenEstimateClassification.Estimated;
        }

        return TokenEstimateClassification.Exact;
    }

    private static long SumInput(Dictionary<ContextTokenSource, MutableEstimate> estimates)
    {
        long total = 0;
        foreach ((ContextTokenSource source, MutableEstimate estimate) in estimates)
        {
            if (source is ContextTokenSource.ReservedAnswer or ContextTokenSource.ReservedReasoning)
            {
                continue;
            }

            total = SaturatingAdd(total, estimate.Count);
        }

        return total;
    }

    private static long SumAll(Dictionary<ContextTokenSource, MutableEstimate> estimates)
    {
        long total = 0;
        foreach (MutableEstimate estimate in estimates.Values)
        {
            total = SaturatingAdd(total, estimate.Count);
        }

        return total;
    }

    private static int FindFinalUserIndex(IReadOnlyList<ChatMessage> messages)
    {
        for (int index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ChatRole.User)
            {
                return index;
            }
        }

        return -1;
    }

    internal static string FormatValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string text)
        {
            return text;
        }

        if (value is JsonElement element)
        {
            return element.GetRawText();
        }

        if (value is JsonDocument document)
        {
            return document.RootElement.GetRawText();
        }

        StringBuilder builder = new();
        AppendValue(builder, value);
        return builder.ToString();
    }

    private static void AppendValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                _ = builder.Append("null");
                return;
            case string text:
                _ = builder.Append(text);
                return;
            case JsonElement element:
                _ = builder.Append(element.GetRawText());
                return;
            case JsonDocument document:
                _ = builder.Append(document.RootElement.GetRawText());
                return;
            case IDictionary<string, object?> dictionary:
                _ = builder.Append('{');
                bool first = true;
                foreach ((string key, object? item) in dictionary.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        _ = builder.Append(',');
                    }

                    first = false;
                    _ = builder.Append(key).Append(':');
                    AppendValue(builder, item);
                }

                _ = builder.Append('}');
                return;
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                _ = builder.Append('{');
                bool firstReadOnly = true;
                foreach ((string key, object? item) in readOnlyDictionary.OrderBy(
                    static pair => pair.Key,
                    StringComparer.Ordinal))
                {
                    if (!firstReadOnly)
                    {
                        _ = builder.Append(',');
                    }

                    firstReadOnly = false;
                    _ = builder.Append(key).Append(':');
                    AppendValue(builder, item);
                }

                _ = builder.Append('}');
                return;
            case IEnumerable sequence when value is not string:
                _ = builder.Append('[');
                bool firstItem = true;
                foreach (object? item in sequence)
                {
                    if (!firstItem)
                    {
                        _ = builder.Append(',');
                    }

                    firstItem = false;
                    AppendValue(builder, item);
                }

                _ = builder.Append(']');
                return;
            case IFormattable formattable:
                _ = builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            default:
                _ = builder.Append(value.ToString());
                return;
        }
    }

    private int ResolveUnknownImageReserve()
    {
        IntelligenceSettings intelligence = ArcanumRuntimeDefaults.Intelligence;
        return ArcanumSettingClamps.UnknownImageTokenReserve(
            intelligence.UnknownImageTokenReserve);
    }

    private static bool IsImage(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType)
        && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static int PercentageCeiling(long value, int percentage)
    {
        if (value <= 0 || percentage <= 0)
        {
            return 0;
        }

        Int128 rounded = ((Int128)value * percentage + 99) / 100;
        return int.CreateSaturating(rounded);
    }

    private static int SaturatingAdd(int left, int right) =>
        ContextTokenBreakdown.SaturatingInt((long)Math.Max(0, left) + Math.Max(0, right));

    private static long SaturatingAdd(long left, long right) =>
        long.CreateSaturating((Int128)left + right);

    private sealed class MutableEstimate(
        long count,
        TokenEstimateClassification classification,
        double confidence)
    {
        public long Count { get; set; } = count;

        public TokenEstimateClassification Classification { get; set; } = classification;

        public double Confidence { get; set; } = confidence;

        public static MutableEstimate Empty(ContextTokenSource source) =>
            new(
                0,
                source is ContextTokenSource.ReservedAnswer or ContextTokenSource.ReservedReasoning
                    ? TokenEstimateClassification.Reserved
                    : TokenEstimateClassification.Exact,
                1d);
    }

    private readonly record struct TextTokenCacheKey(
        string TokenizerId,
        string ContentHash);
}
