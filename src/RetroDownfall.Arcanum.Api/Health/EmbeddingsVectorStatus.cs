using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Api.Health;

/// <summary>Shared Embeddings / Weave status for health, meta, and doctor.</summary>
public static class EmbeddingsVectorStatus
{

    public static (bool Enabled, string Mode, string Diagnostic, int ManagedRowBudget) Resolve(
        EmbeddingSettings embeddings,
        WeaveIndexAvailability availability)
    {

        const int noRowBudget = 0;

        if (!embeddings.Enabled)
        {

            return (
                false,
                WeaveIndexAvailability.ModeDisabled,
                "Embeddings are disabled (no embedding-backed Arcanum:Features opt-in is enabled).",
                noRowBudget);

        }

        if (string.IsNullOrWhiteSpace(embeddings.Provider) || string.IsNullOrWhiteSpace(embeddings.Model))
        {

            return (
                true,
                WeaveIndexAvailability.ModeUnavailable,
                "Embeddings are enabled but Arcanum:Integrations:Embeddings:Provider or Arcanum:Integrations:Embeddings:Model is not configured.",
                noRowBudget);

        }

        if (availability.IsVecAvailable)
        {

            return (true, WeaveIndexAvailability.ModeVec0, availability.Diagnostic, noRowBudget);

        }

        return (
            true,
            WeaveIndexAvailability.ModeManaged,
            "complete streamed managed SIMD fallback; no total row budget. "
            + availability.Diagnostic,
            noRowBudget);

    }

}
