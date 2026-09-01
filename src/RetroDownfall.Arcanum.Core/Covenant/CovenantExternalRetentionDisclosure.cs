using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// What kind of place a retention help action sends the operator.
/// </summary>
/// <remarks>
/// Closed, and the arms are separated by trust rather than by convenience.
/// <see cref="ProviderRetentionDocumentation"/> is an external URI Arcanum ships as a literal, so a
/// surface may open it directly. The other two are internal: one is a Compendium page, one is a
/// repository document. A single "link" kind would leave every consumer deciding for itself whether a
/// value is safe to hand to a browser (§10.18).
/// </remarks>
public enum CovenantRetentionHelpKind : byte
{

    /// <summary>An official, Arcanum-pinned external retention or data-use page.</summary>
    ProviderRetentionDocumentation = 1,

    /// <summary>Compendium's own configured Providers page, for a provider Arcanum cannot recognize.</summary>
    ConfiguredProvidersPage = 2,

    /// <summary>The operator guide's Covenant retention section, always offered.</summary>
    OperatorGuide = 3,

}

/// <summary>
/// One resolved help action, named for the provider that produced it.
/// </summary>
/// <remarks>
/// <paramref name="Provider"/> is the configured display name and exists only so the action can be
/// labelled. It never selects <paramref name="Uri"/>: see
/// <see cref="CovenantExternalRetentionDisclosure.ResolveHelpTargets"/>.
/// </remarks>
public sealed record CovenantRetentionHelpTarget(
    CovenantRetentionHelpKind Kind,
    string Provider,
    string Uri);

/// <summary>
/// The one copy every Covenant enable and destroy surface reads from, plus the help targets that
/// answer "then where do I go to delete it?".
/// </summary>
/// <remarks>
/// Written once because the Compendium toggle, <c>memory covenant doctor --reinitialize-family</c>,
/// <c>data reset-memory</c>, and protected restore all have to tell an operator the same true thing,
/// and four surfaces each writing their own sentence is four subtly different promises. A paraphrase
/// also drifts in one direction: "Arcanum deletes your data" is easier to write than "Arcanum cannot
/// un-send what a provider already received", and the second one is the fact that matters (§10.18).
///
/// <para>Nothing here claims Arcanum can change provider retention, because it cannot. Arcanum
/// suppresses its own explicit cache instructions on Covenant-bearing calls; a provider's own logging
/// and automatic caching are outside the process and outside every local erasure path.</para>
/// </remarks>
public static class CovenantExternalRetentionDisclosure
{

    /// <summary>
    /// What an operator is told before The Covenant is switched on.
    /// </summary>
    public const string EnablementText =
        "Enabling The Covenant sends eligible content on every primary, fallback, retry, "
            + "compression, and tool-loop provider attempt. A single turn may use different "
            + "configured providers or models. Provider logs and automatic prompt caches are "
            + "outside local reset and cannot be revoked by Arcanum. Arcanum suppresses only its "
            + "own explicit cache instructions for Covenant-bearing calls.";

    /// <summary>
    /// What an operator is told before anything is destroyed, and what makes the report honest.
    /// </summary>
    public const string DestructiveOperationText =
        "Local disable, reset, protected-state purge, family reinitialize, and factory erasure "
            + "cannot revoke content retained in provider logs or automatic prompt caches, "
            + "encrypted backup copies, unmanaged files, or other nonrevocable disclosures. "
            + "Review each configured provider's retention and deletion documentation and "
            + "complete any external deletion separately.";

    /// <summary>The operator guide's Covenant retention section. Repository-relative, never a URI.</summary>
    public const string OperatorGuideTarget = "docs/Arcanum.Engineering.md#covenant-provider-retention-and-deletion";

    /// <summary>Compendium's configured Providers page. An internal route, never a URI.</summary>
    public const string ConfiguredProvidersPageTarget = "compendium:providers";

    private const string OpenAiApiHost = "api.openai.com";

    private const string OpenAiApiRetentionUri =
        "https://developers.openai.com/api/docs/guides/your-data#default-usage-policies-by-endpoint";

    private const string CodexCliRetentionUri =
        "https://openai.com/policies/how-your-data-is-used-to-improve-model-performance/";

    private const string ClaudeCodeCliRetentionUri =
        "https://privacy.claude.com/en/collections/10672565-data-handling-retention";

    /// <summary>
    /// Resolves one help action per configured provider, plus the operator guide.
    /// </summary>
    /// <remarks>
    /// Recognition is by exact endpoint host or exact Familiar kind, never by
    /// <see cref="ProviderSettings.Name"/>. A display name is operator-authored text in
    /// <c>arcanum.json</c>, so a name-driven resolver would let a one-line edit point an operator at
    /// <c>openai.com</c> to read the retention policy of a gateway OpenAI has never seen a byte from
    /// — a wrong answer delivered with the full authority of an official link.
    ///
    /// <para>The host comparison is exact and case-insensitive. <c>api.openai.com.evil.example</c>
    /// and <c>openai.com</c> are both unrecognized, because a suffix match is how a lookalike host
    /// earns somebody else's documentation.</para>
    /// </remarks>
    public static ImmutableArray<CovenantRetentionHelpTarget> ResolveHelpTargets(
        IReadOnlyList<ProviderSettings> providers)
    {

        ArgumentNullException.ThrowIfNull(providers);

        ImmutableArray<CovenantRetentionHelpTarget>.Builder targets =
            ImmutableArray.CreateBuilder<CovenantRetentionHelpTarget>(providers.Count + 1);

        foreach (ProviderSettings provider in providers)
        {

            if (provider is null)
            {

                continue;

            }

            string? official = ResolveOfficialUri(provider);

            targets.Add(
                official is null
                    ? new CovenantRetentionHelpTarget(
                        CovenantRetentionHelpKind.ConfiguredProvidersPage,
                        provider.Name,
                        ConfiguredProvidersPageTarget)
                    : new CovenantRetentionHelpTarget(
                        CovenantRetentionHelpKind.ProviderRetentionDocumentation,
                        provider.Name,
                        official));

        }

        // Always last and always present. Even an installation whose every provider is recognized
        // needs somewhere to read what Arcanum itself can and cannot erase.
        targets.Add(
            new CovenantRetentionHelpTarget(
                CovenantRetentionHelpKind.OperatorGuide,
                string.Empty,
                OperatorGuideTarget));

        return targets.ToImmutable();

    }

    private static string? ResolveOfficialUri(ProviderSettings provider) =>
        provider.Type switch
        {
            AiProviderKind.CodexCli => CodexCliRetentionUri,
            AiProviderKind.ClaudeCodeCli => ClaudeCodeCliRetentionUri,
            AiProviderKind.OpenAICompatible => IsOpenAiApiEndpoint(provider.Endpoint)
                ? OpenAiApiRetentionUri
                : null,
            _ => null,
        };

    private static bool IsOpenAiApiEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? parsed)
        && parsed.Host.Equals(OpenAiApiHost, StringComparison.OrdinalIgnoreCase);

}
