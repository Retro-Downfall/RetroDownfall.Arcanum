using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class ModelTokenEstimator
{
    public ContextTokenBreakdown EstimateCampaignMaintenance(
        ProviderSettings provider,
        string canonicalModel,
        CampaignMaintenancePayload payload)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(payload);

        return EstimateClosedChat(provider, canonicalModel, payload.SystemPrompt, payload.UserPrompt,
            [CampaignMaintenancePayload.SchemaName, CampaignMaintenancePayload.SchemaDescription, CampaignMaintenancePayload.SchemaJson],
            CampaignMaintenancePayload.MaxOutputTokens) with
        {
            PayloadFingerprint = CampaignMaintenancePayloadFingerprint.Compute(payload),
        };
    }

    private ContextTokenBreakdown EstimateClosedChat(ProviderSettings provider, string canonicalModel,
        string systemPrompt, string userPrompt, string[] schemaParts, int maxOutputTokens)
    {
        ResolvedModelTokenizationProfile profile = ResolveUsableProfile(ResolveProfile(provider, canonicalModel), out _);
        InferenceTextCounter counter = _tokenizerResolver.Resolve(profile.TokenizerId).Counter;
        Dictionary<ContextTokenSource, MutableEstimate> estimates = [];

        TokenEstimate system = CountCampaignText(systemPrompt, profile, counter);
        TokenEstimate user = CountCampaignText(userPrompt, profile, counter);

        Add(estimates, ContextTokenSource.SystemCodexSpell, system.TokenCount, system.Classification, system.Confidence);
        Add(estimates, ContextTokenSource.CurrentPrompt, user.TokenCount, user.Classification, user.Confidence);

        foreach (string part in schemaParts)
        {
            TokenEstimate counted = CountCampaignText(part, profile, counter);

            Add(estimates, ContextTokenSource.StructuredOutput, counted.TokenCount, counted.Classification, counted.Confidence);
        }

        Add(estimates, ContextTokenSource.ProviderFraming,
            (long)profile.PerMessageOverheadTokens * 2 + profile.ProviderFramingTokens + profile.StopTokenOverheadTokens,
            TokenEstimateClassification.Estimated, profile.Confidence);

        int safetyMargin = profile.Type == ModelTokenizationProfileType.ExactLocalTokenizer
            ? 0
            : PercentageCeiling(SumInput(estimates), profile.SafetyMarginPercent);

        Add(estimates, ContextTokenSource.SafetyMargin, safetyMargin, TokenEstimateClassification.Estimated, profile.Confidence);
        Add(estimates, ContextTokenSource.ReservedAnswer, maxOutputTokens, TokenEstimateClassification.Reserved, 1d);
        Add(estimates, ContextTokenSource.ReservedReasoning, 0, TokenEstimateClassification.Reserved, 1d);

        List<ContextTokenComponent> components = [];
        bool estimated = false;
        bool unknown = false;

        foreach (ContextTokenSource source in Enum.GetValues<ContextTokenSource>())
        {
            MutableEstimate value = estimates.TryGetValue(source, out MutableEstimate? found) ? found : MutableEstimate.Empty(source);
            TokenEstimate estimate = new(ContextTokenBreakdown.SaturatingInt(value.Count), value.Classification, profile.ProfileId,
                value.Confidence, source == ContextTokenSource.SafetyMargin ? safetyMargin : 0);

            components.Add(new(source, estimate));

            if (source is not ContextTokenSource.ReservedAnswer and not ContextTokenSource.ReservedReasoning && estimate.TokenCount > 0)
            {
                estimated |= estimate.Classification == TokenEstimateClassification.Estimated;
                unknown |= estimate.Classification == TokenEstimateClassification.Unknown;
            }
        }

        int input = ContextTokenBreakdown.SaturatingInt(SumInput(estimates));

        return new()
        {
            Provider = provider.Name,
            Model = canonicalModel,
            Profile = profile,
            Components = components,
            MessageTokenCounts = [SaturatingAdd(system.TokenCount, profile.PerMessageOverheadTokens), SaturatingAdd(user.TokenCount, profile.PerMessageOverheadTokens)],
            InputTokens = input,
            ReservedTokens = maxOutputTokens,
            ReservedAnswerTokens = maxOutputTokens,
            ReservedReasoningTokens = 0,
            TotalTokens = SaturatingAdd(input, maxOutputTokens),
            OverallClassification = unknown ? TokenEstimateClassification.Unknown : estimated ? TokenEstimateClassification.Estimated : TokenEstimateClassification.Exact,
            SafetyMarginTokens = safetyMargin,
        };
    }

    private static TokenEstimate CountCampaignText(string text, ResolvedModelTokenizationProfile profile, InferenceTextCounter counter)
    {
        TextTokenCacheKey key = new(profile.TokenizerId, ModelCallPayloadFingerprint.ComputeText(text));

        if (!TextTokenCache.TryGetValue(key, out int count))
        {
            count = counter.CountTokens(text);

            TextTokenCache.Set(key, count);
        }

        if (profile.Type == ModelTokenizationProfileType.UnknownFallback)
        {
            count = Math.Max(count, Encoding.UTF8.GetByteCount(text));
        }

        return new(count,
            profile.Type == ModelTokenizationProfileType.ExactLocalTokenizer ? TokenEstimateClassification.Exact : TokenEstimateClassification.Estimated,
            profile.ProfileId,
            profile.Type == ModelTokenizationProfileType.ExactLocalTokenizer ? 1d : profile.Confidence);
    }
}
