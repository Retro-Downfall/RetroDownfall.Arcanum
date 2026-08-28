using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

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

    [Fact]
    public async Task An_ordinary_purge_authority_erases_under_the_retention_purge_scope()
    {

        await using ErasureFixture fixture = await ErasureFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Guid labelId = await fixture.SeedLabelAsync(artifactId, SensitiveArtifactKind.Lexicon, sessionId: null);

        await fixture.ExecuteAsync(
            $"""
             INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
             VALUES ('{Format(artifactId)}', 'n', 'n', 'Person', '[]', '', '2026-08-16T00:00:00Z');
             """);

        FakeCovenantAuthorityProvider provider = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: provider);

        await using CovenantWriteLease lease = (await gate.AcquireWriteAsync(
            CovenantOperationScope.Global,
            Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority.ForOrdinary(
            lease,
            CovenantErasureAuthorityFixture.OperatorContext(provider),
            CovenantErasureAuthorityFixture.Issuer(provider)).Value;

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.ErasePageAsync(
            fixture.Page(artifactId, labelId, SensitiveArtifactKind.Lexicon, sessionId: null),
            authority,
            Token);

        Assert.True(erased.IsSuccess);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM lexicon_entries;"));

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
                        "Campaigns",
                        "Sessions",
                        "artifact_sensitivity",
                        "session_sensitivity_state",
                        "saga_memories",
                        "saga_memory_embeddings",
                        "saga_memory_attachment_provenance",
                        "lexicon_entries",
                        "lexicon_fact_attachment_provenance",
                        "Entries",
                        "entry_embeddings",
                        "assistant_entry_finalizations",
                        "assistant_entry_erasure_receipts",

                        // The delete guard is the reason the kernel borrows an authorization at all:
                        // without it these tests would prove nothing about the scope a purge runs under.
                        "artifact_sensitivity_guard_delete",
                        "artifact_sensitivity_guard_update",
                    ],
                    Token);

                await database.ExecuteAsync(
                    $"""
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

        internal Task ExecuteAsync(string sql) => _database.ExecuteAsync(sql, Token);

        internal async Task<long> CountAsync(string sql) =>
            Convert.ToInt64(await _database.ScalarLongAsync(sql, Token), CultureInfo.InvariantCulture);

        public ValueTask DisposeAsync() => _database.DisposeAsync();

    }

}
