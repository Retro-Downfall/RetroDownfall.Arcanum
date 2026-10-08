using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>How the host would route a requested model name.</summary>
internal enum ModelChoiceRoute
{
    /// <summary>A provider offers the model in its listings.</summary>
    Listed,

    /// <summary>A Familiar keeps the model off its listings. Hidden is not blocked.</summary>
    Hidden,

    /// <summary>No row lists the name, so the first Familiar is asked for it exactly as typed.</summary>
    FamiliarPassThrough,

    /// <summary>Nothing would route the name, so it is refused.</summary>
    Unknown,
}

/// <param name="Route">How the host would route the name.</param>
/// <param name="Model">
/// The session model to store: the provider's own spelling when a row lists or hides the name, the
/// trimmed name as typed otherwise.
/// </param>
/// <param name="ProviderName">The provider the name goes to; <see langword="null"/> when refused.</param>
/// <param name="Message">The transcript line reporting the decision.</param>
internal sealed record ModelChoiceDecision(ModelChoiceRoute Route, string Model, string? ProviderName, string Message)
{
    public bool Accepted => Route != ModelChoiceRoute.Unknown;
}

/// <summary>
/// Decides whether <c>/model &lt;name&gt;</c> may change the session model. It refuses only a name
/// the host could not route either, so a typo is caught when it is typed instead of failing the next
/// turn with "The requested model is not configured", and it keeps both documented exceptions: a
/// model on a Familiar's hide list is still accepted, and a name no row lists still goes to a
/// Familiar, whose catalogue belongs to the vendor rather than to <c>arcanum.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// A CLI-side mirror of <see cref="ProviderResolver.TryResolveProviderForModel"/>, fed from the
/// projection <c>GET /api/providers</c> already returns (each provider's visible models, its hide
/// list and its kind, in configured order). It asks the host rather than reading the CLI's own
/// settings because the host is what resolves the name at turn time.
/// <c>CommandCenterModelChoiceTests</c> runs the same names through both, so the copy cannot drift
/// silently.
/// </para>
/// <para>
/// Matching is trimmed, case-insensitive and in configured order: a listed model on any provider,
/// then a hidden one, then the first Familiar. Over the same configuration, acceptance always agrees
/// with the resolver. Only the provider named can differ, in two hide-list corners the projection
/// cannot tell apart: an earlier Familiar that declares and hides a name a later row lists (the
/// resolver picks the Familiar), and a Familiar after the first that hides a name it never declares
/// (the resolver hands it to the first Familiar).
/// </para>
/// </remarks>
internal static class CommandCenterModelChoice
{
    private const string DefaultModelDescription = "the default model";

    /// <summary>Decides how <paramref name="requested"/> routes, and what to tell the operator.</summary>
    /// <param name="providers">The providers <c>GET /api/providers</c> returned, in configured order.</param>
    /// <param name="requested">The name the operator typed. Must not be blank.</param>
    /// <param name="currentModel">The model in use now, named when the request is refused.</param>
    public static ModelChoiceDecision Decide(
        IReadOnlyList<ProviderInfoDto> providers,
        string requested,
        string? currentModel)
    {
        ArgumentNullException.ThrowIfNull(providers);

        ArgumentException.ThrowIfNullOrWhiteSpace(requested);

        string needle = requested.Trim();

        foreach (ProviderInfoDto provider in providers)
        {
            foreach (string model in provider.Models ?? [])
            {
                if (!string.IsNullOrWhiteSpace(model) && ProviderResolver.ModelNameMatches(model, needle))
                {
                    return new ModelChoiceDecision(
                        ModelChoiceRoute.Listed,
                        model,
                        provider.Name,
                        Confirmation(model, provider.Name));
                }
            }
        }

        foreach (ProviderInfoDto provider in providers)
        {
            foreach (string hidden in provider.HiddenModels ?? [])
            {
                // Trimmed on both sides, as FamiliarProviders.IsHidden matches a hide-list entry.
                string entry = hidden?.Trim() ?? string.Empty;

                if (entry.Length > 0 && ProviderResolver.ModelNameMatches(entry, needle))
                {
                    return new ModelChoiceDecision(
                        ModelChoiceRoute.Hidden,
                        entry,
                        provider.Name,
                        $"Model set to {entry} ({provider.Name}, hidden from model lists) for this session.");
                }
            }
        }

        foreach (ProviderInfoDto provider in providers)
        {
            if (Enum.TryParse(provider.Type, ignoreCase: true, out AiProviderKind kind)
                && FamiliarProviders.IsFamiliar(kind))
            {
                return new ModelChoiceDecision(
                    ModelChoiceRoute.FamiliarPassThrough,
                    needle,
                    provider.Name,
                    $"Model set to {needle} for this session. No provider lists it, so {provider.Name} ({provider.Type}) will be asked for it.");
            }
        }

        return new ModelChoiceDecision(
            ModelChoiceRoute.Unknown,
            needle,
            ProviderName: null,
            $"Unknown model `{needle}`; still using {DescribeCurrent(currentModel)}. {DescribeAvailable(providers)}");
    }

    /// <summary>
    /// The confirmation for a model a provider lists, shared by <c>/model &lt;name&gt;</c> and the
    /// header drop-down so the two report a selection in the same words.
    /// </summary>
    public static string Confirmation(string model, string providerName) =>
        $"Model set to {model} ({providerName}) for this session.";

    /// <summary>
    /// The refusal when the provider list could not be read. The name is refused rather than taken on
    /// trust: an unchecked name is exactly what used to fail the next turn.
    /// </summary>
    public static string ProvidersUnavailable(string requested, string error, string? currentModel)
    {
        string reason = (error ?? string.Empty).Trim().TrimEnd('.');

        if (reason.Length == 0)
        {
            reason = "the host gave no reason";
        }

        return $"Could not check `{(requested ?? string.Empty).Trim()}` against the configured providers: {reason}. Still using {DescribeCurrent(currentModel)}.";
    }

    private static string DescribeCurrent(string? currentModel) =>
        string.IsNullOrWhiteSpace(currentModel) ? DefaultModelDescription : currentModel.Trim();

    private static string DescribeAvailable(IReadOnlyList<ProviderInfoDto> providers)
    {
        List<string> available = [];

        foreach (ProviderInfoDto provider in providers)
        {
            foreach (string model in provider.Models ?? [])
            {
                if (!string.IsNullOrWhiteSpace(model))
                {
                    available.Add($"{model} ({provider.Name})");
                }
            }
        }

        return available.Count == 0
            ? "No provider lists any model."
            : $"Available: {string.Join(", ", available)}.";
    }
}
