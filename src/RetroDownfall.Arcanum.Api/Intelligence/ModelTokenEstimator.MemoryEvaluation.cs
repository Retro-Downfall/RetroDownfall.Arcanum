using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class ModelTokenEstimator
{
    public ContextTokenBreakdown EstimateMemoryEvaluation(ProviderSettings provider, string canonicalModel, string systemPrompt, string userPrompt, int maxOutputTokens)
    {
        using CovenantCanonicalHashWriter writer = new();

        writer.WriteUtf8("arcanum.memory-evaluation.payload.v1");

        writer.WriteUtf8(systemPrompt);

        writer.WriteUtf8(userPrompt);

        writer.WriteInt32(maxOutputTokens);

        return EstimateClosedChat(provider, canonicalModel, systemPrompt, userPrompt, [], maxOutputTokens) with
        {
            PayloadFingerprint = Convert.ToHexString(writer.FinalizeDigest().Bytes),
        };
    }
}
