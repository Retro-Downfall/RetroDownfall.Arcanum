using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The one managed-file erasure kernel, against real files and a real SQLCipher catalog.
/// </summary>
/// <remarks>
/// The interesting outcomes are the two the deleter must never confuse. A file that is provably gone
/// from the exact recorded location is <c>AlreadyAbsent</c>; a file whose parent, identity, or content
/// does not match what the producer recorded is a manual blocker, and the file, its producer row, and
/// its label are all left exactly as found (§10.17).
/// </remarks>
public sealed class CovenantManagedFileErasureKernelTests
{

    private static CancellationToken Token => CancellationToken.None;

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    /// <summary>
    /// Every filesystem-outcome code is a literal that a durable row already carries, so a
    /// renumbering has to fail here rather than reinterpret rows an earlier build wrote.
    /// </summary>
    [Fact]
    public void Managed_file_outcome_codes_are_literal_and_exhaustive()
    {

        Assert.Equal(1, (byte)ManagedFileVerification.Match);

        Assert.Equal(2, (byte)ManagedFileVerification.Mismatch);

        Assert.Equal(2, Enum.GetValues<ManagedFileVerification>().Length);

        Assert.Equal(1, (byte)ManagedFileCompareDeleteResult.Deleted);

        Assert.Equal(2, (byte)ManagedFileCompareDeleteResult.Mismatch);

        Assert.Equal(2, Enum.GetValues<ManagedFileCompareDeleteResult>().Length);

        Assert.Equal(1, (byte)ManagedFileOpenKind.Opened);

        Assert.Equal(2, (byte)ManagedFileOpenKind.Absent);

        Assert.Equal(3, (byte)ManagedFileOpenKind.Mismatch);

        Assert.Equal(3, Enum.GetValues<ManagedFileOpenKind>().Length);

    }

    [Fact]
    public void Local_erasure_recovery_outcome_codes_are_literal_and_exhaustive()
    {

        Assert.Equal(1, (byte)CovenantLocalErasureStartupRecoveryOutcome.NoActiveWork);

        Assert.Equal(2, (byte)CovenantLocalErasureStartupRecoveryOutcome.ReconciledReady);

        Assert.Equal(3, (byte)CovenantLocalErasureStartupRecoveryOutcome.ManualEvidenceReady);

        Assert.Equal(4, (byte)CovenantLocalErasureStartupRecoveryOutcome.Blocked);

        Assert.Equal(4, Enum.GetValues<CovenantLocalErasureStartupRecoveryOutcome>().Length);

    }

    [Fact]
    public void The_evidence_codec_round_trips_both_locations_and_refuses_a_write_location_as_a_target()
    {

        ManagedFileDurableLocationEvidence target = new(
            CovenantOperationGateFixture.Digest(3),
            pathRevision: 4,
            ["notes", "deep"],
            CovenantOperationGateFixture.Digest(5),
            "answer.md");

        ManagedFileWriteDurableLocationEvidence write = new(target, "answer.md.tmp");

        Assert.Equal(
            target,
            ManagedFileEvidenceCodec.DecodeLocation(ManagedFileEvidenceCodec.EncodeLocation(target)).Value);

        Assert.Equal(
            write,
            ManagedFileEvidenceCodec.DecodeWriteLocation(ManagedFileEvidenceCodec.EncodeWriteLocation(write)).Value);

        // Neither framing may be read as the other: a producer row decoded as an erasure location would
        // hand the deleter a target nobody verified.
        Assert.True(ManagedFileEvidenceCodec
            .DecodeLocation(ManagedFileEvidenceCodec.EncodeWriteLocation(write))
            .IsFailure);

        Assert.True(ManagedFileEvidenceCodec
            .DecodeWriteLocation(ManagedFileEvidenceCodec.EncodeLocation(target))
            .IsFailure);

    }

    [Fact]
    public void A_managed_write_location_cannot_stage_through_its_own_target_leaf()
    {

        ManagedFileDurableLocationEvidence target = new(
            CovenantOperationGateFixture.Digest(3),
            pathRevision: 1,
            [],
            CovenantOperationGateFixture.Digest(5),
            "answer.md");

        _ = Assert.Throws<ArgumentException>(() =>
            new ManagedFileWriteDurableLocationEvidence(target, "answer.md"));

    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("trailing.")]
    public void A_location_component_is_always_one_ordinary_relative_name(string component) =>
        _ = Assert.Throws<ArgumentException>(() =>
            new ManagedFileDurableLocationEvidence(
                CovenantOperationGateFixture.Digest(3),
                pathRevision: 1,
                [],
                CovenantOperationGateFixture.Digest(5),
                component));

    [Fact]
    public async Task A_verified_file_is_deleted_its_producer_is_erased_and_its_label_removed_in_order()
    {

        await using ManagedFileFixture fixture = await ManagedFileFixture.CreateAsync();

        SeededManagedFile file = await fixture.SeedAdoptedFileAsync("answer.md", "covenant derived bytes");

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
            Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority
            .ForExclusive(lease, CovenantExclusiveOperation.CovenantFamilyReinitialize)
            .Value;

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.EraseAsync(
            file.Request(Guid.NewGuid(), Guid.NewGuid()),
            authority,
            Token);

        Assert.True(erased.IsSuccess);

        Assert.Equal(CovenantErasureBlocker.None, erased.Value.Blocker);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.False(File.Exists(file.FullPath));

        Assert.Equal(10, await fixture.CountAsync(
            $"SELECT PhaseCode FROM managed_file_write_intents WHERE WriteOperationId = '{Format(file.WriteOperationId)}';"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

        Assert.Equal(3, await fixture.CountAsync("SELECT StateCode FROM local_erasure_work_items;"));

    }

    [Fact]
    public async Task An_already_absent_file_is_idempotent_and_still_completes_its_producer()
    {

        await using ManagedFileFixture fixture = await ManagedFileFixture.CreateAsync();

        SeededManagedFile file = await fixture.SeedAdoptedFileAsync("gone.md", "bytes that were removed");

        File.Delete(file.FullPath);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.EraseAsync(
            file.Request(Guid.NewGuid(), Guid.NewGuid()),
            CovenantArtifactErasureAuthority.ForExclusive(lease, CovenantExclusiveOperation.CovenantReset).Value,
            Token);

        Assert.True(erased.IsSuccess);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.Equal(1, await fixture.CountAsync(
            "SELECT DeletionEvidenceCode FROM local_erasure_work_items;"));

    }

    [Fact]
    public async Task A_content_mismatch_leaves_the_file_the_producer_row_and_the_label_untouched()
    {

        await using ManagedFileFixture fixture = await ManagedFileFixture.CreateAsync();

        SeededManagedFile file = await fixture.SeedAdoptedFileAsync("swapped.md", "the bytes Arcanum wrote");

        await File.WriteAllTextAsync(file.FullPath, "bytes somebody else wrote", Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.HealthyCatalogFactoryErasure),
            Token)).Value;

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.EraseAsync(
            file.Request(Guid.NewGuid(), Guid.NewGuid()),
            CovenantArtifactErasureAuthority
                .ForExclusive(lease, CovenantExclusiveOperation.HealthyCatalogFactoryErasure)
                .Value,
            Token);

        Assert.True(erased.IsSuccess);

        Assert.Equal(CovenantErasureBlocker.ManualOwnershipMismatch, erased.Value.Blocker);

        Assert.True(File.Exists(file.FullPath));

        Assert.Equal(7, await fixture.CountAsync(
            $"SELECT PhaseCode FROM managed_file_write_intents WHERE WriteOperationId = '{Format(file.WriteOperationId)}';"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

        Assert.Equal(4, await fixture.CountAsync("SELECT StateCode FROM local_erasure_work_items;"));

    }

    [Fact]
    public async Task A_stale_source_revision_never_reaches_a_work_item_or_the_filesystem()
    {

        await using ManagedFileFixture fixture = await ManagedFileFixture.CreateAsync();

        SeededManagedFile file = await fixture.SeedAdoptedFileAsync("guarded.md", "protected bytes");

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        CovenantManagedFileErasureRequest forged = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            file.WriteOperationId,
            file.ArtifactId,
            file.LabelId,
            expectedSourceWriteRevision: 99);

        Result<CovenantArtifactErasureProgress> erased = await fixture.Kernel.EraseAsync(
            forged,
            CovenantArtifactErasureAuthority.ForExclusive(lease, CovenantExclusiveOperation.CovenantReset).Value,
            Token);

        Assert.True(erased.IsSuccess);

        Assert.True(erased.Value.IsBlocked);

        Assert.True(File.Exists(file.FullPath));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM local_erasure_work_items;"));

    }

    [Fact]
    public async Task Startup_recovery_finishes_a_crash_between_the_unlink_and_the_label_removal()
    {

        await using ManagedFileFixture fixture = await ManagedFileFixture.CreateAsync();

        SeededManagedFile file = await fixture.SeedAdoptedFileAsync("interrupted.md", "half-erased bytes");

        // Exactly the crash the work item exists for: the file is gone from disk and the database
        // still says it is owned, labelled, and live.
        await fixture.SeedPreparedWorkItemAsync(file);

        File.Delete(file.FullPath);

        CovenantLocalErasureStartupRecovery recovery = new(fixture.StateMachine);

        using ArcanumMaintenanceLockHandle heldLock = fixture.AcquireInstallationLock();

        Result<CovenantLocalErasureStartupRecoveryOutcome> recovered = await recovery.RecoverBeforeReadinessAsync(
            heldLock.Lock,
            heldLock.Directory,
            fixture.Connection,
            Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(CovenantLocalErasureStartupRecoveryOutcome.ReconciledReady, recovered.Value);

        Assert.Equal(3, await fixture.CountAsync("SELECT StateCode FROM local_erasure_work_items;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

    }

    [Fact]
    public async Task Startup_recovery_reports_no_active_work_when_nothing_is_outstanding()
    {

        await using ManagedFileFixture fixture = await ManagedFileFixture.CreateAsync();

        using ArcanumMaintenanceLockHandle heldLock = fixture.AcquireInstallationLock();

        Result<CovenantLocalErasureStartupRecoveryOutcome> recovered =
            await new CovenantLocalErasureStartupRecovery(fixture.StateMachine).RecoverBeforeReadinessAsync(
                heldLock.Lock,
                heldLock.Directory,
                fixture.Connection,
                Token);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(CovenantLocalErasureStartupRecoveryOutcome.NoActiveWork, recovered.Value);

    }

    [Fact]
    public async Task Startup_recovery_refuses_a_lock_that_guards_a_different_installation()
    {

        await using ManagedFileFixture fixture = await ManagedFileFixture.CreateAsync();

        using ArcanumMaintenanceLockHandle heldLock = fixture.AcquireInstallationLock();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CovenantLocalErasureStartupRecovery(fixture.StateMachine).RecoverBeforeReadinessAsync(
                heldLock.Lock,
                Path.Combine(Path.GetTempPath(), $"not-this-{Guid.NewGuid():N}"),
                fixture.Connection,
                Token));

    }

    private sealed record SeededManagedFile(
        Guid WriteOperationId,
        Guid ArtifactId,
        Guid LabelId,
        long Revision,
        string FullPath,
        ManagedFileDurableLocationEvidence Location,
        ManagedFileOwnershipEvidence Ownership)
    {

        internal CovenantManagedFileErasureRequest Request(Guid workItemId, Guid operationId) =>
            new(workItemId, operationId, WriteOperationId, ArtifactId, LabelId, (ulong)Revision);

    }

    private sealed class ArcanumMaintenanceLockHandle(
        RetroDownfall.Arcanum.Infrastructure.Backup.ArcanumMaintenanceLock heldLock,
        string directory) : IDisposable
    {

        internal RetroDownfall.Arcanum.Infrastructure.Backup.ArcanumMaintenanceLock Lock { get; } = heldLock;

        internal string Directory { get; } = directory;

        public void Dispose() => Lock.Dispose();

    }

    private sealed class ManagedFileFixture : IAsyncDisposable
    {

        private readonly CovenantSchemaScratchDatabase _database;

        private readonly string _workspaceRoot;

        private ManagedFileFixture(CovenantSchemaScratchDatabase database, string workspaceRoot)
        {

            _database = database;

            _workspaceRoot = workspaceRoot;

            StateMachine = new ManagedFileErasureStateMachine(
                CovenantSqliteConnectionInitializer.Instance,
                new ManagedFileCapabilityOpener(),
                new ManagedFileOwnershipVerifier(),
                TimeProvider.System);

            Kernel = new CovenantManagedFileErasureKernel(
                new FixedCovenantConnectionSource(database.Connection),
                CovenantSqliteConnectionInitializer.Instance,
                StateMachine,
                TimeProvider.System);

        }

        internal ManagedFileErasureStateMachine StateMachine { get; }

        internal CovenantManagedFileErasureKernel Kernel { get; }

        internal SqliteConnection Connection => _database.Connection;

        internal static async Task<ManagedFileFixture> CreateAsync()
        {

            CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

            string workspaceRoot = Path.Combine(Path.GetTempPath(), $"covenant-managed-{Guid.NewGuid():N}");

            try
            {

                _ = Directory.CreateDirectory(workspaceRoot);

                await database.InstallCoreObjectsAsync(
                    [
                        "Campaigns",
                        "Sessions",
                        "artifact_sensitivity",
                        "session_sensitivity_state",
                        "campaign_path_identities",
                        "managed_file_write_intents",
                        "local_erasure_work_items",

                        // The guards are the contract: they own the closed edge list, the
                        // authorization selection, and the label-before-producer-before-work-item
                        // ordering. Installing the tables without them would exercise C# alone.
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

                return new ManagedFileFixture(database, workspaceRoot);

            }
            catch
            {

                await database.DisposeAsync();

                throw;

            }

        }

        internal ArcanumMaintenanceLockHandle AcquireInstallationLock()
        {

            string directory = Path.Combine(_workspaceRoot, "installation");

            _ = Directory.CreateDirectory(directory);

            return new ArcanumMaintenanceLockHandle(
                RetroDownfall.Arcanum.Infrastructure.Backup.ArcanumMaintenanceLock.TryAcquire(directory)!,
                directory);

        }

        /// <summary>
        /// Creates a real file and the durable producer row that claims to own it, exactly as an
        /// adopted managed write leaves them.
        /// </summary>
        internal async Task<SeededManagedFile> SeedAdoptedFileAsync(string leaf, string content)
        {

            Guid campaignId = CovenantOperationGateFixture.CampaignOne;

            string campaignRoot = Path.Combine(_workspaceRoot, "campaign");

            string parent = Path.Combine(campaignRoot, "notes");

            _ = Directory.CreateDirectory(parent);

            string fullPath = Path.Combine(parent, leaf);

            await File.WriteAllTextAsync(fullPath, content, Token);

            CovenantDigest rootIdentity = PathIdentity(campaignRoot);

            CovenantDigest parentIdentity = PathIdentity(parent);

            ManagedFileDurableLocationEvidence location = new(
                rootIdentity,
                pathRevision: 1,
                ["notes"],
                parentIdentity,
                leaf);

            ManagedFileOwnershipEvidence ownership = new(
                PathIdentity(fullPath),
                new CovenantDigest(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                Encoding.UTF8.GetByteCount(content));

            Guid writeOperationId = Guid.NewGuid();

            Guid artifactId = Guid.NewGuid();

            Guid labelId = Guid.NewGuid();

            await InsertCampaignAsync(campaignId, campaignRoot, rootIdentity);

            await InsertLabelAsync(labelId, artifactId, campaignId);

            long revision = await InsertProducerAsync(
                writeOperationId,
                artifactId,
                labelId,
                new ManagedFileWriteDurableLocationEvidence(location, $"{leaf}.tmp"),
                ownership);

            return new SeededManagedFile(
                writeOperationId,
                artifactId,
                labelId,
                revision,
                fullPath,
                location,
                ownership);

        }

        internal async Task SeedPreparedWorkItemAsync(SeededManagedFile file)
        {

            await using SqliteTransaction transaction = Connection.BeginTransaction(deferred: false);

            using (CovenantSqliteConnectionInitializer.Instance.Authorize(
                Connection,
                CovenantSqliteAuthorizationKind.SensitivityRetentionPurge))
            {

                await LocalErasureWorkItemStore.InsertPreparedAsync(
                    Connection,
                    transaction,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    file.WriteOperationId,
                    file.Revision,
                    file.ArtifactId,
                    file.LabelId,
                    file.Location,
                    file.Ownership,
                    DateTimeOffset.UnixEpoch,
                    Token);

            }

            await transaction.CommitAsync(Token);

        }

        internal async Task<long> CountAsync(string sql) =>
            Convert.ToInt64(await _database.ScalarLongAsync(sql, Token), CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {

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

        private static CovenantDigest PathIdentity(string path) =>
            FileHandleIdentityInterop.TryGetPathIdentity(path, out FileHandleIdentity identity)
                ? ManagedFilePhysicalIdentity.Digest(identity)
                : throw new InvalidOperationException($"No filesystem identity for '{path}'.");

        private async Task InsertCampaignAsync(Guid campaignId, string root, CovenantDigest identity)
        {

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = """
                INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
                VALUES ($id, 'c', 'c', $root, 1, '{}', '2026-08-16T00:00:00Z', '2026-08-16T00:00:00Z');

                INSERT INTO campaign_path_identities (
                    CampaignId, PolicyVersion, Revision, DisplayPath, Depth, PhysicalIdentityDigest, UpdatedAtUtc)
                VALUES ($id, 1, 1, $root, 2, $identity, '2026-08-16T00:00:00Z');
                """;

            _ = command.Parameters.AddWithValue("$id", Format(campaignId));

            _ = command.Parameters.AddWithValue("$root", root);

            _ = command.Parameters.AddWithValue("$identity", identity.Bytes.ToArray());

            _ = await command.ExecuteNonQueryAsync(Token);

        }

        private async Task InsertLabelAsync(Guid labelId, Guid artifactId, Guid campaignId)
        {

            ArtifactSensitivityLabel label = CovenantErasureAuthorityFixture.Label(
                artifactId,
                labelId,
                SensitiveArtifactKind.ManagedWorkspaceFile,
                sessionId: null,
                campaignId);

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = """
                INSERT INTO artifact_sensitivity (
                    LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                    ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId, ArtifactRevision,
                    ArtifactContentDigest, SensitivityDigest, ArtifactLabelDigest, CreatedAtUtc)
                VALUES ($labelId, 12, $artifactId, 1, 1, $generations, NULL, NULL, $campaignId, NULL, 0,
                    $contentDigest, $sensitivityDigest, $labelDigest, '2026-08-16T00:00:00Z');
                """;

            _ = command.Parameters.AddWithValue("$labelId", Format(labelId));

            _ = command.Parameters.AddWithValue("$artifactId", Format(artifactId));

            _ = command.Parameters.AddWithValue(
                "$generations",
                CovenantOperationGateFixture.DatasetGeneration.ToByteArray());

            _ = command.Parameters.AddWithValue("$campaignId", Format(campaignId));

            _ = command.Parameters.AddWithValue("$contentDigest", label.ArtifactContentDigest.Bytes.ToArray());

            _ = command.Parameters.AddWithValue("$sensitivityDigest", label.SensitivityDigest.Bytes.ToArray());

            _ = command.Parameters.AddWithValue("$labelDigest", label.LabelDigest.Bytes.ToArray());

            _ = await command.ExecuteNonQueryAsync(Token);

        }

        /// <summary>
        /// Walks the producer row through the exact phase sequence a real managed write commits, so
        /// the erasure path is exercised against a row the writer's own guards accepted.
        /// </summary>
        private async Task<long> InsertProducerAsync(
            Guid writeOperationId,
            Guid artifactId,
            Guid labelId,
            ManagedFileWriteDurableLocationEvidence location,
            ManagedFileOwnershipEvidence ownership)
        {

            using CovenantSqliteAuthorizationScope writer = CovenantSqliteConnectionInitializer.Instance.Authorize(
                Connection,
                CovenantSqliteAuthorizationKind.ManagedFileIntentMutation);

            await using (SqliteCommand insert = Connection.CreateCommand())
            {

                insert.CommandText = """
                    INSERT INTO managed_file_write_intents (
                        WriteOperationId, StableEffectIdentityDigest, ArtifactId, SensitivityLabelId,
                        SensitivityLabelDigest, PendingArtifactSensitivityLabel, DurableLocationEvidence,
                        ExpectedContentHash, ExpectedContentLength, CreatedChildPhysicalIdentityDigest,
                        FinalOwnershipEvidence, PhaseCode, Revision, RetryCount, CreatedAtUtc, UpdatedAtUtc)
                    VALUES ($writeOperationId, $effect, $artifactId, $labelId, $labelDigest, $pendingLabel,
                        $location, $contentHash, $contentLength, NULL, NULL, 1, 0, 0,
                        '2026-08-16T00:00:00Z', '2026-08-16T00:00:00Z');
                    """;

                _ = insert.Parameters.AddWithValue("$writeOperationId", Format(writeOperationId));

                _ = insert.Parameters.AddWithValue(
                    "$effect",
                    CovenantOperationGateFixture.Digest(9).Bytes.ToArray());

                _ = insert.Parameters.AddWithValue("$artifactId", Format(artifactId));

                _ = insert.Parameters.AddWithValue("$labelId", Format(labelId));

                _ = insert.Parameters.AddWithValue(
                    "$labelDigest",
                    CovenantOperationGateFixture.Digest(10).Bytes.ToArray());

                _ = insert.Parameters.AddWithValue("$pendingLabel", new byte[] { 1, 2, 3, 4 });

                _ = insert.Parameters.AddWithValue(
                    "$location",
                    ManagedFileEvidenceCodec.EncodeWriteLocation(location));

                _ = insert.Parameters.AddWithValue("$contentHash", ownership.ContentHash.Bytes.ToArray());

                _ = insert.Parameters.AddWithValue("$contentLength", ownership.ContentLength);

                _ = await insert.ExecuteNonQueryAsync(Token);

            }

            await AdvanceProducerAsync(
                writeOperationId,
                fromPhase: 1,
                toPhase: 2,
                "CreatedChildPhysicalIdentityDigest = $childIdentity",
                command => command.Parameters.AddWithValue(
                    "$childIdentity",
                    ownership.PhysicalIdentityDigest.Bytes.ToArray()));

            for (int phase = 2; phase <= 5; phase++)
            {

                await AdvanceProducerAsync(writeOperationId, phase, phase + 1, null, null);

            }

            await AdvanceProducerAsync(
                writeOperationId,
                fromPhase: 6,
                toPhase: 7,
                "FinalOwnershipEvidence = $ownership, PendingArtifactSensitivityLabel = NULL",
                command => command.Parameters.AddWithValue(
                    "$ownership",
                    ManagedFileEvidenceCodec.EncodeOwnership(ownership)));

            return 6;

        }

        private async Task AdvanceProducerAsync(
            Guid writeOperationId,
            int fromPhase,
            int toPhase,
            string? additionalSet,
            Func<SqliteCommand, object>? bind)
        {

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = $"""
                UPDATE managed_file_write_intents
                SET PhaseCode = $toPhase,
                    Revision = Revision + 1,
                    UpdatedAtUtc = '2026-08-16T00:00:00Z'{(additionalSet is null ? string.Empty : $",\n                    {additionalSet}")}
                WHERE WriteOperationId = $writeOperationId AND PhaseCode = $fromPhase;
                """;

            _ = command.Parameters.AddWithValue("$toPhase", toPhase);

            _ = command.Parameters.AddWithValue("$fromPhase", fromPhase);

            _ = command.Parameters.AddWithValue("$writeOperationId", Format(writeOperationId));

            _ = bind?.Invoke(command);

            Assert.Equal(1, await command.ExecuteNonQueryAsync(Token));

        }

        private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    }

}
