using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class ModelTokenEstimator
{
    public ContextTokenBreakdown EstimateLongRestChat(
        ProviderSettings provider,
        string canonicalModel,
        LongRestProviderPayload payload)
    {
        ArgumentNullException.ThrowIfNull(provider);

        ArgumentNullException.ThrowIfNull(payload);

        LongRestChatRequest request = LongRestProviderCallFreezer.RenderChat(payload).Value;

        return EstimateClosedChat(provider, canonicalModel, request.SystemPrompt, request.UserPrompt,
            [request.SchemaName, request.SchemaDescription, request.SchemaJson], request.MaxOutputTokens) with
        {
            PayloadFingerprint = Convert.ToHexString(LongRestProviderCallFreezer.PayloadDigest(payload, provider, canonicalModel).Value.Bytes),
        };
    }

    public TokenEstimate EstimateLongRestEmbedding(ProviderSettings provider, string canonicalModel, LongRestProviderPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Kind != LongRestProviderPayloadKind.Embedding)
        {
            throw new ArgumentException("An embedding estimate requires the closed embedding payload.", nameof(payload));
        }

        ResolvedModelTokenizationProfile profile = ResolveEffectiveProfile(provider, canonicalModel);

        InferenceTextCounter counter = _tokenizerResolver.Resolve(profile.TokenizerId).Counter;

        TokenEstimate raw = CountCampaignText(payload.EmbeddingText!, profile, counter);

        int margin = profile.Type == ModelTokenizationProfileType.ExactLocalTokenizer ? 0 : PercentageCeiling(raw.TokenCount, profile.SafetyMarginPercent);

        return raw with { TokenCount = SaturatingAdd(raw.TokenCount, margin), SafetyMarginTokens = margin };
    }
}
