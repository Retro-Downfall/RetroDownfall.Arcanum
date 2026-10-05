using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
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
            Guid? campaignId = null) =>
            new(
                CovenantOperationGateFixture.DatasetGeneration,
                [CovenantErasureAuthorityFixture.Item(artifactId, labelId, kind, sessionId, campaignId)]);

        internal async Task<Guid> SeedLabelAsync(
            Guid artifactId,
            SensitiveArtifactKind kind,
            Guid? sessionId,
            Guid? campaignId = null)
        {
            Guid labelId = Guid.NewGuid();

            ArtifactSensitivityLabel label = CovenantErasureAuthorityFixture.Label(
                artifactId,
                labelId,
                kind,
                sessionId,
                campaignId);

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = """
                INSERT INTO artifact_sensitivity (
                    LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                    ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId, ArtifactRevision,
                    ArtifactContentDigest, SensitivityDigest, ArtifactLabelDigest, CreatedAtUtc)
                VALUES ($labelId, $kind, $artifactId, 1, 1, $generations, NULL, $sessionId, $campaignId, NULL, 0,
                    $contentDigest, $sensitivityDigest, $labelDigest, '2026-08-16T00:00:00Z');
                """;

            _ = command.Parameters.AddWithValue("$labelId", Format(labelId));

            _ = command.Parameters.AddWithValue("$kind", (long)kind);

            _ = command.Parameters.AddWithValue("$artifactId", Format(artifactId));

            _ = command.Parameters.AddWithValue(
                "$generations",
                CovenantOperationGateFixture.DatasetGeneration.ToByteArray());

            _ = command.Parameters.AddWithValue(
                "$sessionId",
                sessionId is { } session ? Format(session) : DBNull.Value);

            _ = command.Parameters.AddWithValue(
                "$campaignId",
                campaignId is { } campaign ? Format(campaign) : DBNull.Value);

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
