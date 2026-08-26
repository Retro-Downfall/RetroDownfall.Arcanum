using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Transition resources are decoded from the same embedded tree as head objects, are never installed
/// as head objects, and are excluded from every source-definition fingerprint.
/// </summary>
public sealed class GrimoireSchemaTransitionResourceTests
{

    [Fact]
    public void TryParse_decodes_a_core_transition_path()
    {

        Assert.True(GrimoireSchemaCatalog.TryParseTransitionResourcePath(
            "Transitions.V2.010_add_entries_campaign_id",
            out GrimoireSchemaTransitionResourcePath? path));

        Assert.NotNull(path);

        Assert.Equal(GrimoireSchemaFamily.Core, path.Family);

        Assert.Equal(GrimoireSchemaTransactionTier.Core, path.TransactionTier);

        Assert.Equal(2, path.ToVersion);

        Assert.Equal(10, path.Ordinal);

        Assert.Equal("add_entries_campaign_id", path.Name);

    }

    [Fact]
    public void TryParse_decodes_a_capability_transition_path()
    {

        Assert.True(GrimoireSchemaCatalog.TryParseTransitionResourcePath(
            "Capabilities.Covenant.Canonical.Transitions.V3.020_widen_validity",
            out GrimoireSchemaTransitionResourcePath? path));

        Assert.NotNull(path);

        Assert.Equal(GrimoireSchemaFamily.Covenant, path.Family);

        Assert.Equal(GrimoireSchemaTransactionTier.CovenantCanonical, path.TransactionTier);

        Assert.Equal(3, path.ToVersion);

        Assert.Equal(20, path.Ordinal);

        Assert.Equal("widen_validity", path.Name);

    }

    [Theory]
    [InlineData("Tables.grimoire_feature_schemas")]
    [InlineData("Capabilities.Covenant.Canonical.Tables.covenant_entries")]
    public void TryParse_reports_an_object_path_as_not_a_transition(string relative)
    {

        Assert.False(GrimoireSchemaCatalog.TryParseTransitionResourcePath(
            relative,
            out GrimoireSchemaTransitionResourcePath? path));

        Assert.Null(path);

    }

    /// <summary>
    /// A path that is under a transitions folder and malformed throws rather than declining, because
    /// declining would hand it to the object parser and produce a failure naming the wrong mistake.
    /// </summary>
    [Theory]
    [InlineData("Transitions.V1.010_impossible")]
    [InlineData("Transitions.V0.010_impossible")]
    [InlineData("Transitions.Two.010_not_a_version")]
    [InlineData("Transitions.V2.add_without_ordinal")]
    [InlineData("Transitions.V2.010_")]
    [InlineData("Capabilities.Covenant.Canonical.Transitions.V1.010_impossible")]
    public void TryParse_throws_on_a_malformed_transition_path(string relative)
    {

        _ = Assert.Throws<InvalidOperationException>(
            () =>
            {

                _ = GrimoireSchemaCatalog.TryParseTransitionResourcePath(relative, out _);

            });

    }

    /// <summary>
    /// The shipped state, asserted positively rather than left unstated: exactly one tier has left
    /// version 1, and every statement the loader found belongs to that tier's one step.
    /// </summary>
    /// <remarks>
    /// A statement file that landed in the wrong <c>V&lt;n&gt;</c> folder, or under the wrong tier, is
    /// silently absorbed into whichever step the folder names. It would install against a database it
    /// was never written for, so what the loader found is pinned here rather than counted.
    /// </remarks>
    [Fact]
    public void The_shipped_catalog_declares_only_the_core_version_two_step()
    {

        Assert.All(
            GrimoireSchemaCatalog.TransitionStatements,
            static statement =>
            {

                Assert.Equal(GrimoireSchemaTransactionTier.Core, statement.TransactionTier);

                Assert.Equal(2, statement.ToVersion);

            });

        Assert.Equal(
            [
                "saga_memories_scope_kind",
                "saga_memories_campaign_id",
                "saga_memories_scope_index",
                "lexicon_entries_scope",
                "lexicon_entries_retire_name_index",
                "lexicon_entries_scope_index",
            ],
            GrimoireSchemaCatalog.TransitionStatements.Select(static statement => statement.Name));

    }

    [Fact]
    public void No_head_object_is_loaded_from_a_transitions_folder()
    {

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.AllObjects)
        {

            Assert.False(
                definition.ResourcePath.Contains(".Transitions.", StringComparison.Ordinal)
                    || definition.ResourcePath.StartsWith("Transitions.", StringComparison.Ordinal),
                $"{definition.ResourcePath} was loaded as a head object from a transitions folder");

        }

    }

    /// <summary>
    /// Every published source fingerprint is computed over head objects alone.
    /// </summary>
    /// <remarks>
    /// This is load-bearing rather than tidy. A transition resource that entered a tier's fingerprint
    /// would change the value recorded for the version it upgrades <i>from</i>, so authoring the very
    /// step that leaves version 1 would make every installation at version 1 refuse with
    /// <c>SourceDefinitionMismatch</c> before that step could run. The feature would break itself on
    /// its first use.
    /// </remarks>
    [Fact]
    public void A_published_fingerprint_covers_head_objects_alone()
    {

        Assert.Equal(
            GrimoireSchemaCatalog.CoreSchemaFingerprint,
            GrimoireSchemaCatalog.ComputeSourceFingerprint(GrimoireSchemaCatalog.CoreObjects));

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint,
            GrimoireSchemaCatalog.ComputeSourceFingerprint(GrimoireSchemaCatalog.CovenantCanonicalObjects));

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantAcceleratorSchemaFingerprint,
            GrimoireSchemaCatalog.ComputeSourceFingerprint(GrimoireSchemaCatalog.CovenantAcceleratorObjects));

    }

}
