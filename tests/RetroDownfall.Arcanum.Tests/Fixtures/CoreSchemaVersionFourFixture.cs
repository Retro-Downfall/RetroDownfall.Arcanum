using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// The Core head tree as it stood at schema version 4, reconstructed so an upgrade can be driven from a
/// real version-4 installation rather than from a hand-written metadata row.
/// </summary>
/// <remarks>
/// Version 5 both <i>adds</i> and <i>edits</i>, so the reconstruction does both: the objects listed
/// below come out of the shipped list, and <c>session_campaign_bindings_guard_update</c>'s frozen
/// version-4 text is substituted for the shipped one - exactly as
/// <see cref="CoreSchemaVersionThreeFixture"/> does for <c>saga_memories</c>. That is what keeps it
/// honest: the fingerprint this list produces is compared against the pin the shipped chain carries for
/// version 5, so a reconstruction that drifted fails rather than quietly certifying the wrong pin.
///
/// <para>Every guard trigger version 5 adds belongs in the name list below, and every further Core
/// object whose shipped text has moved since version 4 needs its version-4 text frozen here beside the
/// ones that already are, or the reconstruction stops describing version 4 and the pin assertion says
/// so. A version-5 statement is the usual reason the text moves and it is not the only one: the
/// fingerprint is taken over the file rather than over the SQL it holds, so a corrected comment moves
/// it too.</para>
///
/// <para>It peels back from <see cref="CoreSchemaVersionFiveFixture"/> rather than from the shipped
/// catalog, so each fixture undoes exactly one version and no fixture has to restate what a later one
/// already froze. Version 6's edits reach this reconstruction through that fixture.</para>
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
        "session_campaign_bindings_CampaignId_guard_identity_insert",
        "session_campaign_bindings_CampaignId_guard_identity_update",
        "saga_memories_CampaignId_guard_identity_insert",
        "saga_memories_CampaignId_guard_identity_update",
        "saga_retirement_suppressions_CampaignId_guard_identity_insert",
        "saga_retirement_suppressions_CampaignId_guard_identity_update",
    ];

    /// <summary>
    /// <c>session_campaign_bindings_guard_update</c> before version 5 gave it the CampaignId
    /// canonicalization exemption.
    /// </summary>
    /// <remarks>
    /// The one Core object version 5 <i>edits</i> rather than adds, so removal alone no longer
    /// reconstructs version 4 and this text has to be frozen exactly as the remarks above require. The
    /// version-4 guard aborts any update to a binding whose kind is not 3, which is every Campaign
    /// binding there is, so the version-5 sweep could not have repaired one row of
    /// <c>session_campaign_bindings.CampaignId</c> without this replacement.
    /// </remarks>
    private const string SessionCampaignBindingsGuardUpdateSql =
        """
        -- The binding is written once and read as authority forever after. Exactly one update exists: the
        -- authenticated one-time resolution that turns an unresolved legacy row into a final one. Everything
        -- else is rejected outright, because an editable binding would let a Session be moved into another
        -- Campaign's context, or laundered into Global context, without leaving the receipt that makes such
        -- a move reviewable.
        CREATE TRIGGER IF NOT EXISTS session_campaign_bindings_guard_update
        BEFORE UPDATE ON session_campaign_bindings
        BEGIN
            SELECT RAISE(ABORT, 'A Session Campaign binding resolution requires the Session binding write scope.')
            WHERE arcanum_session_binding_write_authorized() = 0;

            SELECT RAISE(ABORT, 'A Session Campaign binding cannot change the Session it belongs to.')
            WHERE NEW.SessionId <> OLD.SessionId;

            SELECT RAISE(ABORT, 'Only an unresolved legacy Session Campaign binding can be resolved.')
            WHERE OLD.BindingKindCode <> 3;

            SELECT RAISE(ABORT, 'A resolved Session Campaign binding must be final.')
            WHERE NEW.BindingKindCode NOT IN (1, 2);
        END;

        """;

    /// <summary><c>saga_retirement_suppressions</c> as version 4 declared it.</summary>
    /// <remarks>
    /// Version 5 does not touch this object; its shipped text moved afterwards, because comments in it
    /// were corrected. A corrected comment is still a changed file, and the fingerprint reads the file.
    /// </remarks>
    private const string SagaRetirementSuppressionsSql =
        """
        -- Content-free, keyed evidence that one memory was retired, kept so the next extraction pass cannot
        -- re-add what the operator just removed. It deliberately names no memory: the row has to outlive the
        -- row it describes, because an operator who retires a memory and then deletes it must not thereby
        -- re-enable the extraction they rejected.
        --
        -- The digest is an HMAC rather than a bare hash for two reasons, both narrow and both stated in full.
        -- annal_versions.ContentHash is already a bare SHA-256 of the same bytes, so an unkeyed digest here
        -- would be that identical value and the two tables would join into one confirmation oracle rather
        -- than none. And deleting the single saga_suppression_key row makes every digest here permanently
        -- useless for confirming a guess about content that has since been erased, which one row cannot do
        -- for an unkeyed hash.
        --
        -- The scope columns restate what the digest was computed over. That is not a second measurement of
        -- the digest: it is the only way a Campaign deletion can find its own suppressions, and a suppression
        -- that outlived the Campaign identity it applied to would suppress extraction for an owner that no
        -- longer exists with nothing left to remove it.
        CREATE TABLE IF NOT EXISTS saga_retirement_suppressions (
            SuppressionDigest BLOB NOT NULL PRIMARY KEY CHECK (length(SuppressionDigest) = 32),
            ScopeKindCode INTEGER NOT NULL CHECK (ScopeKindCode IN (0, 1, 2, 3)),
            CampaignId TEXT NULL,
            RetiredAtUtc TEXT NOT NULL,
            CHECK ((ScopeKindCode = 2 AND CampaignId IS NOT NULL) OR (ScopeKindCode <> 2 AND CampaignId IS NULL))
        );

        -- Campaign cleanup reads this to remove a deleted Campaign's suppressions in the transaction that
        -- removes the Campaign.
        CREATE INDEX IF NOT EXISTS idx_saga_retirement_suppressions_campaign
        ON saga_retirement_suppressions(ScopeKindCode, CampaignId);

        """;

    /// <summary>Every Core object as version 4 declared it.</summary>
    /// <remarks>
    /// Line endings are normalized because the frozen text above is a C# literal and the catalog's text
    /// is an embedded file. A checkout that handed one of them CRLF would move the fingerprint without
    /// changing a single character of SQL.
    /// </remarks>
    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. CoreSchemaVersionFiveFixture.Objects
            .Where(static definition => !VersionFiveObjectNames.Contains(definition.Name, StringComparer.Ordinal))
            .Select(static definition => definition.Name switch
            {

                "session_campaign_bindings_guard_update" => definition with
                {
                    Sql = SessionCampaignBindingsGuardUpdateSql.ReplaceLineEndings("\n"),
                },

                "saga_retirement_suppressions" => definition with
                {
                    Sql = SagaRetirementSuppressionsSql.ReplaceLineEndings("\n"),
                },

                _ => definition,

            }),
    ];

    /// <summary>The fingerprint the version-4 tree published, computed from the reconstruction above.</summary>
    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeRawSourceFingerprint(Objects);

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
