using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Single source of truth for flattening configured models across all providers into
/// <see cref="ModelInfoDto"/>. Shared by <c>GET /api/models</c> (native) and
/// <c>GET /v1/models</c> (OpenAI-compatible, enriched with capability fields) so the
/// two surfaces never drift out of sync.
/// </summary>
/// <remarks>
/// This is also where a Familiar's hide list is applied, and it is applied exactly here so that
/// every listing surface — <c>/api/models</c>, <c>/v1/models</c>, <c>arcanum model list</c>, the
/// Command Center drop-down, The Forge's Arsenal — inherits it from one place. Resolution does not
/// come through here, which is what keeps a hidden model resolvable by explicit name: hiding is a
/// decluttering preference, not a policy control.
/// </remarks>
internal static class ModelInfoBuilder
{

    public static List<ModelInfoDto> BuildModelInfoList(ArcanumSettings settings)
    {

        List<ModelInfoDto> models = [];

        foreach (ProviderSettings provider in settings.Providers ?? [])
        {

            string redactedEndpoint = RedactRequired(provider.Endpoint);

            foreach (ModelEntry model in provider.Models ?? [])
            {

                if (string.IsNullOrWhiteSpace(model.Name))
                {

                    continue;

                }

                if (FamiliarProviders.IsHidden(provider, model.Name))
                {

                    continue;

                }

                // maxBudgetTokens means nothing without the dialect that says how a budget is carried on
                // the wire, so the two are advertised together or not at all. Startup validation refuses
                // that pairing now, which makes this defence in depth — but this is the published
                // capability surface, and projecting the two fields independently is what let them
                // disagree in the first place.
                ReasoningWireDialect? wireDialect = model.Reasoning?.WireDialect;

                models.Add(new ModelInfoDto(
                    model.Name,
                    provider.Name,
                    provider.Type.ToString(),
                    redactedEndpoint,
                    provider.ContextWindowLimit,
                    model.SupportsVision,
                    wireDialect,
                    wireDialect is null ? null : model.Reasoning?.MaxBudgetTokens,
                    ModelCapabilityCatalog.ResolvePromptCaching(provider, model.Name)));

            }

        }

        return models;

    }

    /// <summary>
    /// Maps <see cref="ProviderSettings.Type"/> (as already stringified on <see cref="ModelInfoDto.ProviderType"/>)
    /// to the OpenAI-community snake_case convention (<c>openai_compatible</c>)
    /// for <c>GET /v1/models</c>. <see cref="ModelInfoDto.ProviderType"/> itself stays PascalCase
    /// (<c>AiProviderKind.ToString()</c>) since that is the existing, non-breaking wire shape for
    /// <c>GET /api/models</c>.
    /// </summary>
    public static string ToSnakeCaseProviderType(string providerType) =>
        providerType switch
        {
            nameof(AiProviderKind.OpenAICompatible) => "openai_compatible",
            _ => providerType.ToLowerInvariant(),
        };

    private static string RedactRequired(string value) =>
        string.IsNullOrEmpty(value) ? value : "***";

}
