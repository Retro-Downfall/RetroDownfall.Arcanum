using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// The Core head tree as it stood at schema version 4, reconstructed so an upgrade can be driven from a
/// real version-4 installation rather than from a hand-written metadata row.
/// </summary>
/// <remarks>
/// Version 5 only <i>adds</i>, so the reconstruction only removes: the objects listed below come out of
/// the shipped list and nothing else changes. That is what keeps it honest - the fingerprint this list
/// produces is compared against the pin the shipped chain carries for version 5, so a reconstruction
/// that drifted fails rather than quietly certifying the wrong pin.
///
/// <para>A later version-5 statement that <i>edits</i> an existing Core object would have to freeze that
/// object's version-4 text here as well, exactly as <see cref="CoreSchemaVersionThreeFixture"/> freezes
/// <c>saga_memories</c>, or the reconstruction stops describing version 4 and the pin assertion says so.
/// Every guard trigger version 5 goes on to add belongs in the list below for the same reason.</para>
/// </remarks>
internal static class CoreSchemaVersionFourFixture
{

    /// <summary>The objects version 5 introduced.</summary>
    internal static readonly string[] VersionFiveObjectNames =
    [
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
    ];

    /// <summary>Every Core object as version 4 declared it.</summary>
    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects
            .Where(static definition => !VersionFiveObjectNames.Contains(definition.Name, StringComparer.Ordinal)),
    ];

    /// <summary>The fingerprint the version-4 tree published, computed from the reconstruction above.</summary>
    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    /// <summary>
    /// An installable version-4 chain set: the reconstructed Core tree at version 4 with the three steps
    /// that reach it, and the shipped chains for both Covenant tiers.
    /// </summary>
    /// <remarks>
    /// Installing this and then handing the same installer <see cref="GrimoireSchemaVersionChains.Default"/>
    /// is the whole of an upgrade as a caller reaches it. Nothing writes a metadata row or a journal row
    /// to describe the older installation, so no assertion rests on a state a test invented.
    ///
    /// <para>The chain carries the three shipped steps to version 4, because a chain needs exactly one
    /// step per version above 1 and those are the steps version 4 actually shipped with. A fresh install
    /// runs none of them, so they are here to make this a faithful statement of what version 4 was rather
    /// than to be executed.</para>
    /// </remarks>
    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 4,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps
                        .Where(static step => step.ToVersion <= 4),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

}
