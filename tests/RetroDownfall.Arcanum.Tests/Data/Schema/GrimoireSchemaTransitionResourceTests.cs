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
    /// The shipped state, asserted positively rather than left unstated: which tiers have left version 1,
    /// and that every statement the loader found belongs to one of their steps.
    /// </summary>
    /// <remarks>
    /// A statement file that landed in the wrong <c>V&lt;n&gt;</c> folder, or under the wrong tier, is
    /// silently absorbed into whichever step the folder names. It would install against a database it
    /// was never written for, so what the loader found is pinned here rather than counted.
    ///
    /// <para>The order is the install order, so this pins sequencing as well as membership: an index
    /// declared before the table it constrains, or a trigger before the table it fires on, would reach
    /// SQLite as a statement against an object that does not exist yet.</para>
    /// </remarks>
    [Fact]
    public void The_shipped_catalog_declares_only_the_steps_its_tiers_have_taken()
    {

        Assert.All(
            GrimoireSchemaCatalog.TransitionStatements,
            static statement =>
            {

                Assert.Contains(
                    (statement.TransactionTier, statement.ToVersion),
                    ((GrimoireSchemaTransactionTier Tier, int ToVersion)[])
                    [
                        (GrimoireSchemaTransactionTier.Core, 2),
                        (GrimoireSchemaTransactionTier.Core, 3),
                        (GrimoireSchemaTransactionTier.Core, 4),
                        (GrimoireSchemaTransactionTier.Core, 5),
                        (GrimoireSchemaTransactionTier.Core, 6),
                        (GrimoireSchemaTransactionTier.CovenantCanonical, 2),
                        (GrimoireSchemaTransactionTier.CovenantCanonical, 3),
                    ]);

            });

        Assert.Equal(
            [
                "saga_memories_scope_kind",
                "saga_memories_campaign_id",
                "saga_memories_scope_index",
                "lexicon_entries_scope",
                "lexicon_entries_retire_name_index",
                "lexicon_entries_scope_index",
                "annal_claims",
                "annal_claims_subject_index",
                "annal_claims_store_candidate_index",
                "annal_versions",
                "annal_versions_version_index",
                "annal_versions_sequence_candidate_index",
                "annal_versions_claim_revision_index",
                "annal_versions_head_candidate_index",
                "annal_versions_claim_recorded_index",
                "annal_versions_predecessor_index",
                "annal_heads",
                "annal_heads_current_version_index",
                "annal_heads_store_index",
                "annal_dependencies",
                "annal_dependencies_dependent_ordinal_index",
                "annal_dependencies_dependency_index",
                "annal_claims_guard_update",
                "annal_versions_guard_update",
                "annal_dependencies_guard_update",
                "annal_heads_validate_update",
                "saga_memories_retired_at",
                "saga_memories_pinned_at",
                "saga_retirement_suppressions",
                "saga_retirement_suppressions_campaign_index",
                "saga_suppression_key",
                "Sessions_Id_guard_identity_insert",
                "Sessions_Id_guard_identity_update",
                "Sessions_CampaignId_guard_identity_insert",
                "Sessions_CampaignId_guard_identity_update",
                "Campaigns_Id_guard_identity_insert",
                "Campaigns_Id_guard_identity_update",
                "Entries_Id_guard_identity_insert",
                "Entries_Id_guard_identity_update",
                "Entries_SessionId_guard_identity_insert",
                "Entries_SessionId_guard_identity_update",
                "entry_embeddings_EntryId_guard_identity_insert",
                "entry_embeddings_EntryId_guard_identity_update",
                "assistant_entry_finalizations_AssistantEntryId_guard_identity_insert",
                "assistant_entry_finalizations_SessionId_guard_identity_insert",
                "session_sensitivity_state_SessionId_guard_identity_insert",
                "session_sensitivity_state_SessionId_guard_identity_update",
                "SessionAttachments_Id_guard_identity_insert",
                "SessionAttachments_Id_guard_identity_update",
                "SessionAttachments_SessionId_guard_identity_insert",
                "SessionAttachments_SessionId_guard_identity_update",
                "SessionAttachments_EntryId_guard_identity_insert",
                "SessionAttachments_EntryId_guard_identity_update",
                "session_attachment_chunks_AttachmentId_guard_identity_insert",
                "session_attachment_chunks_AttachmentId_guard_identity_update",
                "session_attachment_index_state_AttachmentId_guard_identity_insert",
                "session_attachment_index_state_AttachmentId_guard_identity_update",
                "attachment_memory_consultations_AttachmentId_guard_identity_insert",
                "attachment_memory_consultations_AttachmentId_guard_identity_update",
                "saga_memory_attachment_provenance_AttachmentId_guard_identity_insert",
                "saga_memory_attachment_provenance_AttachmentId_guard_identity_update",
                "lexicon_fact_attachment_provenance_AttachmentId_guard_identity_insert",
                "lexicon_fact_attachment_provenance_AttachmentId_guard_identity_update",
                "artifact_sensitivity_SessionId_guard_identity_insert",
                "session_campaign_bindings_SessionId_guard_identity_insert",
                "session_campaign_bindings_guard_update",
                "session_campaign_bindings_CampaignId_guard_identity_insert",
                "session_campaign_bindings_CampaignId_guard_identity_update",
                "saga_memories_CampaignId_guard_identity_insert",
                "saga_memories_CampaignId_guard_identity_update",
                "saga_retirement_suppressions_CampaignId_guard_identity_insert",
                "saga_retirement_suppressions_CampaignId_guard_identity_update",
                "LongRunningOperations_SessionId_spelling",
                "LongRunningOperations_RunId_spelling",
                "LongRunningOperations_InferenceRunId_spelling",
                "Entries_SessionId_norm_index",
                "entry_embeddings_EntryId_norm_index",
                "SessionAttachments_SessionId_norm_index",
                "SessionAttachments_Id_norm_index",
                "covenant_curation_versions",
                "covenant_curation_versions_head_candidate_index",
                "covenant_curation_versions_global_revision_index",
                "covenant_curation_versions_campaign_revision_index",
                "covenant_curation_versions_mutation_index",
                "covenant_curation_versions_campaign_cleanup_index",
                "covenant_curation_heads",
                "covenant_curation_heads_global_subject_index",
                "covenant_curation_heads_campaign_subject_index",
                "covenant_curation_heads_current_version_index",
                "covenant_curation_heads_campaign_masks_index",
                "covenant_curation_receipts",
                "covenant_curation_receipts_campaign_cleanup_index",
                "covenant_curation_receipts_resulting_version_index",
                "covenant_curation_versions_guard_delete",
                "covenant_curation_versions_guard_update",
                "covenant_curation_receipts_guard_delete",
                "covenant_curation_receipts_guard_update",
                "covenant_versions_defer_foreign_keys",
                "covenant_heads_staging",
                "covenant_version_attachment_provenance_staging",
                "covenant_heads_drop",
                "covenant_version_attachment_provenance_drop",
                "covenant_versions_replacement",
                "covenant_versions_copy",
                "covenant_versions_drop",
                "covenant_versions_rename",
                "covenant_versions_entry_lane_revision_index",
                "covenant_versions_head_candidate_index",
                "covenant_versions_mutation_index",
                "covenant_versions_entry_created_index",
                "covenant_versions_source_turn_index",
                "covenant_versions_guard_delete",
                "covenant_versions_guard_update",
                "covenant_heads",
                "covenant_heads_copy",
                "covenant_heads_staging_drop",
                "covenant_heads_search_row_index",
                "covenant_heads_current_version_index",
                "covenant_heads_global_active_index",
                "covenant_heads_campaign_active_index",
                "covenant_heads_campaign_cleanup_index",
                "covenant_heads_key_epoch_delete",
                "covenant_heads_key_epoch_insert",
                "covenant_heads_key_epoch_update",
                "covenant_heads_validate_insert",
                "covenant_heads_validate_update",
                "covenant_version_attachment_provenance",
                "covenant_version_attachment_provenance_copy",
                "covenant_version_attachment_provenance_staging_drop",
                "covenant_version_attachment_provenance_attachment_index",
                "covenant_version_attachment_provenance_guard_delete",
                "covenant_version_attachment_provenance_guard_update",
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

        // Both Covenant tiers still publish the raw computation: neither is leaving its version in
        // this change, and a tier's fingerprint may only switch in the change that raises its version.
        Assert.Equal(
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint,
            GrimoireSchemaCatalog.ComputeRawSourceFingerprint(GrimoireSchemaCatalog.CovenantCanonicalObjects));

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantAcceleratorSchemaFingerprint,
            GrimoireSchemaCatalog.ComputeRawSourceFingerprint(GrimoireSchemaCatalog.CovenantAcceleratorObjects));

    }

}
