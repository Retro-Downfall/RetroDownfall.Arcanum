using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The shared database-artifact erasure kernel, against a real SQLCipher catalog.
/// </summary>
/// <remarks>
/// Every assertion here is about what the kernel refuses. Deleting the right rows is the easy half;
/// the half that matters is that a moved owner, a stale generation, a lease that does not cover the
/// artifact, or an authority that has since been revoked all stop before any statement runs (§10.17).
/// </remarks>
public sealed class CovenantProtectedArtifactErasureKernelTests
{
    private static readonly Guid SessionId = Guid.Parse("0A1B2C3D-4E5F-4A6B-8C9D-0E1F2A3B4C5D");

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_staged_protected_purge_keeps_Campaign_tombstones_and_counts_dependent_labels_once()
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid campaignId = Guid.NewGuid();

        Guid contribution = Guid.NewGuid();

        Guid rollup = Guid.NewGuid();

        await fixture.SeedCampaignSummariesAsync(campaignId, contribution, rollup);

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        Result<BackupRestoreProtectedStatePurgeReceipt> purged = await BackupRestoreProtectedStatePurger.PurgeStagedAsync(
            fixture.Connection, transaction, CovenantSqliteConnectionInitializer.Instance, TimeProvider.System, Token);

        Assert.True(purged.IsSuccess, purged.Error.Message);

        await transaction.CommitAsync(Token);

        Assert.Equal(2UL, purged.Value.RemovedArtifacts);

        Assert.Equal(2UL, purged.Value.RemovedLabels);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_artifacts;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_sources;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_state WHERE CurrentArtifactId IS NULL AND Revision = 1 AND RefoldRequired = 1;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_state WHERE CurrentArtifactId IS NULL AND Revision = 1 AND SummarizedThroughSequence = 7 AND RefoldRequired = 1;"));
    }

    [Fact]
    public async Task An_exclusive_authority_erases_the_artifact_its_projections_and_its_label_atomically()
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Guid labelId = await fixture.SeedLabelAsync(artifactId, SensitiveArtifactKind.Saga, SessionId);

        await fixture.SeedSagaAsync(artifactId);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
            Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority
            .ForExclusive(lease, CovenantExclusiveOperation.CovenantFamilyReinitialize)
            .Value;

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.ErasePageAsync(
            fixture.Page(artifactId, labelId, SensitiveArtifactKind.Saga, SessionId),
            authority,
            Token);

        Assert.True(erased.IsSuccess);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.Equal(CovenantErasureBlocker.None, erased.Value.Blocker);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM saga_memories;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM saga_memory_embeddings;"));
    }

    [Theory]
    [InlineData("current")]
    [InlineData("missing")]
    [InlineData("malformed")]
    public async Task An_ordinary_purge_authority_requires_committed_schema_metadata_and_erases_under_the_retention_purge_scope(string metadata)
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Guid labelId = await fixture.SeedLabelAsync(artifactId, SensitiveArtifactKind.Lexicon, sessionId: null);

        // The Saga memory is the subject of the Saga claim below, which shares the entry's identity so a
        // purge that ignored the store code would take it. It exists so that claim describes a row, the
        // way every claim a production writer opens does.
        await fixture.ExecuteAsync(
            $"""
             INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
             VALUES ('{Format(artifactId)}', 'n', 'n', 'Person', '[]', '', '2026-08-16T00:00:00Z');
             INSERT INTO saga_memories (Id, Content, CreatedAt) VALUES ('{artifactId:D}', 'c', '2026-08-16T00:00:00Z');
             INSERT INTO annal_claims (ClaimId, SubjectStoreCode, SubjectId, CreatedAtUtc)
             VALUES ('lexicon-claim', 2, '{artifactId:N}', '2026-08-16T00:00:00Z'),
                    ('saga-claim', 1, '{artifactId:D}', '2026-08-16T00:00:00Z');
             INSERT INTO annal_versions (VersionId, ClaimId, Revision, OperationCode, OriginCode,
                 ScopeKindCode, SensitivityCode, ContentHash, ValidFromUtc, RecordedAtUtc)
             VALUES ('lexicon-version', 'lexicon-claim', 1, 1, 1, 1, 0, zeroblob(32), '2026-08-16T00:00:00Z', '2026-08-16T00:00:00Z'),
                    ('saga-version', 'saga-claim', 1, 1, 1, 1, 0, zeroblob(32), '2026-08-16T00:00:00Z', '2026-08-16T00:00:00Z');
             INSERT INTO annal_heads (ClaimId, SubjectStoreCode, CurrentVersionId, CurrentRevision, CurrentOperationCode, UpdatedAtUtc)
             VALUES ('lexicon-claim', 2, 'lexicon-version', 1, 1, '2026-08-16T00:00:00Z'),
                    ('saga-claim', 1, 'saga-version', 1, 1, '2026-08-16T00:00:00Z');
             INSERT INTO lexicon_annal_fact_provenance (AnnalVersionId, FactOrdinal, SessionId, AttachmentId,
                 LogicalKey, AttachmentVersion, AttachmentContentHash, MaterializedAt, SourceType)
             VALUES ('lexicon-version', 0, 'session', 'attachment', 'source', 1, 'attachment-digest', '2026-08-16T00:00:00Z', 'text');
             """);

        if (metadata != "current")
        {
            await fixture.ExecuteAsync(metadata == "missing"
                ? "DELETE FROM grimoire_feature_schemas WHERE FamilyCode = 0 AND TransactionTierCode = 0;"
                : "UPDATE grimoire_feature_schemas SET SchemaVersion = 11.5 WHERE FamilyCode = 0 AND TransactionTierCode = 0;");
        }

        FakeCovenantAuthorityProvider provider = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: provider);

        await using CovenantWriteLease lease = (await gate.AcquireWriteAsync(
            CovenantOperationScope.Global,
            Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority.ForOrdinary(
            lease,
            CovenantErasureAuthorityFixture.OperatorContext(provider),
            CovenantErasureAuthorityFixture.Issuer(provider)).Value;

        if (metadata != "current")
        {
            _ = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Kernel.ErasePageAsync(
                fixture.Page(artifactId, labelId, SensitiveArtifactKind.Lexicon, sessionId: null), authority, Token).AsTask());

            Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

            Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM lexicon_entries;"));

            Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM lexicon_annal_fact_provenance;"));

            Assert.Equal(2, await fixture.CountAsync("SELECT COUNT(*) FROM annal_versions;"));

            return;
        }

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.ErasePageAsync(
            fixture.Page(artifactId, labelId, SensitiveArtifactKind.Lexicon, sessionId: null),
            authority,
            Token);

        Assert.True(erased.IsSuccess);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM lexicon_entries;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM lexicon_annal_fact_provenance;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM annal_claims WHERE SubjectStoreCode = 2;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM annal_claims WHERE SubjectStoreCode = 1;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM annal_heads;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM annal_versions;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM saga_memories;"));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(fixture.Connection);
    }

    [Fact]
    public async Task An_owner_outside_the_lease_scope_is_rejected_before_any_row_is_touched()
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Guid labelId = await fixture.SeedLabelAsync(
            artifactId,
            SensitiveArtifactKind.Lexicon,
            sessionId: null,
            campaignId: CovenantOperationGateFixture.CampaignOne);

        FakeCovenantAuthorityProvider provider = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: provider);

        await using CovenantWriteLease lease = (await gate.AcquireWriteAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignTwo),
            Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority.ForOrdinary(
            lease,
            CovenantErasureAuthorityFixture.OperatorContext(provider),
            CovenantErasureAuthorityFixture.Issuer(provider)).Value;

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.ErasePageAsync(
            fixture.Page(
                artifactId,
                labelId,
                SensitiveArtifactKind.Lexicon,
                sessionId: null,
                campaignId: CovenantOperationGateFixture.CampaignOne),
            authority,
            Token);

        Assert.True(erased.IsSuccess);

        Assert.Equal(CovenantErasureBlocker.ManualOwnershipMismatch, erased.Value.Blocker);

        Assert.Equal(0UL, erased.Value.ErasedCount);

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));
    }

    [Fact]
    public async Task A_revoked_operator_authority_stops_the_page_before_its_first_transaction()
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Guid labelId = await fixture.SeedLabelAsync(artifactId, SensitiveArtifactKind.Lexicon, sessionId: null);

        FakeCovenantAuthorityProvider provider = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: provider);

        await using CovenantWriteLease lease = (await gate.AcquireWriteAsync(
            CovenantOperationScope.Global,
            Token)).Value;

        RevocableOperatorAuthorityIssuer issuer = new(provider) { RevalidationFails = true };

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority.ForOrdinary(
            lease,
            CovenantErasureAuthorityFixture.OperatorContext(provider),
            issuer).Value;

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.ErasePageAsync(
            fixture.Page(artifactId, labelId, SensitiveArtifactKind.Lexicon, sessionId: null),
            authority,
            Token);

        Assert.True(erased.IsSuccess);

        Assert.Equal(CovenantErasureBlocker.AuthorityStale, erased.Value.Blocker);

        Assert.Equal(0UL, erased.Value.ExaminedCount);

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));
    }

    [Fact]
    public async Task A_page_computed_against_a_replaced_dataset_generation_is_refused()
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Guid labelId = await fixture.SeedLabelAsync(artifactId, SensitiveArtifactKind.Lexicon, sessionId: null);

        FakeCovenantAuthorityProvider provider = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: provider);

        await using CovenantWriteLease lease = (await gate.AcquireWriteAsync(
            CovenantOperationScope.Global,
            Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority.ForOrdinary(
            lease,
            CovenantErasureAuthorityFixture.OperatorContext(provider),
            CovenantErasureAuthorityFixture.Issuer(provider)).Value;

        CovenantProtectedArtifactErasurePage stale = new(
            Guid.Parse("99999999-9999-4999-8999-999999999999"),
            [CovenantErasureAuthorityFixture.Item(artifactId, labelId, SensitiveArtifactKind.Lexicon)]);

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.ErasePageAsync(
            stale,
            authority,
            Token);

        Assert.True(erased.IsSuccess);

        Assert.Equal(CovenantErasureBlocker.IntegrityFailure, erased.Value.Blocker);

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));
    }

    /// <summary>
    /// A statement the schema refuses inside the purge is durable state disagreeing with itself.
    /// </summary>
    /// <remarks>
    /// A guard trigger's abort surfaces as <c>SQLITE_CONSTRAINT</c>, which is the database saying the
    /// delete would break a rule it enforces. That is an integrity failure an operator has to look at,
    /// and the whole transaction rolls back with the artifact and its label left in place.
    /// </remarks>
    [Fact]
    public async Task A_constraint_refusal_inside_the_purge_is_an_integrity_failure_and_deletes_nothing()
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Guid labelId = await fixture.SeedLabelAsync(artifactId, SensitiveArtifactKind.Saga, SessionId);

        await fixture.SeedSagaAsync(artifactId);

        await fixture.ExecuteAsync(
            """
            CREATE TRIGGER saga_memories_refuse_delete BEFORE DELETE ON saga_memories
            BEGIN
                SELECT RAISE(ABORT, 'refused');
            END;
            """);

        Result<CovenantArtifactErasureProgress> erased = await EraseUnderExclusiveAsync(
            fixture,
            fixture.Page(artifactId, labelId, SensitiveArtifactKind.Saga, SessionId));

        Assert.True(erased.IsSuccess);

        Assert.Equal(CovenantErasureBlocker.IntegrityFailure, erased.Value.Blocker);

        Assert.Equal(0UL, erased.Value.ErasedCount);

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM saga_memories;"));
    }

    /// <summary>
    /// A table the plan declares, missing from the installed schema, is an integrity failure.
    /// </summary>
    /// <remarks>
    /// A missing table (here the Tapestry tables an Entry purge reaches) is <c>SQLITE_ERROR</c>. The plan
    /// runner never skips a declared target, so the schema the purge was written against and the schema
    /// on disk disagree: that is the integrity failure an operator has to see, not a transient condition
    /// a retry clears. It is still a blocker, never a success: the transaction rolls back, and the Entry
    /// and its label stay.
    /// </remarks>
    [Fact]
    public async Task A_missing_declared_table_inside_the_purge_is_an_integrity_failure_and_deletes_nothing()
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Guid labelId = await fixture.SeedLabelAsync(
            artifactId,
            SensitiveArtifactKind.AssistantEntry,
            SessionId);

        await fixture.SeedCommittedAssistantEntryAsync(artifactId);

        await fixture.ExecuteAsync(
            """
            DROP TABLE tapestry_node_embeddings;
            DROP TABLE tapestry_nodes;
            DROP TABLE tapestry_generations;
            """);

        Result<CovenantArtifactErasureProgress> erased = await EraseUnderExclusiveAsync(
            fixture,
            fixture.Page(artifactId, labelId, SensitiveArtifactKind.AssistantEntry, SessionId));

        Assert.True(erased.IsSuccess);

        Assert.Equal(CovenantErasureBlocker.IntegrityFailure, erased.Value.Blocker);

        Assert.Equal(0UL, erased.Value.ErasedCount);

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM \"Entries\";"));
    }

    /// <summary>
    /// An environmental storage condition that says nothing about the artifact's data is not reported
    /// as corrupt data.
    /// </summary>
    /// <remarks>
    /// A database that refuses writes answers the purge's <c>BEGIN IMMEDIATE</c> with
    /// <c>SQLITE_READONLY</c>: the engine could not carry out the purge, and durable state does not
    /// disagree with itself. It is the storage-unavailable blocker, still never a success, and the
    /// artifact and its label stay.
    /// </remarks>
    [Fact]
    public async Task A_read_only_database_inside_the_purge_is_storage_unavailable_and_deletes_nothing()
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Guid labelId = await fixture.SeedLabelAsync(artifactId, SensitiveArtifactKind.Saga, SessionId);

        await fixture.SeedSagaAsync(artifactId);

        await fixture.ExecuteAsync("PRAGMA query_only = ON;");

        Result<CovenantArtifactErasureProgress> erased = await EraseUnderExclusiveAsync(
            fixture,
            fixture.Page(artifactId, labelId, SensitiveArtifactKind.Saga, SessionId));

        await fixture.ExecuteAsync("PRAGMA query_only = OFF;");

        Assert.True(erased.IsSuccess);

        Assert.Equal(CovenantErasureBlocker.StorageUnavailable, erased.Value.Blocker);

        Assert.Equal(0UL, erased.Value.ErasedCount);

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM saga_memories;"));
    }

    [Fact]
    public async Task An_artifact_whose_label_is_already_gone_is_counted_without_being_deleted_twice()
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Guid labelId = Guid.NewGuid();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority
            .ForExclusive(lease, CovenantExclusiveOperation.CovenantReset)
            .Value;

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.ErasePageAsync(
            fixture.Page(artifactId, labelId, SensitiveArtifactKind.Lexicon, sessionId: null),
            authority,
            Token);

        Assert.True(erased.IsSuccess);

        Assert.Equal(1UL, erased.Value.ExaminedCount);

        Assert.Equal(0UL, erased.Value.ErasedCount);

        Assert.Equal(CovenantErasureBlocker.None, erased.Value.Blocker);
    }

    /// <summary>
    /// A managed workspace file cannot be smuggled into a database page: the kernel that owns it has
    /// a durable work item, and a page has nowhere to put one.
    /// </summary>
    [Fact]
    public void A_managed_workspace_file_can_never_enter_a_database_erasure_page() =>
        Assert.Throws<ArgumentException>(() =>
            new CovenantProtectedArtifactErasurePage(
                CovenantOperationGateFixture.DatasetGeneration,
                [
                    CovenantErasureAuthorityFixture.Item(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        SensitiveArtifactKind.ManagedWorkspaceFile),
                ]));

    /// <summary>
    /// Erasing a committed assistant entry leaves the receipt its finalization guard is bound to.
    /// </summary>
    /// <remarks>
    /// <b>The statement this covers had no test at all until now, and its failure mode is silence.</b>
    /// The receipt is written by an <c>INSERT … SELECT</c> that finds the guard by
    /// <c>assistant_entry_finalizations.AssistantEntryId</c>. A comparison that matched no guard would
    /// insert no receipt, raise nothing, and let the purge report success — and a purged entry with no
    /// receipt is indistinguishable from one that never existed, so a replay of its turn claim would
    /// return or recreate exactly the response the erasure removed. Every other assertion in this file
    /// is about what the kernel refuses, and none of them would have noticed.
    ///
    /// <para>Asserted on the stored identity rather than on a row count, because the receipt's own key
    /// is the thing a replay looks it up by: a receipt written under some other spelling would satisfy
    /// a count and still leave the replay answering from the erased content.</para>
    /// </remarks>
    [Fact]
    public async Task Erasing_a_committed_assistant_entry_writes_the_receipt_its_guard_is_bound_to()
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Guid labelId = await fixture.SeedLabelAsync(
            artifactId,
            SensitiveArtifactKind.AssistantEntry,
            SessionId);

        await fixture.SeedCommittedAssistantEntryAsync(artifactId);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
            Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority
            .ForExclusive(lease, CovenantExclusiveOperation.CovenantFamilyReinitialize)
            .Value;

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.ErasePageAsync(
            fixture.Page(artifactId, labelId, SensitiveArtifactKind.AssistantEntry, SessionId),
            authority,
            Token);

        Assert.True(erased.IsSuccess, erased.IsFailure ? erased.Error.Message : string.Empty);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM \"Entries\";"));

        Assert.Equal(Format(artifactId), await fixture.ReceiptIdentityAsync());

        // The receipt names the guard it replaces content for, so a receipt carrying some other turn's
        // digest would authorize a refusal for a turn that never finalized.
        Assert.Equal(ErasureFixture.RequestDigest, await fixture.ReceiptGuardDigestAsync());
    }

    /// <summary>
    /// Erasing a Covenant entry takes the Session's hierarchical summary of it with it.
    /// </summary>
    /// <remarks>
    /// A Session tree summarizes every non-empty entry of its Session, so a model-written summary of the
    /// erased entry's words stays retrievable after the purge reported success unless the purge reaches
    /// it. The tree is derived data the next sweep rebuilds from what is left, so the purge removes the
    /// whole Session-kind tree of the owning Session, nodes, embeddings and the vector mirror included,
    /// in the transaction that deletes the entry. Another Session's tree and the same Session's
    /// attachment tree are not derived from its entries and stay.
    /// </remarks>
    [Fact]
    public async Task Entry_purge_stops_the_owning_session_tapestry_generation_being_retrievable()
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Guid otherSessionId = Guid.Parse("1B2C3D4E-5F6A-4B7C-9D0E-1F2A3B4C5D6E");

        Guid labelId = await fixture.SeedLabelAsync(
            artifactId,
            SensitiveArtifactKind.AssistantEntry,
            SessionId);

        await fixture.SeedCommittedAssistantEntryAsync(artifactId);

        await fixture.SeedTapestryAsync("Session", Format(SessionId), "session-generation");

        await fixture.SeedTapestryAsync("SessionAttachment", SessionId.ToString("D"), "attachment-generation");

        await fixture.SeedTapestryAsync("Session", Format(otherSessionId), "other-session-generation");

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
            Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority
            .ForExclusive(lease, CovenantExclusiveOperation.CovenantFamilyReinitialize)
            .Value;

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.ErasePageAsync(
            fixture.Page(artifactId, labelId, SensitiveArtifactKind.AssistantEntry, SessionId),
            authority,
            Token);

        Assert.True(erased.IsSuccess, erased.IsFailure ? erased.Error.Message : string.Empty);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM \"Entries\";"));

        Assert.Equal(
            0,
            await fixture.CountAsync(
                $"SELECT COUNT(*) FROM tapestry_generations WHERE ScopeKind = 'Session' AND ScopeId = '{Format(SessionId)}';"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM tapestry_nodes WHERE GenerationId = 'session-generation';"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM tapestry_node_embeddings WHERE NodeId = 'session-generation-node';"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM tapestry_node_embeddings_vec WHERE NodeId = 'session-generation-node';"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM tapestry_generations WHERE GenerationId = 'attachment-generation';"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM tapestry_generations WHERE GenerationId = 'other-session-generation';"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM tapestry_node_embeddings_vec WHERE NodeId = 'other-session-generation-node';"));
    }

    [Theory]
    [InlineData(SensitiveArtifactKind.CampaignRollup)]
    [InlineData(SensitiveArtifactKind.CampaignContribution)]
    public async Task Campaign_artifact_erasure_removes_owned_sources_and_labels_while_retaining_monotonic_denial_state(SensitiveArtifactKind kind)
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid campaignId = Guid.NewGuid();

        Guid contribution = Guid.NewGuid();

        Guid rollup = Guid.NewGuid();

        await fixture.SeedCampaignSummariesAsync(campaignId, contribution, rollup);

        Guid selected = kind == SensitiveArtifactKind.CampaignRollup ? rollup : contribution;

        Guid? owner = kind == SensitiveArtifactKind.CampaignRollup ? null : SessionId;

        Guid labelId = Guid.Parse((await fixture.LabelIdAsync(selected))!);

        Result<CovenantArtifactErasureProgress> erased = await EraseUnderExclusiveAsync(
            fixture, fixture.Page(selected, labelId, kind, owner, campaignId, revision: 1));

        Assert.True(erased.IsSuccess, erased.IsFailure ? erased.Error.Message : string.Empty);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_artifacts;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_sources;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_state WHERE CurrentArtifactId IS NULL AND Revision = 1 AND SourceGeneration > 0 AND RefoldRequired = 1;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 14;"));

        Assert.Equal(kind == SensitiveArtifactKind.CampaignRollup ? 1 : 0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));

        Assert.Equal(kind == SensitiveArtifactKind.CampaignRollup ? 1 : 0, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 15;"));

        if (kind == SensitiveArtifactKind.CampaignContribution)
        {
            Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_state WHERE CurrentArtifactId IS NULL AND Revision = 1 AND SummarizedThroughSequence = 7 AND SourceGeneration > 0 AND RefoldRequired = 1;"));
        }
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    public async Task A_protected_contribution_purge_counts_and_removes_exact_dependent_label_aliases(string spelling)
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid campaignId = Guid.NewGuid();

        Guid contribution = Guid.NewGuid();

        Guid rollup = Guid.NewGuid();

        await fixture.SeedCampaignSummariesAsync(campaignId, contribution, rollup,
            rollupLabelIdentity: rollup.ToString(spelling).ToLowerInvariant());

        Guid labelId = Guid.Parse((await fixture.LabelIdAsync(contribution))!);

        long plannedClosure = await CampaignSummaryLifecycle.CountArtifactClosureAsync(
            fixture.Connection, null, SensitiveArtifactKind.CampaignContribution, contribution, Token);

        Result<CovenantArtifactErasureProgress> erased = await EraseUnderExclusiveAsync(
            fixture, fixture.Page(contribution, labelId, SensitiveArtifactKind.CampaignContribution,
                SessionId, campaignId, revision: 1));

        Assert.True(erased.IsSuccess, erased.Error.Message);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_artifacts;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));

        Assert.Equal(5L, plannedClosure);
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    public async Task A_native_Entry_purge_removes_exact_Session_summary_label_aliases(string spelling)
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid entry = Guid.NewGuid();

        Guid entryLabel = await fixture.SeedLabelAsync(entry, SensitiveArtifactKind.AssistantEntry, SessionId);

        await fixture.SeedCommittedAssistantEntryAsync(entry);

        await fixture.SeedSessionSummaryAsync(Guid.NewGuid(), spelling);

        Result<CovenantArtifactErasureProgress> erased = await EraseUnderExclusiveAsync(
            fixture, fixture.Page(entry, entryLabel, SensitiveArtifactKind.AssistantEntry, SessionId));

        Assert.True(erased.IsSuccess, erased.Error.Message);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM session_summary_artifacts;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM session_summary_state;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("D")]
    [InlineData("N")]
    public async Task An_entry_purge_cannot_clear_dependent_Campaign_artifacts_outside_its_historical_owner_lease(string spelling)
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid currentCampaign = Guid.NewGuid();

        Guid contribution = Guid.NewGuid();

        Guid rollup = Guid.NewGuid();

        await fixture.SeedCampaignSummariesAsync(currentCampaign, contribution, rollup,
            rollupLabelIdentity: spelling == "canonical" ? null : rollup.ToString(spelling).ToLowerInvariant(),
            contributionLabelIdentity: spelling == "canonical" ? null : contribution.ToString(spelling).ToLowerInvariant());

        Guid historicalCampaign = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        Guid labelId = await fixture.SeedLabelAsync(entryId, SensitiveArtifactKind.AssistantEntry, SessionId, historicalCampaign);

        await fixture.SeedCommittedAssistantEntryAsync(entryId);

        FakeCovenantAuthorityProvider provider = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: provider);

        await using CovenantWriteLease lease = (await gate.AcquireWriteAsync(
            CovenantOperationScope.ForCampaign(historicalCampaign), Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority.ForOrdinary(
            lease, CovenantErasureAuthorityFixture.OperatorContext(provider), CovenantErasureAuthorityFixture.Issuer(provider)).Value;

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.ErasePageAsync(
            fixture.Page(entryId, labelId, SensitiveArtifactKind.AssistantEntry, SessionId, historicalCampaign), authority, Token);

        Assert.True(erased.IsSuccess);

        Assert.Equal(0UL, erased.Value.ErasedCount);

        Assert.NotEqual(CovenantErasureBlocker.None, erased.Value.Blocker);

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM Entries;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_artifacts;"));

        Assert.Equal(3, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    public async Task An_entry_purge_cannot_clear_a_native_Session_summary_with_a_global_historical_label(string spelling)
    {
        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid historicalCampaign = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        Guid labelId = await fixture.SeedLabelAsync(entryId, SensitiveArtifactKind.AssistantEntry, SessionId, historicalCampaign);

        await fixture.SeedCommittedAssistantEntryAsync(entryId);

        await fixture.SeedSessionSummaryAsync(Guid.NewGuid(), spelling, unscopedLabel: true);

        FakeCovenantAuthorityProvider provider = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: provider);

        await using CovenantWriteLease lease = (await gate.AcquireWriteAsync(
            CovenantOperationScope.ForCampaign(historicalCampaign), Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority.ForOrdinary(
            lease, CovenantErasureAuthorityFixture.OperatorContext(provider), CovenantErasureAuthorityFixture.Issuer(provider)).Value;

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.ErasePageAsync(
            fixture.Page(entryId, labelId, SensitiveArtifactKind.AssistantEntry, SessionId, historicalCampaign), authority, Token);

        Assert.True(erased.IsSuccess, erased.Error.Message);

        Assert.Equal(0UL, erased.Value.ErasedCount);

        Assert.NotEqual(CovenantErasureBlocker.None, erased.Value.Blocker);

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM Entries;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM session_summary_artifacts;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM session_summary_state;"));

        Assert.Equal(2, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));
    }

    private static async Task<Result<CovenantArtifactErasureProgress>> EraseUnderExclusiveAsync(
        ErasureFixture fixture,
        CovenantProtectedArtifactErasurePage page)
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
            Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority
            .ForExclusive(lease, CovenantExclusiveOperation.CovenantFamilyReinitialize)
            .Value;

        return await fixture.Kernel.ErasePageAsync(page, authority, Token);
    }

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    private sealed class ErasureFixture : IAsyncDisposable
    {
        private readonly CovenantSchemaScratchDatabase _database;

        private ErasureFixture(CovenantSchemaScratchDatabase database)
        {
            _database = database;

            Kernel = new CovenantProtectedArtifactErasureKernel(
                new FixedCovenantConnectionSource(database.Connection),
                CovenantSqliteConnectionInitializer.Instance,
                TimeProvider.System);
        }

        internal CovenantProtectedArtifactErasureKernel Kernel { get; }

        /// <summary>
        /// The request digest the seeded finalization guard carries, and the value the receipt must
        /// copy into <c>FinalizationGuardDigest</c>.
        /// </summary>
        /// <remarks>
        /// Distinct from the all-zero content digest beside it, so a receipt that copied the wrong
        /// column would be visible rather than coincidentally equal.
        /// </remarks>
        internal static byte[] RequestDigest => [.. Enumerable.Repeat((byte)0x5A, 32)];

        internal static async Task<ErasureFixture> CreateAsync()
        {
            CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

            try
            {
                await database.InstallCoreObjectsAsync(
                    [
                        "grimoire_feature_schemas",
                        "Campaigns",
                        "Sessions",
                        "artifact_sensitivity",
                        "session_sensitivity_state",
                        "saga_memories",
                        "saga_memory_embeddings",
                        "saga_memory_attachment_provenance",
                        "lexicon_entries",
                        "lexicon_fact_attachment_provenance",
                        "annal_claims",
                        "annal_versions",
                        "annal_heads",
                        "annal_dependencies",
                        "lexicon_annal_fact_provenance",
                        "Entries",
                        "entry_embeddings",
                        "assistant_entry_finalizations",
                        "assistant_entry_erasure_receipts",
                        "tapestry_generations",
                        "tapestry_nodes",
                        "tapestry_node_embeddings",

                        // The delete guard is the reason the kernel borrows an authorization at all:
                        // without it these tests would prove nothing about the scope a purge runs under.
                        "artifact_sensitivity_guard_delete",
                        "artifact_sensitivity_guard_update",
                    ],
                    Token);

                await database.ExecuteAsync(
                    $"""
                     INSERT INTO grimoire_feature_schemas
                         (FamilyCode, TransactionTierCode, SchemaVersion, SourceDefinitionFingerprint,
                          InstalledCatalogFingerprint, InstalledAtUtc, HealthCode)
                     VALUES (0, 0, 11, hex(zeroblob(32)), 'sha256:' || hex(zeroblob(32)), '2026-08-16T00:00:00Z', 0);
                     INSERT INTO "Sessions" ("Id", "Title", "CreatedAt", "UpdatedAt")
                     VALUES ('{Format(SessionId)}', 'erasure', '2026-08-16T00:00:00Z', '2026-08-16T00:00:00Z');
                     """,
                    Token);

                return new ErasureFixture(database);
            }
            catch
            {
                await database.DisposeAsync();

                throw;
            }
        }

        internal CovenantProtectedArtifactErasurePage Page(
            Guid artifactId,
            Guid labelId,
            SensitiveArtifactKind kind,
            Guid? sessionId,
            Guid? campaignId = null,
            ulong revision = 0) =>
            new(
                CovenantOperationGateFixture.DatasetGeneration,
                [CovenantErasureAuthorityFixture.Item(artifactId, labelId, kind, sessionId, campaignId, revision)]);

        internal async Task<Guid> SeedLabelAsync(
            Guid artifactId,
            SensitiveArtifactKind kind,
            Guid? sessionId,
            Guid? campaignId = null,
            ulong revision = 0,
            string? storedIdentity = null)
        {
            Guid labelId = Guid.NewGuid();

            ArtifactSensitivityLabel label = CovenantErasureAuthorityFixture.Label(
                artifactId,
                labelId,
                kind,
                sessionId,
                campaignId,
                revision);

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = """
                INSERT INTO artifact_sensitivity (
                    LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                    ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId, ArtifactRevision,
                    ArtifactContentDigest, SensitivityDigest, ArtifactLabelDigest, CreatedAtUtc)
                VALUES ($labelId, $kind, $artifactId, 1, 1, $generations, NULL, $sessionId, $campaignId, NULL, $revision,
                    $contentDigest, $sensitivityDigest, $labelDigest, '2026-08-16T00:00:00Z');
                """;

            _ = command.Parameters.AddWithValue("$labelId", Format(labelId));

            _ = command.Parameters.AddWithValue("$kind", (long)kind);

            _ = command.Parameters.AddWithValue("$artifactId", storedIdentity ?? Format(artifactId));

            _ = command.Parameters.AddWithValue(
                "$generations",
                CovenantOperationGateFixture.DatasetGeneration.ToByteArray());

            _ = command.Parameters.AddWithValue(
                "$sessionId",
                sessionId is { } session ? Format(session) : DBNull.Value);

            _ = command.Parameters.AddWithValue(
                "$campaignId",
                campaignId is { } campaign ? Format(campaign) : DBNull.Value);

            _ = command.Parameters.AddWithValue("$revision", (long)revision);

            _ = command.Parameters.AddWithValue("$contentDigest", label.ArtifactContentDigest.Bytes.ToArray());

            _ = command.Parameters.AddWithValue("$sensitivityDigest", label.SensitivityDigest.Bytes.ToArray());

            _ = command.Parameters.AddWithValue("$labelDigest", label.LabelDigest.Bytes.ToArray());

            _ = await command.ExecuteNonQueryAsync(Token);

            return labelId;
        }

        /// <summary>
        /// A committed assistant entry with the finalization guard its receipt is bound to.
        /// </summary>
        /// <remarks>
        /// Seeded in the canonical spelling because that is what both writers of
        /// <c>assistant_entry_finalizations.AssistantEntryId</c> render: the turn committer hands the
        /// provider a raw <see cref="Guid"/>, which its type mapping uppercases, and the protected
        /// import formats the same way. A lowercase seed here would describe a database the guard
        /// trigger on that column now refuses, and would make this case agree with a comparison
        /// production does not make.
        ///
        /// <para>The guard is seeded rather than committed through a turn because it is this case's
        /// precondition and not its subject. What is asserted is the receipt, and the kernel is the only
        /// thing that writes one.</para>
        /// </remarks>
        internal async Task SeedCommittedAssistantEntryAsync(Guid artifactId)
        {
            await ExecuteAsync(
                $"""
                 INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
                 VALUES ('{Format(artifactId)}', '{Format(SessionId)}', 'assistant', 'the answer', 'm',
                         '2026-08-16T00:00:00Z', 1);
                 """);

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = """
                INSERT INTO assistant_entry_finalizations (
                    AssistantEntryId, SessionId, OutcomeCode, ContentSensitivityCode,
                    ContentSensitivityDigest, RequestDigest, FinalReceiptDigest, SourceEvidenceDigest,
                    FinalizedAtUtc)
                VALUES ($artifactId, $sessionId, 1, 1, $sensitivity, $request, NULL, NULL,
                        '2026-08-16T00:00:00Z');
                """;

            _ = command.Parameters.AddWithValue("$artifactId", Format(artifactId));

            _ = command.Parameters.AddWithValue("$sessionId", Format(SessionId));

            _ = command.Parameters.AddWithValue("$sensitivity", new byte[32]);

            _ = command.Parameters.AddWithValue("$request", RequestDigest);

            _ = await command.ExecuteNonQueryAsync(Token);
        }

        /// <summary>
        /// One published Tapestry generation with a single node, its embedding row and its plain
        /// vector-mirror row, keyed by the scope id the sweep would have written.
        /// </summary>
        internal async Task SeedTapestryAsync(string scopeKind, string scopeId, string generationId)
        {
            await ExecuteAsync(
                $"""
                 CREATE TABLE IF NOT EXISTS tapestry_node_embeddings_vec (NodeId TEXT PRIMARY KEY, Embedding BLOB NOT NULL);
                 INSERT INTO tapestry_generations
                     (GenerationId, ScopeKind, ScopeId, Status, AlgorithmVersion, SettingsFingerprint,
                      SummaryRecipeVersion, EmbeddingDimension, CorpusFingerprint, LayerCount, NodeCount,
                      RootNodeCount, TerminalReason, StartedAt, CompletedAt)
                 VALUES ('{generationId}', '{scopeKind}', '{scopeId}', 'Complete', 'algorithm', 'settings',
                         'recipe', 1, 'corpus', 1, 1, 1, 'LeafOnly',
                         '2026-08-16T00:00:00.0000000Z', '2026-08-16T00:00:00.0000000Z');
                 INSERT INTO tapestry_nodes
                     (NodeId, GenerationId, ScopeKind, ScopeId, Layer, ParentScopeKey, NodeKind, SourceLabel,
                      Content, ContentHash, EmbeddingDimension, CreatedAt)
                 VALUES ('{generationId}-node', '{generationId}', '{scopeKind}', '{scopeId}', 1, '{generationId}#root',
                         'Summary', 'summary', 'a summary of the erased words', 'hash', 1,
                         '2026-08-16T00:00:00.0000000Z');
                 INSERT INTO tapestry_node_embeddings (NodeId, Embedding, Dim) VALUES ('{generationId}-node', x'00', 1);
                 INSERT INTO tapestry_node_embeddings_vec (NodeId, Embedding) VALUES ('{generationId}-node', x'00');
                 """);
        }

        internal async Task<string?> ReceiptIdentityAsync()
        {
            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = "SELECT AssistantEntryId FROM assistant_entry_erasure_receipts;";

            return await command.ExecuteScalarAsync(Token) as string;
        }

        internal async Task<byte[]?> ReceiptGuardDigestAsync()
        {
            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = "SELECT FinalizationGuardDigest FROM assistant_entry_erasure_receipts;";

            return await command.ExecuteScalarAsync(Token) as byte[];
        }

        internal async Task<string?> LabelIdAsync(Guid artifactId) =>
            await _database.ScalarStringAsync($"SELECT LabelId FROM artifact_sensitivity WHERE ArtifactId = '{Format(artifactId)}';", Token);

        internal async Task SeedSessionSummaryAsync(Guid artifactId, string spelling, bool unscopedLabel = false)
        {
            await _database.InstallCoreObjectsAsync(["session_summary_artifacts", "session_summary_state"], Token);

            Guid? labelSessionId = unscopedLabel ? null : SessionId;

            Guid labelId = await SeedLabelAsync(artifactId, SensitiveArtifactKind.Summary, labelSessionId,
                revision: 1, storedIdentity: artifactId.ToString(spelling).ToLowerInvariant());

            string sensitivity = Convert.ToHexString(CovenantErasureAuthorityFixture.Label(
                artifactId, labelId, SensitiveArtifactKind.Summary, labelSessionId, revision: 1).SensitivityDigest.Bytes);

            await ExecuteAsync($"""
                INSERT INTO session_summary_artifacts(ArtifactId,SessionId,Revision,ContentDigest,SensitivityCode,SensitivityDigest,SummarizedThroughUtc,CreatedAtUtc)
                VALUES('{Format(artifactId)}','{Format(SessionId)}',1,x'{new string('1',64)}',1,x'{sensitivity}',NULL,'2026-10-01T00:00:00Z');
                INSERT INTO session_summary_state(SessionId,CurrentArtifactId,Revision,UpdatedAtUtc)
                VALUES('{Format(SessionId)}','{Format(artifactId)}',1,'2026-10-01T00:00:00Z');
                """);
        }

        internal async Task SeedCampaignSummariesAsync(
            Guid campaignId,
            Guid contribution,
            Guid rollup,
            string? rollupLabelIdentity = null,
            string? contributionLabelIdentity = null)
        {
            await _database.InstallCoreObjectsAsync([
                "session_campaign_bindings", "session_turn_claims", "assistant_finalization_capacity_reservations",
            ], Token);

            foreach (GrimoireSchemaObject item in GrimoireSchemaCatalog.CoreObjects.Where(static item => item.Name.StartsWith("campaign_", StringComparison.Ordinal)))
            {
                await _database.InstallCoreObjectsAsync([item.Name], Token);
            }

            await ExecuteAsync($"""
                UPDATE grimoire_feature_schemas SET SchemaVersion = 16 WHERE FamilyCode = 0 AND TransactionTierCode = 0;
                INSERT INTO Campaigns(Id,Name,NameLower,Path,Type,Settings,CreatedAt,UpdatedAt) VALUES('{Format(campaignId)}','campaign','campaign','/fixture',0,char(123)||char(125),'2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
                UPDATE Sessions SET CampaignId = '{Format(campaignId)}' WHERE Id = '{Format(SessionId)}';
                INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc) VALUES('{Format(SessionId)}',2,'{Format(campaignId)}','2026-10-01T00:00:00Z');
                """);

            Guid contributionLabel = await SeedLabelAsync(contribution, SensitiveArtifactKind.CampaignContribution, SessionId, campaignId,
                revision: 1, storedIdentity: contributionLabelIdentity);

            Guid rollupLabel = await SeedLabelAsync(rollup, SensitiveArtifactKind.CampaignRollup, null, campaignId,
                revision: 1, storedIdentity: rollupLabelIdentity);

            string sensitivity = Convert.ToHexString(CovenantErasureAuthorityFixture.Label(contribution, contributionLabel, SensitiveArtifactKind.CampaignContribution, SessionId, campaignId, revision: 1).SensitivityDigest.Bytes);

            await ExecuteAsync($"""
                INSERT INTO campaign_contribution_artifacts(ArtifactId,CampaignId,SessionId,Revision,Content,ContentDigest,SensitivityCode,SensitivityDigest,SourceManifestDigest,SourceGeneration,SummarizedThroughSequence,CreatedAtUtc)
                VALUES('{Format(contribution)}','{Format(campaignId)}','{Format(SessionId)}',1,'protected source',x'{new string('1',64)}',1,x'{sensitivity}',zeroblob(32),0,7,'2026-10-01T00:00:00Z');
                INSERT INTO campaign_contribution_state(SessionId,CampaignId,CurrentArtifactId,Revision,SourceGeneration,SummarizedThroughSequence,RefoldRequired,UpdatedAtUtc)
                VALUES('{Format(SessionId)}','{Format(campaignId)}','{Format(contribution)}',1,0,7,0,'2026-10-01T00:00:00Z');
                INSERT INTO campaign_rollup_artifacts(ArtifactId,CampaignId,Revision,Content,ContentDigest,SensitivityCode,SensitivityDigest,SourceManifestDigest,SourceGeneration,SourceCount,CreatedAtUtc)
                VALUES('{Format(rollup)}','{Format(campaignId)}',1,'protected Campaign',x'{new string('1',64)}',1,x'{sensitivity}',zeroblob(32),1,1,'2026-10-01T00:00:00Z');
                INSERT INTO campaign_rollup_state(CampaignId,CurrentArtifactId,Revision,SourceGeneration,RefoldRequired,LastFoldedSessionId,UpdatedAtUtc)
                VALUES('{Format(campaignId)}','{Format(rollup)}',1,1,0,'{Format(SessionId)}','2026-10-01T00:00:00Z');
                INSERT INTO campaign_rollup_sources(RollupArtifactId,CampaignId,RollupRevision,SessionId,ContributionArtifactId,ContributionRevision,ContentDigest,SensitivityDigest,SummarizedThroughSequence)
                VALUES('{Format(rollup)}','{Format(campaignId)}',1,'{Format(SessionId)}','{Format(contribution)}',1,x'{new string('1',64)}',x'{sensitivity}',7);
                """);
        }

        internal async Task SeedSagaAsync(Guid artifactId)
        {
            await ExecuteAsync(
                $"""
                 INSERT INTO saga_memories (Id, Content, CreatedAt) VALUES ('{Format(artifactId)}', 'c', '2026-08-16T00:00:00Z');
                 INSERT INTO saga_memory_embeddings (MemoryId, Embedding, Dim) VALUES ('{Format(artifactId)}', x'00', 1);
                 """);
        }

        internal SqliteConnection Connection => _database.Connection;

        internal Task ExecuteAsync(string sql) => _database.ExecuteAsync(sql, Token);

        internal async Task<long> CountAsync(string sql) =>
            Convert.ToInt64(await _database.ScalarLongAsync(sql, Token), CultureInfo.InvariantCulture);

        public ValueTask DisposeAsync() => _database.DisposeAsync();
    }
}
