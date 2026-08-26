using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// A chain is the closed, ordered statement of every version a tier has had and how to reach the one
/// this binary declares. Every way it could be authored wrong is refused at construction, because
/// everything downstream treats a constructed chain as trusted.
/// </summary>
public sealed class GrimoireSchemaVersionChainTests
{

    [Fact]
    public void Every_shipped_tier_is_at_version_one_with_no_step()
    {

        foreach (GrimoireSchemaTransactionTier tier in Enum.GetValues<GrimoireSchemaTransactionTier>())
        {

            GrimoireSchemaVersionChain chain = GrimoireSchemaVersionChains.Default.ForTier(tier);

            Assert.Equal(1, chain.HeadVersion);

            Assert.Empty(chain.Steps);

        }

    }

    [Fact]
    public void The_head_fingerprint_answers_for_the_head_version()
    {

        GrimoireSchemaVersionChain chain = GrimoireSchemaVersionChains.Default
            .ForTier(GrimoireSchemaTransactionTier.CovenantCanonical);

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint,
            chain.SourceDefinitionFingerprintFor(1));

    }

    [Fact]
    public void A_version_the_chain_does_not_cover_has_no_fingerprint()
    {

        GrimoireSchemaVersionChain chain = GrimoireSchemaVersionChains.Default
            .ForTier(GrimoireSchemaTransactionTier.Core);

        Assert.Null(chain.SourceDefinitionFingerprintFor(2));

    }

    [Fact]
    public void A_two_version_chain_answers_for_both_versions()
    {

        GrimoireSchemaVersionChain chain = GrimoireSchemaEvolutionFixture.TwoVersionChain();

        Assert.Equal(2, chain.HeadVersion);

        Assert.Equal(
            GrimoireSchemaEvolutionFixture.VersionOneFingerprint,
            chain.SourceDefinitionFingerprintFor(1));

        Assert.Equal(chain.HeadManifest.SourceDefinitionFingerprint, chain.SourceDefinitionFingerprintFor(2));

        Assert.True(chain.TryGetStep(1, out GrimoireSchemaVersionStep step));

        Assert.Equal(2, step.ToVersion);

    }

    [Fact]
    public void A_step_that_skips_a_version_is_refused()
    {

        // One step for head version 2, so the step-count check passes and the refusal is genuinely
        // about the step's own arithmetic rather than about how many steps the chain declares.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaEvolutionFixture.ChainWithSteps(
                headVersion: 2,
                GrimoireSchemaEvolutionFixture.Step(fromVersion: 1, toVersion: 3)));

        Assert.Contains("consecutive", error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void A_gap_between_steps_is_refused()
    {

        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaEvolutionFixture.ChainWithSteps(
                headVersion: 4,
                GrimoireSchemaEvolutionFixture.Step(1, 2),
                GrimoireSchemaEvolutionFixture.Step(3, 4)));

    }

    [Fact]
    public void A_step_count_that_does_not_match_the_head_version_is_refused()
    {

        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaEvolutionFixture.ChainWithSteps(
                headVersion: 3,
                GrimoireSchemaEvolutionFixture.Step(1, 2)));

    }

    [Fact]
    public void A_step_with_no_statement_is_refused()
    {

        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaEvolutionFixture.ChainWithSteps(
                headVersion: 2,
                GrimoireSchemaEvolutionFixture.Step(1, 2, statements: [])));

    }

    [Fact]
    public void A_step_pinning_a_malformed_fingerprint_is_refused()
    {

        GrimoireSchemaVersionStep step = GrimoireSchemaEvolutionFixture.Step(1, 2) with
        {

            FromSourceDefinitionFingerprint = "too-short",

        };

        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaEvolutionFixture.ChainWithSteps(headVersion: 2, step));

    }

    /// <summary>
    /// The journal identifies a pending sweep by name, so two steps sharing one name are
    /// indistinguishable after a restart.
    /// </summary>
    [Fact]
    public void Two_steps_naming_one_backfill_are_refused()
    {

        TestBackfill shared = new("shared");

        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaEvolutionFixture.ChainWithSteps(
                headVersion: 3,
                GrimoireSchemaEvolutionFixture.Step(1, 2, backfill: shared),
                GrimoireSchemaEvolutionFixture.Step(2, 3, backfill: shared)));

    }

    [Fact]
    public void A_step_belonging_to_another_tier_is_refused()
    {

        GrimoireSchemaVersionStep step = GrimoireSchemaEvolutionFixture.Step(1, 2) with
        {

            TransactionTier = GrimoireSchemaTransactionTier.CovenantCanonical,

        };

        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaEvolutionFixture.ChainWithSteps(headVersion: 2, step));

    }

    [Fact]
    public void A_chain_set_missing_a_tier_is_refused()
    {

        _ = Assert.Throws<InvalidOperationException>(
            () => new GrimoireSchemaVersionChainSet(
            [
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core),
            ]));

    }

    [Fact]
    public void A_chain_set_declaring_one_tier_twice_is_refused()
    {

        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default
            .ForTier(GrimoireSchemaTransactionTier.Core);

        _ = Assert.Throws<InvalidOperationException>(
            () => new GrimoireSchemaVersionChainSet([core, core]));

    }

}
