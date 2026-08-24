using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The stopped-host managed-file reconciliation, against a real SQLCipher catalog and real files.
/// </summary>
/// <remarks>
/// The suite is written against what an operator can observe afterwards rather than against the
/// reconciler's own return value, because the return value collapses every refusal to one recovery
/// -required error on purpose. What distinguishes the arms is the published checkpoint, the rows, and
/// the files, so those are what the assertions read.
/// </remarks>
public sealed class FullInstallationResetManagedFileReconcilerTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void Reconciliation_phase_codes_are_literal_and_exhaustive()
    {

        Assert.Equal(
            1,
            (byte)FullInstallationResetManagedFileReconciliationPhase.InventoryPrepared);

        Assert.Equal(
            2,
            (byte)FullInstallationResetManagedFileReconciliationPhase.WriteIntentsReconciled);

        Assert.Equal(
            3,
            (byte)FullInstallationResetManagedFileReconciliationPhase.WorkItemsReconciled);

        Assert.Equal(
            4,
            (byte)FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified);

        Assert.Equal(
            4,
            Enum.GetValues<FullInstallationResetManagedFileReconciliationPhase>().Length);

        Assert.Equal(1, (byte)FullInstallationResetManagedFileBlockerArm.ManualWriteOrphan);

        Assert.Equal(2, (byte)FullInstallationResetManagedFileBlockerArm.ManualWorkItemOrphan);

        Assert.Equal(2, Enum.GetValues<FullInstallationResetManagedFileBlockerArm>().Length);

    }

    [Fact]
    public async Task An_empty_installation_walks_all_four_phases_and_verifies_a_zero_inventory()
    {

        await using ReconcilerFixture fixture = await ReconcilerFixture.CreateAsync();

        Result<InstallationResetActivePublication> reconciled = await fixture.ReconcileAsync();

        Assert.True(reconciled.IsSuccess, reconciled.Error.Message);

        FullInstallationResetManagedFileCheckpointV1 checkpoint = fixture.ManagedCheckpoint();

        Assert.Equal(
            FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified,
            checkpoint.Phase);

        Assert.Equal(0UL, checkpoint.SourceCount);

        Assert.Equal(0UL, checkpoint.LocalErasureWorkItemCount);

        Assert.Equal(0UL, checkpoint.SafeTerminalWriteIntentCount);

        Assert.Equal(0UL, checkpoint.ManualWriteOrphanCount);

        // Every phase was published rather than skipped to the last one, because each is a fact the
        // next relies on and a resume has to be able to land on any of them.
        Assert.Equal(
            [
                FullInstallationResetManagedFileReconciliationPhase.InventoryPrepared,
                FullInstallationResetManagedFileReconciliationPhase.WriteIntentsReconciled,
                FullInstallationResetManagedFileReconciliationPhase.WorkItemsReconciled,
                FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified,
            ],
            fixture.PublishedPhases());

    }

    [Fact]
    public async Task An_unfinished_write_is_cleaned_and_an_adopted_file_is_erased_before_the_counts_add_up()
    {

        await using ReconcilerFixture fixture = await ReconcilerFixture.CreateAsync();

        SeededSource unfinished = await fixture.SeedUnfinishedWriteAsync("draft.md", "partial");

        SeededSource adopted = await fixture.SeedAdoptedFileAsync("answer.md", "final");

        Assert.True(File.Exists(unfinished.ChildPath));

        Assert.True(File.Exists(adopted.ChildPath));

        Result<InstallationResetActivePublication> reconciled = await fixture.ReconcileAsync();

        Assert.True(reconciled.IsSuccess, reconciled.Error.Message);

        // Both files are gone from disk, which is the only claim that matters to the operator.
        Assert.False(File.Exists(unfinished.ChildPath));

        Assert.False(File.Exists(adopted.ChildPath));

        FullInstallationResetManagedFileCheckpointV1 checkpoint = fixture.ManagedCheckpoint();

        Assert.Equal(
            FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified,
            checkpoint.Phase);

        Assert.Equal(2UL, checkpoint.SourceCount);

        Assert.Equal(2UL, checkpoint.SafeTerminalWriteIntentCount);

        Assert.Equal(0UL, checkpoint.ManualWriteOrphanCount);

        Assert.Equal(1UL, checkpoint.LocalErasureWorkItemCount);

        Assert.Equal(1UL, checkpoint.CompletedWorkItemCount);

        Assert.Equal(0UL, checkpoint.ManualWorkItemOrphanCount);

        // The adopted producer reached Erased and the unfinished one reached Cleaned; the label the
        // adopted file carried is gone with it.
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT COUNT(*) FROM managed_file_write_intents WHERE PhaseCode = 10;"));

        Assert.Equal(1, await fixture.CountAsync(
            "SELECT COUNT(*) FROM managed_file_write_intents WHERE PhaseCode = 8;"));

        Assert.Equal(0, await fixture.CountAsync(
            "SELECT COUNT(*) FROM artifact_sensitivity;"));

        Assert.Equal(
            checked((ulong)checkpoint.OrderedSourceWriteOperationIds.Length),
            checkpoint.SourceCount);

        Assert.Contains(unfinished.WriteOperationId, checkpoint.OrderedSourceWriteOperationIds);

        Assert.Contains(adopted.WriteOperationId, checkpoint.OrderedSourceWriteOperationIds);

    }

    [Fact]
    public async Task A_file_that_is_not_ours_becomes_an_authenticated_manual_orphan_and_is_left_on_disk()
    {

        await using ReconcilerFixture fixture = await ReconcilerFixture.CreateAsync();

        SeededSource adopted = await fixture.SeedAdoptedFileAsync("answer.md", "final");

        // Same name, different bytes: the ownership evidence the producer recorded no longer
        // describes what is there, so the deleter must refuse rather than guess.
        await File.WriteAllTextAsync(adopted.ChildPath, "somebody else's", Token);

        Result<InstallationResetActivePublication> reconciled = await fixture.ReconcileAsync();

        Assert.True(reconciled.IsSuccess, reconciled.Error.Message);

        Assert.True(File.Exists(adopted.ChildPath));

        Assert.Equal("somebody else's", await File.ReadAllTextAsync(adopted.ChildPath, Token));

        FullInstallationResetManagedFileCheckpointV1 checkpoint = fixture.ManagedCheckpoint();

        Assert.Equal(
            FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified,
            checkpoint.Phase);

        // The producer stays AdoptedAndLabeled, so it is not a safe terminal; its work item is the
        // manual orphan, and the classification digest commits to exactly that shape.
        Assert.Equal(1UL, checkpoint.LocalErasureWorkItemCount);

        Assert.Equal(0UL, checkpoint.CompletedWorkItemCount);

        Assert.Equal(1UL, checkpoint.ManualWorkItemOrphanCount);

        Guid workItemId = checkpoint.OrderedLocalErasureWorkItemIds!.Value.Single();

        // The published digest is exactly the one the observed shape produces: the adopted source
        // recorded at the phase it genuinely still holds, under the write-orphan arm, and its refused
        // work item under the work-item arm.
        Result<CovenantDigest> expected =
            FullInstallationResetManagedFileDigests.TerminalClassification(
                [
                    new FullInstallationResetManagedSourceClassificationV1(
                        adopted.WriteOperationId,
                        ManagedFileWriteIntentPhase.AdoptedAndLabeled,
                        FullInstallationResetManagedFileDigests.BlockerEvidence(
                            adopted.WriteOperationId,
                            FullInstallationResetManagedFileBlockerArm.ManualWriteOrphan,
                            CovenantErasureBlocker.ManualOwnershipMismatch).Value),
                ],
                [
                    new FullInstallationResetManagedWorkItemClassificationV1(
                        workItemId,
                        LocalErasureWorkItemState.ManualBlocker,
                        DeletionEvidence: null,
                        FullInstallationResetManagedFileDigests.BlockerEvidence(
                            workItemId,
                            FullInstallationResetManagedFileBlockerArm.ManualWorkItemOrphan,
                            CovenantErasureBlocker.ManualOwnershipMismatch).Value),
                ]);

        Assert.True(expected.IsSuccess, expected.Error.Message);

        Assert.Equal(expected.Value, checkpoint.TerminalClassificationDigest);

        // The label and the producer row are untouched, because nothing was proved gone.
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT COUNT(*) FROM managed_file_write_intents WHERE PhaseCode = 7;"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

    }

    [Fact]
    public async Task A_source_still_adopted_at_the_end_refuses_terminal_verification()
    {

        await using ReconcilerFixture fixture = await ReconcilerFixture.CreateAsync();

        SeededSource adopted = await fixture.SeedAdoptedFileAsync("answer.md", "final");

        await File.WriteAllTextAsync(adopted.ChildPath, "somebody else's", Token);

        Result<InstallationResetActivePublication> reconciled = await fixture.ReconcileAsync();

        Assert.True(reconciled.IsSuccess, reconciled.Error.Message);

        // The producer could not be terminalized, so it must not be counted as a safe terminal write.
        FullInstallationResetManagedFileCheckpointV1 checkpoint = fixture.ManagedCheckpoint();

        Assert.Equal(1UL, checkpoint.SourceCount);

        Assert.Equal(0UL, checkpoint.SafeTerminalWriteIntentCount);

        Assert.Equal(1UL, checkpoint.ManualWriteOrphanCount);

        Assert.Equal(
            checked(
                checkpoint.SafeTerminalWriteIntentCount!.Value
                + checkpoint.ManualWriteOrphanCount!.Value),
            checkpoint.SourceCount);

    }

    [Fact]
    public async Task A_source_that_appears_after_the_inventory_was_fixed_refuses_the_whole_reconciliation()
    {

        await using ReconcilerFixture fixture = await ReconcilerFixture.CreateAsync();

        _ = await fixture.SeedUnfinishedWriteAsync("draft.md", "partial");

        Result<InstallationResetActivePublication> first = await fixture.ReconcileAsync();

        Assert.True(first.IsSuccess, first.Error.Message);

        // The reconciliation is terminal. A source appearing now is an inventory this operation never
        // authenticated, and a resume must refuse rather than quietly widen its own scope.
        _ = await fixture.SeedUnfinishedWriteAsync("late.md", "late");

        Result<InstallationResetActivePublication> second = await fixture.ReconcileAsync();

        Assert.True(second.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, second.Error.Code);

    }

    [Fact]
    public async Task A_resumed_reconciliation_reruns_from_its_published_phase_without_a_second_effect()
    {

        await using ReconcilerFixture fixture = await ReconcilerFixture.CreateAsync();

        SeededSource adopted = await fixture.SeedAdoptedFileAsync("answer.md", "final");

        Result<InstallationResetActivePublication> first = await fixture.ReconcileAsync();

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.False(File.Exists(adopted.ChildPath));

        int publicationsAfterFirst = fixture.PublishedPhases().Count;

        Result<InstallationResetActivePublication> second = await fixture.ReconcileAsync();

        Assert.True(second.IsSuccess, second.Error.Message);

        // Already terminal: the resume republishes nothing, because republishing would advance the
        // envelope revision and invalidate every proof bound to the one it replaced.
        Assert.Equal(publicationsAfterFirst, fixture.PublishedPhases().Count);

    }

    [Fact]
    public async Task A_record_whose_campaign_receipt_is_not_terminal_is_refused_before_anything_is_read()
    {

        await using ReconcilerFixture fixture = await ReconcilerFixture.CreateAsync(terminalReceipt: false);

        SeededSource adopted = await fixture.SeedAdoptedFileAsync("answer.md", "final");

        Result<InstallationResetActivePublication> reconciled = await fixture.ReconcileAsync();

        Assert.True(reconciled.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, reconciled.Error.Code);

        Assert.True(File.Exists(adopted.ChildPath));

        Assert.Empty(fixture.PublishedPhases());

    }

    [Fact]
    public async Task A_marker_checkpoint_short_of_pair_absence_is_refused()
    {

        await using ReconcilerFixture fixture = await ReconcilerFixture.CreateAsync(
            phase: HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted);

        Result<InstallationResetActivePublication> reconciled = await fixture.ReconcileAsync();

        Assert.True(reconciled.IsFailure);

        Assert.Empty(fixture.PublishedPhases());

    }

    [Fact]
    public async Task A_publication_that_moved_underneath_the_operation_revokes_the_authority()
    {

        await using ReconcilerFixture fixture = await ReconcilerFixture.CreateAsync();

        _ = await fixture.SeedAdoptedFileAsync("answer.md", "final");

        InstallationResetActivePublication stale = fixture.Store.Current;

        // Somebody else published, so the envelope revision this operation holds is no longer current.
        // Every effect after that point would be authorized by a record that is gone.
        fixture.Store.AdvanceOutOfBand();

        Result<InstallationResetActivePublication> reconciled =
            await fixture.ReconcileAsync(stale);

        Assert.True(reconciled.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, reconciled.Error.Code);

    }

    [Fact]
    public async Task The_erasure_authority_cannot_be_minted_from_anything_but_the_reconcilers_own_proof()
    {

        await using ReconcilerFixture fixture = await ReconcilerFixture.CreateAsync();

        // The factory is internal so the erasure kernel can be handed an authority, but its input is
        // the reconciler's own private proof. Anything else is a forgery, and a factory that accepted
        // one would make the private constructor decorative.
        _ = Assert.Throws<InvalidOperationException>(() =>
            FullInstallationResetManagedFileReconciler
                .FullInstallationResetManagedFileErasureAuthority
                .Create(fixture.Subject, new object()));

        _ = Assert.Throws<InvalidOperationException>(() =>
            FullInstallationResetManagedFileReconciler
                .FullInstallationResetManagedFileErasureAuthority
                .Create(fixture.Subject, fixture.Store.Current));

    }

    private sealed record SeededSource(Guid WriteOperationId, string ChildPath);

    private sealed class ReconcilerFixture : IAsyncDisposable
    {

        private static readonly Guid CampaignId = CovenantOperationGateFixture.CampaignOne;

        private static readonly Guid InstallationId =
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

        private readonly CovenantSchemaScratchDatabase _database;

        private readonly string _workspaceRoot;

        private readonly string _campaignRoot;

        private readonly ArcanumMaintenanceLock _lock;

        private bool _campaignInserted;

        private ReconcilerFixture(
            CovenantSchemaScratchDatabase database,
            string workspaceRoot,
            ArcanumMaintenanceLock heldLock,
            RecordingActiveStore store)
        {

            _database = database;

            _workspaceRoot = workspaceRoot;

            _campaignRoot = Path.Combine(workspaceRoot, "campaign");

            _lock = heldLock;

            Store = store;

            ManagedFileErasureStateMachine stateMachine = new(
                CovenantSqliteConnectionInitializer.Instance,
                new ManagedFileCapabilityOpener(),
                new ManagedFileOwnershipVerifier(),
                TimeProvider.System);

            Subject = new FullInstallationResetManagedFileReconciler(
                store,
                new CovenantManagedFileErasureKernel(
                    new UnreachableConnectionSource(),
                    CovenantSqliteConnectionInitializer.Instance,
                    stateMachine,
                    TimeProvider.System),
                new ManagedFileWriteIntentRecoveryService(
                    CovenantSqliteConnectionInitializer.Instance,
                    new ManagedFileCapabilityOpener(),
                    new ManagedFileOwnershipVerifier(),
                    TimeProvider.System));

        }

        internal RecordingActiveStore Store { get; }

        internal FullInstallationResetManagedFileReconciler Subject { get; }

        internal static async Task<ReconcilerFixture> CreateAsync(
            HostToolsMarkerPairResetPhase phase = HostToolsMarkerPairResetPhase.PairAbsenceVerified,
            bool terminalReceipt = true)
        {

            CovenantSchemaScratchDatabase database =
                await CovenantSchemaScratchDatabase.CreateAsync(Token);

            string workspaceRoot =
                Path.Combine(Path.GetTempPath(), $"arcanum-managed-reconcile-{Guid.NewGuid():N}");

            try
            {

                _ = Directory.CreateDirectory(workspaceRoot);

                string guardedRoot = Path.Combine(workspaceRoot, "guarded");

                _ = Directory.CreateDirectory(guardedRoot);

                await database.InstallCoreObjectsAsync(
                    [
                        "Campaigns",
                        "Sessions",
                        "artifact_sensitivity",
                        "session_sensitivity_state",
                        "campaign_path_identities",
                        "managed_file_write_intents",
                        "local_erasure_work_items",
                        "artifact_sensitivity_guard_delete",
                        "artifact_sensitivity_guard_update",
                        "managed_file_write_intents_guard_insert",
                        "managed_file_write_intents_guard_update",
                        "managed_file_write_intents_guard_delete",
                        "local_erasure_work_items_guard_insert",
                        "local_erasure_work_items_guard_update",
                        "local_erasure_work_items_guard_delete",
                    ],
                    Token);

                ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                    ArcanumMaintenanceLock.TryAcquire(guardedRoot));

                RecordingActiveStore store = new(guardedRoot, Publication(phase, terminalReceipt));

                return new ReconcilerFixture(database, workspaceRoot, heldLock, store);

            }
            catch
            {

                await database.DisposeAsync();

                throw;

            }

        }

        internal Task<Result<InstallationResetActivePublication>> ReconcileAsync(
            InstallationResetActivePublication? publication = null) =>
            Subject.ReconcileAsync(
                _lock,
                publication ?? Store.Current,
                _database.Connection,
                Token);

        internal FullInstallationResetManagedFileCheckpointV1 ManagedCheckpoint() =>
            Store.Current.Payload.HostToolsMarkerPairReset?.ManagedFile
            ?? throw new InvalidOperationException("No managed-file checkpoint was published.");

        internal IReadOnlyList<FullInstallationResetManagedFileReconciliationPhase> PublishedPhases() =>
            Store.PublishedManagedPhases;

        /// <summary>
        /// Creates a real file and the producer row an adopted managed write leaves behind.
        /// </summary>
        internal async Task<SeededSource> SeedAdoptedFileAsync(string leaf, string content)
        {

            SeedContext context = await SeedFileAsync(leaf, content);

            long revision = await WalkAsync(
                context,
                ManagedFileWriteIntentPhase.AdoptedAndLabeled);

            _ = revision;

            return new SeededSource(context.WriteOperationId, context.ChildPath);

        }

        /// <summary>
        /// Creates a real temporary child and the producer row a crashed managed write leaves behind.
        /// </summary>
        internal async Task<SeededSource> SeedUnfinishedWriteAsync(string leaf, string content)
        {

            SeedContext context = await SeedFileAsync(leaf, content, temporary: true);

            _ = await WalkAsync(context, ManagedFileWriteIntentPhase.TempWritten);

            return new SeededSource(context.WriteOperationId, context.ChildPath);

        }

        internal async Task<long> CountAsync(string sql) =>
            Convert.ToInt64(await _database.ScalarLongAsync(sql, Token), CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {

            _lock.Dispose();

            await _database.DisposeAsync();

            try
            {

                Directory.Delete(_workspaceRoot, recursive: true);

            }
            catch (IOException)
            {

                // A scratch workspace under the OS temp root; a failure to remove it is not a test
                // outcome.
            }

        }

        private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

        private static CovenantDigest Digest(byte value) =>
            new(Enumerable.Repeat(value, 32).ToArray());

        private static CovenantDigest PathIdentity(string path) =>
            FileHandleIdentityInterop.TryGetPathIdentity(path, out FileHandleIdentity identity)
                ? ManagedFilePhysicalIdentity.Digest(identity)
                : throw new InvalidOperationException($"No filesystem identity for '{path}'.");

        /// <summary>
        /// A publication carrying an authenticated marker-pair checkpoint at the requested phase.
        /// </summary>
        /// <remarks>
        /// The Campaign receipt is an empty inventory whose deleted and orphan counts are both zero,
        /// which is the terminal shape for an installation with no registered Campaign roots. The
        /// nonterminal variant keeps the same vector and breaks only the arithmetic, so the refusal
        /// under test is the sum rather than a missing field.
        /// </remarks>
        private static InstallationResetActivePublication Publication(
            HostToolsMarkerPairResetPhase phase,
            bool terminalReceipt)
        {

            Guid operation = Guid.Parse("11112222-3333-4444-8555-666677778888");

            DateTimeOffset acceptedAtUtc = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

            FullInstallationResetExternalRemediationAttestation attestation = new(
                Version: 1,
                operation,
                InstallationId,
                HostToolsTransitionId: Guid.Parse("11111111-2222-4333-8444-555555555555"),
                TaintMasterKeyVersion: 7,
                AuthorityFingerprint: Digest(0x5A),
                DatabaseMarkerDigest: Digest(0x5B),
                OsMarkerDigest: Digest(0x5C),
                RemediationActionDigest: Digest(0x5D),
                NonceBase64Url: "nonce",
                Issuer: "issuer",
                IssuedAtUtc: acceptedAtUtc,
                ExpiresAtUtc: acceptedAtUtc.AddHours(1),
                SignatureBase64Url: "signature");

            HostProcessToolsDatabaseMarkerEvidence database = new(
                InstallationId.ToString(),
                RetroDownfall.Arcanum.Core.Security.CovenantHostToolsState.HostToolsTainted,
                attestation.HostToolsTransitionId,
                attestation.TaintMasterKeyVersion,
                attestation.AuthorityFingerprint);

            HostProcessToolsOsMarkerEvidence osMarker = new(
                InstallationId.ToString(),
                attestation.HostToolsTransitionId,
                attestation.TaintMasterKeyVersion,
                attestation.AuthorityFingerprint,
                attestation.OsMarkerDigest,
                Digest(0x5E));

            ImmutableArray<Guid> markerIntents = [];

            HostToolsMarkerPairResetCheckpointV1 checkpoint = new(
                Version: 1,
                phase,
                new FullInstallationResetRestartProofV1(
                    Version: 1,
                    FullInstallationResetSignedAttestationProjectionV1.FromAttestation(attestation),
                    acceptedAtUtc,
                    Digest(0x60),
                    database,
                    osMarker,
                    Digest(0x61)),
                CampaignInventory: [],
                Digest(0x62),
                Digest(0x63),
                MarkerIntentCount: 0,
                markerIntents,
                FullInstallationResetMarkerPairResetDigests
                    .FullResetIntentVector(markerIntents)
                    .Value,
                DeletedCount: 0,
                OrphanCount: terminalReceipt ? 0 : null);

            InstallationResetActiveRecord record = new(
                InstallationResetActiveStore.CurrentVersion,
                operation,
                "full-reset-plan",
                InstallationResetScope.All,
                new DataRetentionWorkspaceBinding(CampaignId, "/workspace"),
                new InstallationResetAcceptedBinding("binding", [], [], [], [], []),
                InstallationResetPhase.Prepared,
                PointOfNoReturn: false,
                RowsDeleted: 0,
                FilesDeleted: 0,
                EstimatedBytesDeleted: 0,
                CredentialResults: [],
                LastErrorCode: ErrorCodes.Data.RecoveryRequired,
                FullInstallationResetRemediationClaim: new FullInstallationResetRemediationClaimV1(
                    1,
                    operation,
                    InstallationId,
                    Digest(0x60),
                    Digest(0x45),
                    Digest(0x46),
                    acceptedAtUtc),
                HostToolsMarkerPairReset: checkpoint);

            InstallationResetActiveLocation location = new(
                "/active",
                Digest(0x10),
                Digest(0x11),
                "reset.active",
                Digest(0x12));

            CovenantDigest envelopeDigest = Digest(0x14);

            return new InstallationResetActivePublication(
                location,
                new InstallationResetActiveEnvelopeV2(
                    2,
                    location.ProfileNamespaceDigest,
                    InstallationId,
                    operation,
                    1,
                    Digest(0x13),
                    location.Digest,
                    InstallationResetScope.All,
                    record.PlanId,
                    "nonce",
                    "ciphertext",
                    "tag"),
                envelopeDigest,
                InstallationResetActivePayloadV2.FromRecord(record),
                new InstallationResetActiveAnchorV1(
                    1,
                    InstallationResetActiveAnchorState.Active,
                    location.ProfileNamespaceDigest,
                    InstallationId,
                    operation,
                    1,
                    envelopeDigest,
                    location.Digest));

        }

        private sealed record SeedContext(
            Guid WriteOperationId,
            Guid ArtifactId,
            Guid LabelId,
            string ChildPath,
            ManagedFileWriteDurableLocationEvidence Location,
            ManagedFileOwnershipEvidence Ownership,
            CovenantDigest CreatedChildIdentity,
            string Content);

        private async Task<SeedContext> SeedFileAsync(
            string leaf,
            string content,
            bool temporary = false)
        {

            string parent = Path.Combine(_campaignRoot, "notes");

            _ = Directory.CreateDirectory(parent);

            await InsertCampaignOnceAsync();

            string temporaryLeaf = $"{leaf}.tmp";

            string childPath = Path.Combine(parent, temporary ? temporaryLeaf : leaf);

            await File.WriteAllTextAsync(childPath, content, Token);

            ManagedFileDurableLocationEvidence target = new(
                PathIdentity(_campaignRoot),
                pathRevision: 1,
                ["notes"],
                PathIdentity(parent),
                leaf);

            return new SeedContext(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                childPath,
                new ManagedFileWriteDurableLocationEvidence(target, temporaryLeaf),
                new ManagedFileOwnershipEvidence(
                    PathIdentity(childPath),
                    new CovenantDigest(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                    Encoding.UTF8.GetByteCount(content)),
                PathIdentity(childPath),
                content);

        }

        private async Task InsertCampaignOnceAsync()
        {

            if (_campaignInserted)
            {

                return;

            }

            _ = Directory.CreateDirectory(_campaignRoot);

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = """
                INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
                VALUES ($id, 'c', 'c', $root, 1, '{}', '2026-08-16T00:00:00Z', '2026-08-16T00:00:00Z');

                INSERT INTO campaign_path_identities (
                    CampaignId, PolicyVersion, Revision, DisplayPath, Depth, PhysicalIdentityDigest, UpdatedAtUtc)
                VALUES ($id, 1, 1, $root, 2, $identity, '2026-08-16T00:00:00Z');
                """;

            _ = command.Parameters.AddWithValue("$id", Format(CampaignId));

            _ = command.Parameters.AddWithValue("$root", _campaignRoot);

            _ = command.Parameters.AddWithValue(
                "$identity",
                PathIdentity(_campaignRoot).Bytes.ToArray());

            _ = await command.ExecuteNonQueryAsync(Token);

            _campaignInserted = true;

        }

        /// <summary>
        /// Walks a producer row to the requested phase through the exact edges its guard accepts.
        /// </summary>
        private async Task<long> WalkAsync(SeedContext context, ManagedFileWriteIntentPhase phase)
        {

            if (phase is ManagedFileWriteIntentPhase.AdoptedAndLabeled)
            {

                await InsertLabelAsync(context);

            }

            using CovenantSqliteAuthorizationScope writer =
                CovenantSqliteConnectionInitializer.Instance.Authorize(
                    _database.Connection,
                    CovenantSqliteAuthorizationKind.ManagedFileIntentMutation);

            await using (SqliteCommand insert = _database.Connection.CreateCommand())
            {

                insert.CommandText = """
                    INSERT INTO managed_file_write_intents (
                        WriteOperationId, StableEffectIdentityDigest, ArtifactId, SensitivityLabelId,
                        SensitivityLabelDigest, PendingArtifactSensitivityLabel, DurableLocationEvidence,
                        ExpectedContentHash, ExpectedContentLength, CreatedChildPhysicalIdentityDigest,
                        FinalOwnershipEvidence, PhaseCode, Revision, RetryCount, CreatedAtUtc, UpdatedAtUtc)
                    VALUES ($write, $effect, $artifact, $label, zeroblob(32), zeroblob(64), $location,
                        $contentHash, $contentLength, NULL, NULL, 1, 0, 0,
                        '2026-08-16T00:00:00Z', '2026-08-16T00:00:00Z');
                    """;

                _ = insert.Parameters.AddWithValue("$write", Format(context.WriteOperationId));

                _ = insert.Parameters.AddWithValue(
                    "$effect",
                    SHA256.HashData(Encoding.UTF8.GetBytes(Format(context.WriteOperationId))));

                _ = insert.Parameters.AddWithValue("$artifact", Format(context.ArtifactId));

                _ = insert.Parameters.AddWithValue("$label", Format(context.LabelId));

                _ = insert.Parameters.AddWithValue(
                    "$location",
                    ManagedFileEvidenceCodec.EncodeWriteLocation(context.Location));

                _ = insert.Parameters.AddWithValue(
                    "$contentHash",
                    context.Ownership.ContentHash.Bytes.ToArray());

                _ = insert.Parameters.AddWithValue(
                    "$contentLength",
                    context.Ownership.ContentLength);

                _ = await insert.ExecuteNonQueryAsync(Token);

            }

            await using (SqliteCommand created = _database.Connection.CreateCommand())
            {

                created.CommandText = """
                    UPDATE managed_file_write_intents
                    SET PhaseCode = 2,
                        Revision = Revision + 1,
                        CreatedChildPhysicalIdentityDigest = $child,
                        UpdatedAtUtc = '2026-08-16T00:00:01Z'
                    WHERE WriteOperationId = $write;
                    """;

                _ = created.Parameters.AddWithValue(
                    "$child",
                    context.CreatedChildIdentity.Bytes.ToArray());

                _ = created.Parameters.AddWithValue("$write", Format(context.WriteOperationId));

                _ = await created.ExecuteNonQueryAsync(Token);

            }

            long revision = 1;

            for (int next = 3; next <= (int)phase; next++)
            {

                await using SqliteCommand advance = _database.Connection.CreateCommand();

                advance.CommandText = """
                    UPDATE managed_file_write_intents
                    SET PhaseCode = $phase,
                        Revision = Revision + 1,
                        PendingArtifactSensitivityLabel =
                            CASE WHEN $phase = 7 THEN NULL ELSE PendingArtifactSensitivityLabel END,
                        FinalOwnershipEvidence =
                            CASE WHEN $phase = 7 THEN $ownership ELSE FinalOwnershipEvidence END,
                        UpdatedAtUtc = '2026-08-16T00:00:02Z'
                    WHERE WriteOperationId = $write;
                    """;

                _ = advance.Parameters.AddWithValue("$phase", next);

                _ = advance.Parameters.AddWithValue(
                    "$ownership",
                    ManagedFileEvidenceCodec.EncodeOwnership(context.Ownership));

                _ = advance.Parameters.AddWithValue("$write", Format(context.WriteOperationId));

                _ = await advance.ExecuteNonQueryAsync(Token);

                revision++;

            }

            return revision;

        }

        private async Task InsertLabelAsync(SeedContext context)
        {

            ArtifactSensitivityLabel label = CovenantErasureAuthorityFixture.Label(
                context.ArtifactId,
                context.LabelId,
                SensitiveArtifactKind.ManagedWorkspaceFile,
                sessionId: null,
                CampaignId);

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = """
                INSERT INTO artifact_sensitivity (
                    LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                    ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId, ArtifactRevision,
                    ArtifactContentDigest, SensitivityDigest, ArtifactLabelDigest, CreatedAtUtc)
                VALUES ($labelId, 12, $artifactId, 1, 1, $generations, NULL, NULL, $campaignId, NULL, 0,
                    $contentDigest, $sensitivityDigest, $labelDigest, '2026-08-16T00:00:00Z');
                """;

            _ = command.Parameters.AddWithValue("$labelId", Format(context.LabelId));

            _ = command.Parameters.AddWithValue("$artifactId", Format(context.ArtifactId));

            _ = command.Parameters.AddWithValue(
                "$generations",
                CovenantOperationGateFixture.DatasetGeneration.ToByteArray());

            _ = command.Parameters.AddWithValue("$campaignId", Format(CampaignId));

            _ = command.Parameters.AddWithValue(
                "$contentDigest",
                label.ArtifactContentDigest.Bytes.ToArray());

            _ = command.Parameters.AddWithValue(
                "$sensitivityDigest",
                label.SensitivityDigest.Bytes.ToArray());

            _ = command.Parameters.AddWithValue("$labelDigest", label.LabelDigest.Bytes.ToArray());

            _ = await command.ExecuteNonQueryAsync(Token);

        }

    }

    /// <summary>
    /// A connection source the reconciler must never reach for.
    /// </summary>
    /// <remarks>
    /// The stopped-host overloads take the caller's own already-initialized connection. Resolving one
    /// through the pooled source would go through the database context, which is precisely what
    /// pre-readiness code has no business opening, so reaching it here is a test failure rather than a
    /// fallback.
    /// </remarks>
    private sealed class UnreachableConnectionSource : ICovenantConnectionSource
    {

        public ValueTask<SqliteConnection> GetOpenConnectionAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The stopped-host reconciliation must use the caller's connection.");

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The stopped-host reconciliation must use the caller's connection.");

    }

    /// <summary>
    /// A durable active store that records every publication and advances the envelope revision.
    /// </summary>
    /// <remarks>
    /// It is deliberately not a no-op double. The reconciler's whole safety argument is that every
    /// publication supersedes the proof bound to the revision it replaced, so a store that did not move
    /// the revision would let a stale authority keep answering and the suite would prove nothing.
    /// </remarks>
    private sealed class RecordingActiveStore(
        string guardedRoot,
        InstallationResetActivePublication initial) : IInstallationResetActiveStore
    {

        private readonly List<FullInstallationResetManagedFileReconciliationPhase> _phases = [];

        public string GuardedRoot { get; } = guardedRoot;

        internal InstallationResetActivePublication Current { get; private set; } = initial;

        internal IReadOnlyList<FullInstallationResetManagedFileReconciliationPhase>
            PublishedManagedPhases => _phases;

        /// <summary>Publishes an unrelated revision, exactly as another writer would.</summary>
        internal void AdvanceOutOfBand() =>
            Current = Bump(Current, Current.Payload);

        public Task<Result<InstallationResetActiveRecoveryState>> RecoverAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default)
        {

            ArgumentNullException.ThrowIfNull(heldInstallationLock);

            heldInstallationLock.AssertHeldFor(GuardedRoot);

            return Task.FromResult(
                Result<InstallationResetActiveRecoveryState>.Success(
                    new InstallationResetActiveRecoveryState(
                        InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
                        Current,
                        LegacyRecord: null)));

        }

        public Task<Result<InstallationResetActivePublication>> AdvanceAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication current,
            InstallationResetActiveRecord next,
            CancellationToken cancellationToken = default)
        {

            ArgumentNullException.ThrowIfNull(heldInstallationLock);

            ArgumentNullException.ThrowIfNull(next);

            heldInstallationLock.AssertHeldFor(GuardedRoot);

            if (current.EnvelopeDigest != Current.EnvelopeDigest)
            {

                return Task.FromResult(
                    Result<InstallationResetActivePublication>.Failure(
                        new Error(
                            ErrorCodes.Data.RecoveryRequired,
                            "The publication is not the current one.")));

            }

            if (next.HostToolsMarkerPairReset?.ManagedFile is { } managed)
            {

                _phases.Add(managed.Phase);

            }

            Current = Bump(Current, InstallationResetActivePayloadV2.FromRecord(next));

            return Task.FromResult(
                Result<InstallationResetActivePublication>.Success(Current));

        }

        public Task<Result<InstallationResetActivePublication>> BeginAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetActiveRecoveryState>> InspectAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetActivePublication>> MigrateLegacyV1Async(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord expectedRecord,
            FileHandleIdentity expectedIdentity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> RetireAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> CompleteStartupCleanupAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static InstallationResetActivePublication Bump(
            InstallationResetActivePublication current,
            InstallationResetActivePayloadV2 payload)
        {

            CovenantDigest envelopeDigest = new(
                [.. Enumerable.Repeat(
                    checked((byte)(0x20 + current.Envelope.Revision)),
                    32)]);

            return current with
            {
                Envelope = current.Envelope with
                {
                    Revision = current.Envelope.Revision + 1,
                    PreviousEnvelopeDigest = current.EnvelopeDigest,
                },
                EnvelopeDigest = envelopeDigest,
                Payload = payload,
                Anchor = current.Anchor with
                {
                    Revision = current.Anchor.Revision + 1,
                    EnvelopeDigest = envelopeDigest,
                },
            };

        }

    }

}
