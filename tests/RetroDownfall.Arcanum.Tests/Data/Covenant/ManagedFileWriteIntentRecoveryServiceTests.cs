using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The terminalizer for managed writes that never finished being adopted, against real files and a
/// real SQLCipher catalog.
/// </summary>
/// <remarks>
/// The two outcomes are the whole contract. <c>Cleaned</c> means every candidate child was either
/// proved absent or compare-deleted through the exact created-child physical identity the producer
/// recorded; <c>ManualNonrevocable</c> means something is on disk this operation may not touch, and it
/// is left exactly as found. The suite asserts the filesystem, the row, and the pending label
/// projection separately, because a pass that terminalized the row without removing the file would
/// look identical in the database alone.
/// </remarks>
public sealed class ManagedFileWriteIntentRecoveryServiceTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void Write_intent_recovery_outcome_codes_are_literal_and_exhaustive()
    {

        Assert.Equal(1, (byte)ManagedFileWriteIntentRecoveryOutcome.Cleaned);

        Assert.Equal(2, (byte)ManagedFileWriteIntentRecoveryOutcome.ManualNonrevocable);

        Assert.Equal(2, Enum.GetValues<ManagedFileWriteIntentRecoveryOutcome>().Length);

    }

    [Fact]
    public async Task A_temporary_child_this_operation_created_is_compare_deleted_and_the_row_terminalizes_cleaned()
    {

        await using WriteIntentFixture fixture = await WriteIntentFixture.CreateAsync();

        SeededWriteIntent seeded = await fixture.SeedAsync(
            "answer.md",
            temporaryChildContent: "partial",
            targetChildContent: null,
            ManagedFileWriteIntentPhase.TempWritten);

        Assert.True(File.Exists(seeded.TemporaryPath));

        Result<ManagedFileWriteIntentRecoveryOutcome> recovered = await fixture.Service
            .RecoverForFullInstallationResetAsync(fixture.Connection, await fixture.ReadAsync(seeded), Token);

        Assert.True(recovered.IsSuccess, recovered.Error.Message);

        Assert.Equal(ManagedFileWriteIntentRecoveryOutcome.Cleaned, recovered.Value);

        Assert.False(File.Exists(seeded.TemporaryPath));

        ManagedFileWriteIntentRow after = await fixture.ReadAsync(seeded);

        Assert.Equal(ManagedFileWriteIntentPhase.Cleaned, after.Phase);

        Assert.Equal(seeded.Revision + 1, after.Revision);

        // The projection is what adoption existed to consume. A row that will never be adopted must
        // not keep carrying it, and the table refuses a terminal row that does.
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT COUNT(*) FROM managed_file_write_intents WHERE PendingArtifactSensitivityLabel IS NOT NULL;"));

    }

    [Fact]
    public async Task A_child_already_renamed_onto_its_target_leaf_is_removed_even_though_the_phase_reads_earlier()
    {

        await using WriteIntentFixture fixture = await WriteIntentFixture.CreateAsync();

        // The rename and the compare-and-swap that records it are two effects in two systems, so a
        // row journaled at TempFsynced can already have its child sitting on the target leaf.
        SeededWriteIntent seeded = await fixture.SeedAsync(
            "answer.md",
            temporaryChildContent: null,
            targetChildContent: "renamed",
            ManagedFileWriteIntentPhase.TempFsynced);

        Assert.True(File.Exists(seeded.TargetPath));

        Result<ManagedFileWriteIntentRecoveryOutcome> recovered = await fixture.Service
            .RecoverForFullInstallationResetAsync(fixture.Connection, await fixture.ReadAsync(seeded), Token);

        Assert.True(recovered.IsSuccess, recovered.Error.Message);

        Assert.Equal(ManagedFileWriteIntentRecoveryOutcome.Cleaned, recovered.Value);

        Assert.False(File.Exists(seeded.TargetPath));

        Assert.Equal(
            ManagedFileWriteIntentPhase.Cleaned,
            (await fixture.ReadAsync(seeded)).Phase);

    }

    [Fact]
    public async Task A_row_whose_children_are_both_already_absent_terminalizes_cleaned_without_touching_anything()
    {

        await using WriteIntentFixture fixture = await WriteIntentFixture.CreateAsync();

        SeededWriteIntent seeded = await fixture.SeedAsync(
            "answer.md",
            temporaryChildContent: "gone-later",
            targetChildContent: null,
            ManagedFileWriteIntentPhase.TempCreated);

        File.Delete(seeded.TemporaryPath);

        Result<ManagedFileWriteIntentRecoveryOutcome> recovered = await fixture.Service
            .RecoverForFullInstallationResetAsync(fixture.Connection, await fixture.ReadAsync(seeded), Token);

        Assert.True(recovered.IsSuccess, recovered.Error.Message);

        Assert.Equal(ManagedFileWriteIntentRecoveryOutcome.Cleaned, recovered.Value);

        Assert.Equal(
            ManagedFileWriteIntentPhase.Cleaned,
            (await fixture.ReadAsync(seeded)).Phase);

    }

    [Fact]
    public async Task A_child_whose_physical_identity_is_not_the_recorded_one_is_left_alone_as_a_manual_orphan()
    {

        await using WriteIntentFixture fixture = await WriteIntentFixture.CreateAsync();

        SeededWriteIntent seeded = await fixture.SeedAsync(
            "answer.md",
            temporaryChildContent: "ours",
            targetChildContent: null,
            ManagedFileWriteIntentPhase.TempWritten);

        // Same name, different file. This is the substitution the created-child identity exists to
        // detect, and the only honest answer is to leave it.
        File.Delete(seeded.TemporaryPath);

        await File.WriteAllTextAsync(seeded.TemporaryPath, "somebody else's", Token);

        Result<ManagedFileWriteIntentRecoveryOutcome> recovered = await fixture.Service
            .RecoverForFullInstallationResetAsync(fixture.Connection, await fixture.ReadAsync(seeded), Token);

        Assert.True(recovered.IsSuccess, recovered.Error.Message);

        Assert.Equal(ManagedFileWriteIntentRecoveryOutcome.ManualNonrevocable, recovered.Value);

        Assert.True(File.Exists(seeded.TemporaryPath));

        Assert.Equal("somebody else's", await File.ReadAllTextAsync(seeded.TemporaryPath, Token));

        Assert.Equal(
            ManagedFileWriteIntentPhase.ManualNonrevocable,
            (await fixture.ReadAsync(seeded)).Phase);

    }

    [Fact]
    public async Task A_prepared_row_that_never_created_a_child_refuses_an_occupied_leaf_rather_than_claiming_it()
    {

        await using WriteIntentFixture fixture = await WriteIntentFixture.CreateAsync();

        SeededWriteIntent seeded = await fixture.SeedAsync(
            "answer.md",
            temporaryChildContent: null,
            targetChildContent: null,
            ManagedFileWriteIntentPhase.Prepared);

        await File.WriteAllTextAsync(seeded.TargetPath, "pre-existing", Token);

        Result<ManagedFileWriteIntentRecoveryOutcome> recovered = await fixture.Service
            .RecoverForFullInstallationResetAsync(fixture.Connection, await fixture.ReadAsync(seeded), Token);

        Assert.True(recovered.IsSuccess, recovered.Error.Message);

        Assert.Equal(ManagedFileWriteIntentRecoveryOutcome.ManualNonrevocable, recovered.Value);

        Assert.True(File.Exists(seeded.TargetPath));

        Assert.Equal("pre-existing", await File.ReadAllTextAsync(seeded.TargetPath, Token));

    }

    [Fact]
    public async Task A_prepared_row_with_both_leaves_empty_terminalizes_cleaned()
    {

        await using WriteIntentFixture fixture = await WriteIntentFixture.CreateAsync();

        SeededWriteIntent seeded = await fixture.SeedAsync(
            "answer.md",
            temporaryChildContent: null,
            targetChildContent: null,
            ManagedFileWriteIntentPhase.Prepared);

        Result<ManagedFileWriteIntentRecoveryOutcome> recovered = await fixture.Service
            .RecoverForFullInstallationResetAsync(fixture.Connection, await fixture.ReadAsync(seeded), Token);

        Assert.True(recovered.IsSuccess, recovered.Error.Message);

        Assert.Equal(ManagedFileWriteIntentRecoveryOutcome.Cleaned, recovered.Value);

    }

    [Fact]
    public async Task A_root_that_has_been_re_registered_since_the_write_is_a_manual_orphan_rather_than_a_clean()
    {

        await using WriteIntentFixture fixture = await WriteIntentFixture.CreateAsync();

        SeededWriteIntent seeded = await fixture.SeedAsync(
            "answer.md",
            temporaryChildContent: "ours",
            targetChildContent: null,
            ManagedFileWriteIntentPhase.TempWritten);

        await fixture.BumpCampaignPathRevisionAsync();

        Result<ManagedFileWriteIntentRecoveryOutcome> recovered = await fixture.Service
            .RecoverForFullInstallationResetAsync(fixture.Connection, await fixture.ReadAsync(seeded), Token);

        Assert.True(recovered.IsSuccess, recovered.Error.Message);

        Assert.Equal(ManagedFileWriteIntentRecoveryOutcome.ManualNonrevocable, recovered.Value);

        // Unresolvable is not the same as absent. The file stays exactly where it was.
        Assert.True(File.Exists(seeded.TemporaryPath));

    }

    [Fact]
    public async Task An_adopted_or_already_terminal_row_is_refused_rather_than_recovered()
    {

        await using WriteIntentFixture fixture = await WriteIntentFixture.CreateAsync();

        SeededWriteIntent seeded = await fixture.SeedAsync(
            "answer.md",
            temporaryChildContent: "ours",
            targetChildContent: null,
            ManagedFileWriteIntentPhase.TempWritten);

        ManagedFileWriteIntentRow adopted = (await fixture.ReadAsync(seeded)) with
        {
            Phase = ManagedFileWriteIntentPhase.AdoptedAndLabeled,
        };

        Assert.True(
            (await fixture.Service.RecoverForFullInstallationResetAsync(
                fixture.Connection,
                adopted,
                Token)).IsFailure);

        ManagedFileWriteIntentRow erased = adopted with
        {
            Phase = ManagedFileWriteIntentPhase.Erased,
        };

        Assert.True(
            (await fixture.Service.RecoverForFullInstallationResetAsync(
                fixture.Connection,
                erased,
                Token)).IsFailure);

        // Refused means refused: nothing was removed and nothing was written.
        Assert.True(File.Exists(seeded.TemporaryPath));

        Assert.Equal(
            ManagedFileWriteIntentPhase.TempWritten,
            (await fixture.ReadAsync(seeded)).Phase);

    }

    [Fact]
    public async Task The_inventory_reader_returns_every_row_in_canonical_identity_order()
    {

        await using WriteIntentFixture fixture = await WriteIntentFixture.CreateAsync();

        SeededWriteIntent second = await fixture.SeedAsync(
            "second.md",
            temporaryChildContent: null,
            targetChildContent: null,
            ManagedFileWriteIntentPhase.Prepared,
            Guid.Parse("00000100-0000-0000-0000-000000000000"));

        SeededWriteIntent first = await fixture.SeedAsync(
            "first.md",
            temporaryChildContent: null,
            targetChildContent: null,
            ManagedFileWriteIntentPhase.Prepared,
            Guid.Parse("00000001-0000-0000-0000-000000000000"));

        Result<IReadOnlyList<ManagedFileWriteIntentRow>> inventory =
            await ManagedFileWriteIntentStore.ListInventoryAsync(fixture.Connection, ceiling: 16, Token);

        Assert.True(inventory.IsSuccess, inventory.Error.Message);

        // Uppercase RFC-4122 text sorts exactly as the network-order bytes the vector digest commits
        // to, so the reader's ORDER BY is the canonical order rather than an approximation of it.
        Assert.Equal(
            [first.WriteOperationId, second.WriteOperationId],
            inventory.Value.Select(static row => row.WriteOperationId).ToArray());

        ImmutableArray<Guid> ordered = [.. inventory.Value.Select(static row => row.WriteOperationId)];

        Assert.True(
            RetroDownfall.Arcanum.Infrastructure.InstallationReset
                .FullInstallationResetManagedFileDigests
                .SourceWriteIntentVector(ordered)
                .IsSuccess);

    }

    [Fact]
    public async Task The_inventory_reader_reads_one_row_past_the_ceiling_so_an_oversized_inventory_is_detectable()
    {

        await using WriteIntentFixture fixture = await WriteIntentFixture.CreateAsync();

        for (int index = 1; index <= 3; index++)
        {

            _ = await fixture.SeedAsync(
                $"leaf-{index}.md",
                temporaryChildContent: null,
                targetChildContent: null,
                ManagedFileWriteIntentPhase.Prepared,
                new Guid(index, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        }

        Result<IReadOnlyList<ManagedFileWriteIntentRow>> inventory =
            await ManagedFileWriteIntentStore.ListInventoryAsync(fixture.Connection, ceiling: 2, Token);

        Assert.True(inventory.IsSuccess, inventory.Error.Message);

        Assert.Equal(3, inventory.Value.Count);

    }

    private sealed record SeededWriteIntent(
        Guid WriteOperationId,
        long Revision,
        string TemporaryPath,
        string TargetPath);

    private sealed class WriteIntentFixture : IAsyncDisposable
    {

        private static readonly Guid CampaignId = CovenantOperationGateFixture.CampaignOne;

        private readonly CovenantSchemaScratchDatabase _database;

        private readonly string _workspaceRoot;

        private readonly string _campaignRoot;

        private bool _campaignInserted;

        private WriteIntentFixture(CovenantSchemaScratchDatabase database, string workspaceRoot)
        {

            _database = database;

            _workspaceRoot = workspaceRoot;

            _campaignRoot = Path.Combine(workspaceRoot, "campaign");

            Service = new ManagedFileWriteIntentRecoveryService(
                CovenantSqliteConnectionInitializer.Instance,
                new ManagedFileCapabilityOpener(),
                new ManagedFileOwnershipVerifier(),
                TimeProvider.System);

        }

        internal ManagedFileWriteIntentRecoveryService Service { get; }

        internal SqliteConnection Connection => _database.Connection;

        internal static async Task<WriteIntentFixture> CreateAsync()
        {

            CovenantSchemaScratchDatabase database =
                await CovenantSchemaScratchDatabase.CreateAsync(Token);

            string workspaceRoot =
                Path.Combine(Path.GetTempPath(), $"arcanum-write-intent-{Guid.NewGuid():N}");

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

                        // The update guard owns the closed edge list and picks the authorization scope
                        // by phase edge. Installing the table without it would exercise C# alone.
                        "managed_file_write_intents_guard_insert",
                        "managed_file_write_intents_guard_update",
                        "managed_file_write_intents_guard_delete",
                        "local_erasure_work_items_guard_insert",
                        "local_erasure_work_items_guard_update",
                        "local_erasure_work_items_guard_delete",
                    ],
                    Token);

                return new WriteIntentFixture(database, workspaceRoot);

            }
            catch
            {

                await database.DisposeAsync();

                throw;

            }

        }

        /// <summary>
        /// Walks one write intent to the requested phase through the exact edges the guard accepts.
        /// </summary>
        /// <remarks>
        /// Never jumped to. The created-child identity is filled only on the <c>Prepared</c> to
        /// <c>TempCreated</c> edge and is immutable afterwards, so a seed that skipped ahead would be
        /// creating a row the product cannot produce and the recovery under test would be deciding
        /// against evidence no writer ever wrote.
        /// </remarks>
        internal async Task<SeededWriteIntent> SeedAsync(
            string leaf,
            string? temporaryChildContent,
            string? targetChildContent,
            ManagedFileWriteIntentPhase phase,
            Guid? writeOperationId = null)
        {

            string parent = Path.Combine(_campaignRoot, "notes");

            _ = Directory.CreateDirectory(parent);

            await InsertCampaignOnceAsync();

            string temporaryLeaf = $"{leaf}.tmp";

            string temporaryPath = Path.Combine(parent, temporaryLeaf);

            string targetPath = Path.Combine(parent, leaf);

            string? childPath = temporaryChildContent is not null
                ? temporaryPath
                : targetChildContent is not null
                    ? targetPath
                    : null;

            if (childPath is not null)
            {

                await File.WriteAllTextAsync(
                    childPath,
                    temporaryChildContent ?? targetChildContent!,
                    Token);

            }

            ManagedFileWriteDurableLocationEvidence location = new(
                new ManagedFileDurableLocationEvidence(
                    PathIdentity(_campaignRoot),
                    pathRevision: 1,
                    ["notes"],
                    PathIdentity(parent),
                    leaf),
                temporaryLeaf);

            Guid operationId = writeOperationId ?? Guid.NewGuid();

            long revision = await InsertAndWalkAsync(
                operationId,
                location,
                childPath is null ? null : PathIdentity(childPath),
                phase);

            return new SeededWriteIntent(operationId, revision, temporaryPath, targetPath);

        }

        internal async Task<ManagedFileWriteIntentRow> ReadAsync(SeededWriteIntent seeded)
        {

            Result<IReadOnlyList<ManagedFileWriteIntentRow>> inventory =
                await ManagedFileWriteIntentStore.ListInventoryAsync(Connection, ceiling: 64, Token);

            Assert.True(inventory.IsSuccess, inventory.Error.Message);

            return inventory.Value.Single(row => row.WriteOperationId == seeded.WriteOperationId);

        }

        internal async Task BumpCampaignPathRevisionAsync()
        {

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText =
                "UPDATE campaign_path_identities SET Revision = Revision + 1 WHERE CampaignId = $id;";

            _ = command.Parameters.AddWithValue("$id", Format(CampaignId));

            _ = await command.ExecuteNonQueryAsync(Token);

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

        private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

        private static CovenantDigest PathIdentity(string path) =>
            FileHandleIdentityInterop.TryGetPathIdentity(path, out FileHandleIdentity identity)
                ? ManagedFilePhysicalIdentity.Digest(identity)
                : throw new InvalidOperationException($"No filesystem identity for '{path}'.");

        private async Task InsertCampaignOnceAsync()
        {

            if (_campaignInserted)
            {

                return;

            }

            _ = Directory.CreateDirectory(_campaignRoot);

            await using SqliteCommand command = Connection.CreateCommand();

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

        private async Task<long> InsertAndWalkAsync(
            Guid writeOperationId,
            ManagedFileWriteDurableLocationEvidence location,
            CovenantDigest? createdChildPhysicalIdentityDigest,
            ManagedFileWriteIntentPhase phase)
        {

            using CovenantSqliteAuthorizationScope writer =
                CovenantSqliteConnectionInitializer.Instance.Authorize(
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
                    VALUES ($write, $effect, $artifact, $label, zeroblob(32), zeroblob(64), $location,
                        zeroblob(32), 0, NULL, NULL, 1, 0, 0,
                        '2026-08-16T00:00:00Z', '2026-08-16T00:00:00Z');
                    """;

                _ = insert.Parameters.AddWithValue("$write", Format(writeOperationId));

                _ = insert.Parameters.AddWithValue(
                    "$effect",
                    SHA256.HashData(Encoding.UTF8.GetBytes(Format(writeOperationId))));

                _ = insert.Parameters.AddWithValue("$artifact", Format(Guid.NewGuid()));

                _ = insert.Parameters.AddWithValue("$label", Format(Guid.NewGuid()));

                _ = insert.Parameters.AddWithValue(
                    "$location",
                    ManagedFileEvidenceCodec.EncodeWriteLocation(location));

                _ = await insert.ExecuteNonQueryAsync(Token);

            }

            long revision = 0;

            if (phase is ManagedFileWriteIntentPhase.Prepared)
            {

                return revision;

            }

            await using (SqliteCommand created = Connection.CreateCommand())
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
                    (createdChildPhysicalIdentityDigest
                        ?? new CovenantDigest(new byte[CovenantLimits.DigestBytes])).Bytes.ToArray());

                _ = created.Parameters.AddWithValue("$write", Format(writeOperationId));

                _ = await created.ExecuteNonQueryAsync(Token);

            }

            revision++;

            for (int next = 3; next <= (int)phase; next++)
            {

                await using SqliteCommand advance = Connection.CreateCommand();

                advance.CommandText = """
                    UPDATE managed_file_write_intents
                    SET PhaseCode = $phase,
                        Revision = Revision + 1,
                        UpdatedAtUtc = '2026-08-16T00:00:02Z'
                    WHERE WriteOperationId = $write;
                    """;

                _ = advance.Parameters.AddWithValue("$phase", next);

                _ = advance.Parameters.AddWithValue("$write", Format(writeOperationId));

                _ = await advance.ExecuteNonQueryAsync(Token);

                revision++;

            }

            return revision;

        }

    }

}
