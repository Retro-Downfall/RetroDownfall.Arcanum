using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Issue #89 — the one disclosure copy every enable and destroy surface reads from.
/// </summary>
/// <remarks>
/// The defect this prevents is a paraphrase. The Compendium toggle, the CLI reinitialize prompt, the
/// reset prompt, and the restore prompt all have to tell an operator the same true thing about what
/// leaves the machine, and four surfaces each writing their own sentence is four subtly different
/// promises. Worse, a paraphrase drifts toward reassurance: "Arcanum deletes your data" is easier to
/// write than "Arcanum cannot un-send what a provider already received."
///
/// <para>The URI tests pin identity, not marketing. A help target is selected by an exact endpoint
/// host or an exact Familiar kind, never by a provider's display name, because a display name is
/// operator-authored text and a name-driven link would let an <c>arcanum.json</c> edit point an
/// operator at a page about somebody else's retention policy.</para>
/// </remarks>
public sealed class CovenantExternalRetentionDisclosureTests
{

    [Fact]
    public void Disclosure_constants_match_the_approved_golden_copy()
    {

        Assert.Equal(
            "Enabling The Covenant sends eligible content on every primary, fallback, retry, "
                + "compression, and tool-loop provider attempt. A single turn may use different "
                + "configured providers or models. Provider logs and automatic prompt caches are "
                + "outside local reset and cannot be revoked by Arcanum. Arcanum suppresses only its "
                + "own explicit cache instructions for Covenant-bearing calls.",
            CovenantExternalRetentionDisclosure.EnablementText);

        Assert.Equal(
            "Local disable, reset, protected-state purge, family reinitialize, and factory erasure "
                + "cannot revoke content retained in provider logs or automatic prompt caches, "
                + "encrypted backup copies, unmanaged files, or other nonrevocable disclosures. "
                + "Review each configured provider's retention and deletion documentation and "
                + "complete any external deletion separately.",
            CovenantExternalRetentionDisclosure.DestructiveOperationText);

    }

    [Fact]
    public void Disclosure_never_claims_a_local_provider_cache_control()
    {

        Assert.DoesNotContain(
            "disable provider caching",
            CovenantExternalRetentionDisclosure.EnablementText,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            "delete",
            CovenantExternalRetentionDisclosure.EnablementText,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "cannot be revoked by Arcanum",
            CovenantExternalRetentionDisclosure.EnablementText,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Known_openai_api_codex_and_claude_code_targets_use_official_data_handling_pages()
    {

        ImmutableArray<CovenantRetentionHelpTarget> targets =
            CovenantExternalRetentionDisclosure.ResolveHelpTargets(
            [
                new ProviderSettings { Name = "openai", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://api.openai.com/v1" },
                new ProviderSettings { Name = "codex", Type = AiProviderKind.CodexCli },
                new ProviderSettings { Name = "claude", Type = AiProviderKind.ClaudeCodeCli },
            ]);

        Assert.Equal(
            "https://developers.openai.com/api/docs/guides/your-data#default-usage-policies-by-endpoint",
            Assert.Single(targets, target => target.Provider == "openai").Uri);

        Assert.Equal(
            "https://openai.com/policies/how-your-data-is-used-to-improve-model-performance/",
            Assert.Single(targets, target => target.Provider == "codex").Uri);

        Assert.Equal(
            "https://privacy.claude.com/en/collections/10672565-data-handling-retention",
            Assert.Single(targets, target => target.Provider == "claude").Uri);

        Assert.All(
            targets.Where(target => target.Kind == CovenantRetentionHelpKind.ProviderRetentionDocumentation),
            target => Assert.StartsWith("https://", target.Uri, StringComparison.Ordinal));

    }

    [Fact]
    public void Unknown_proxy_and_self_hosted_targets_use_providers_page_and_operator_guide()
    {

        ImmutableArray<CovenantRetentionHelpTarget> targets =
            CovenantExternalRetentionDisclosure.ResolveHelpTargets(
            [
                new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Endpoint = "http://localhost:11434/v1" },
                new ProviderSettings { Name = "corp-proxy", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://gateway.example.internal/openai/v1" },
            ]);

        Assert.Equal(2, targets.Count(target => target.Kind == CovenantRetentionHelpKind.ConfiguredProvidersPage));

        Assert.DoesNotContain(targets, target => target.Kind == CovenantRetentionHelpKind.ProviderRetentionDocumentation);

        CovenantRetentionHelpTarget guide = Assert.Single(
            targets,
            target => target.Kind == CovenantRetentionHelpKind.OperatorGuide);

        Assert.Equal(CovenantExternalRetentionDisclosure.OperatorGuideTarget, guide.Uri);

        Assert.Equal("docs/Arcanum.Engineering.md#covenant-provider-retention-and-deletion", guide.Uri);

    }

    [Fact]
    public void Operator_guide_target_is_always_present_even_with_no_configured_provider()
    {

        ImmutableArray<CovenantRetentionHelpTarget> targets =
            CovenantExternalRetentionDisclosure.ResolveHelpTargets([]);

        Assert.Equal(CovenantRetentionHelpKind.OperatorGuide, Assert.Single(targets).Kind);

    }

    [Fact]
    public void Provider_display_name_cannot_select_an_external_uri()
    {

        // The name says OpenAI; the endpoint is somebody's gateway. A name-driven resolver would
        // send the operator to openai.com to read the retention policy of a host OpenAI has never
        // seen a byte from.
        ImmutableArray<CovenantRetentionHelpTarget> targets =
            CovenantExternalRetentionDisclosure.ResolveHelpTargets(
            [
                new ProviderSettings
                {
                    Name = "api.openai.com",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://not-openai.example.com/v1",
                },
            ]);

        Assert.DoesNotContain(targets, target => target.Kind == CovenantRetentionHelpKind.ProviderRetentionDocumentation);

        Assert.Contains(targets, target => target.Kind == CovenantRetentionHelpKind.ConfiguredProvidersPage);

    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://API.OpenAI.com/v1")]
    [InlineData("https://api.openai.com")]
    public void Openai_host_is_matched_exactly_and_case_insensitively(string endpoint)
    {

        ImmutableArray<CovenantRetentionHelpTarget> targets =
            CovenantExternalRetentionDisclosure.ResolveHelpTargets(
            [
                new ProviderSettings { Name = "p", Type = AiProviderKind.OpenAICompatible, Endpoint = endpoint },
            ]);

        Assert.Contains(targets, target => target.Kind == CovenantRetentionHelpKind.ProviderRetentionDocumentation);

    }

    [Theory]
    [InlineData("https://api.openai.com.evil.example/v1")]
    [InlineData("https://openai.com/v1")]
    [InlineData("not a uri")]
    [InlineData("")]
    public void A_lookalike_or_unparsable_endpoint_never_reaches_the_official_page(string endpoint)
    {

        ImmutableArray<CovenantRetentionHelpTarget> targets =
            CovenantExternalRetentionDisclosure.ResolveHelpTargets(
            [
                new ProviderSettings { Name = "p", Type = AiProviderKind.OpenAICompatible, Endpoint = endpoint },
            ]);

        Assert.DoesNotContain(targets, target => target.Kind == CovenantRetentionHelpKind.ProviderRetentionDocumentation);

    }

    [Fact]
    public void One_target_per_provider_survives_a_duplicated_endpoint()
    {

        ImmutableArray<CovenantRetentionHelpTarget> targets =
            CovenantExternalRetentionDisclosure.ResolveHelpTargets(
            [
                new ProviderSettings { Name = "a", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://api.openai.com/v1" },
                new ProviderSettings { Name = "b", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://api.openai.com/v1" },
            ]);

        Assert.Equal(
            2,
            targets.Count(target => target.Kind == CovenantRetentionHelpKind.ProviderRetentionDocumentation));

        Assert.Equal(3, targets.Length);

    }

}
