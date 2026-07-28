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

        int budget = WeaveIndexAvailability.ManagedSearchRowBudget;

        if (!embeddings.Enabled)
        {

            return (
                false,
                WeaveIndexAvailability.ModeDisabled,
                "Embeddings are disabled (no embedding-backed Arcanum:Features opt-in is enabled).",
                budget);

        }

        if (string.IsNullOrWhiteSpace(embeddings.Provider) || string.IsNullOrWhiteSpace(embeddings.Model))
        {

            return (
                true,
                WeaveIndexAvailability.ModeUnavailable,
                "Embeddings are enabled but Arcanum:Integrations:Embeddings:Provider or Arcanum:Integrations:Embeddings:Model is not configured.",
                budget);

        }

        if (availability.IsVecAvailable)
        {

            return (true, WeaveIndexAvailability.ModeVec0, availability.Diagnostic, budget);

        }

        return (
            true,
            WeaveIndexAvailability.ModeManaged,
            "managed SIMD fallback; preview/performance-limited; row budget "
            + budget
            + ". "
            + availability.Diagnostic,
            budget);

    }

}
