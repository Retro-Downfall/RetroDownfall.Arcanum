using System.Security.Cryptography;

using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// How a selective import decides which Campaign an archived Session lands in (§10.19.12).
/// </summary>
/// <remarks>
/// The interesting case is the archived Campaign nobody mapped. Dropping the binding would import a
/// Session whose standing Campaign instructions silently stop applying to it, and guessing one would
/// attach it to a Campaign the operator never chose — so the only supported answer is a refusal that
/// says which archived Campaign is unaccounted for.
/// </remarks>
public sealed class BackupSessionImportPlannerTests : IDisposable
{

    // Hex letters in every identity the archive stores, because an all-digit Guid renders identically
    // in both cases and a fixture built from one cannot tell a normalised comparison from a broken
    // one. These four used to be 1111…, 9999…, 2222… and 3333…, which is half of why this suite was
    // green over a planner that could not read a real backup at all.
    private static readonly Guid BoundSessionId =
        Guid.Parse("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");

    private static readonly Guid UnboundSessionId =
        Guid.Parse("f0e1d2c3-b4a5-4968-8778-695a4b3c2d1e");

    private static readonly Guid ArchivedCampaignId =
        Guid.Parse("bead1e55-c0de-4fab-9dec-ade1facec0de");

    private static readonly Guid DestinationCampaignId =
        Guid.Parse("d0c0ffee-baba-4d0d-8fee-1ceb00dac0fe");

    private static readonly Guid UserEntryId =
        Guid.Parse("b7c8d9ea-1f20-4a31-8b42-c53d64e75f86");

    private static readonly Guid AssistantEntryId =
        Guid.Parse("c8d9ea1f-2031-4b42-8c53-d64e75f86a97");

    private static readonly Guid AttachmentId =
        Guid.Parse("d9ea1f20-3142-4c53-8d64-e75f86a97b08");

    private static readonly Guid ReservationId =
        Guid.Parse("ea1f2031-4253-4d64-8e75-f86a97b08c19");

    // The owner segment is the Session in "N" form, exactly as SessionAttachmentStore writes it.
    private static readonly string AttachmentRelativePath =
        UnboundSessionId.ToString("N") + "/note/v1/note.bin";

    // A real 64-character digest, because the manifest preimage carries the attachment's content hash
    // and a placeholder shorter than that is silently replaced with thirty-two zero bytes.
    private static readonly string AttachmentContentSha256 =
        Convert.ToHexString(SHA256.HashData("attachment bytes"u8));

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-import-planner-" + Guid.NewGuid().ToString("N"));

    public BackupSessionImportPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task An_unmapped_Campaign_bound_Session_names_the_Campaign_rather_than_arriving_unbound()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        Result<ImportedSessionTransferRequest> planned = await BackupSessionImportPlanner.PlanAsync(
            lease,
            BoundSessionId,
            [],
            CancellationToken.None);

        Assert.True(planned.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.CampaignBindingConflict, planned.Error.Code);

        // The archived Campaign, by identity. It is the only thing the operator can put on the left of
        // the mapping they now have to supply, and a refusal that withheld it would leave them guessing.
        Assert.Contains(
            ArchivedCampaignId.ToString("D"),
            planned.Error.Message,
            StringComparison.Ordinal);

        Assert.Contains("--map-campaign", planned.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task An_explicit_mapping_binds_the_import_to_the_destination_Campaign()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        Result<ImportedSessionTransferRequest> planned = await BackupSessionImportPlanner.PlanAsync(
            lease,
            BoundSessionId,
            [new BackupSessionCampaignMapping(ArchivedCampaignId, DestinationCampaignId)],
            CancellationToken.None);

        Assert.True(planned.IsSuccess);

        BackupSessionCampaignMapping mapping = Assert.IsType<BackupSessionCampaignMapping>(
            planned.Value.CampaignMapping);

        Assert.Equal(ArchivedCampaignId, mapping.SourceCampaignId);

        Assert.Equal(DestinationCampaignId, mapping.DestinationCampaignId);

        // The digest, not just the echoed argument. This is the value the transfer store scopes the
        // whole import by, and it is Campaign-scoped precisely because a mapping exists — a binding
        // computed as Global here would hand an exclusive owner a plan the request does not describe.
        Assert.Equal(
            ProtectedSessionTransferDigests.DestinationBinding(
                CovenantScope.Campaign,
                DestinationCampaignId,
                ArchivedCampaignId),
            planned.Value.DestinationBindingDigest);

    }

    [Fact]
    public async Task A_mapping_for_some_other_archived_Campaign_does_not_satisfy_this_one()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        Result<ImportedSessionTransferRequest> planned = await BackupSessionImportPlanner.PlanAsync(
            lease,
            BoundSessionId,
            [
                new BackupSessionCampaignMapping(
                    Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    DestinationCampaignId),
            ],
            CancellationToken.None);

        Assert.True(planned.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.CampaignBindingConflict, planned.Error.Code);

        Assert.Contains(
            ArchivedCampaignId.ToString("D"),
            planned.Error.Message,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_Session_that_was_never_Campaign_bound_needs_no_mapping_at_all()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        Result<ImportedSessionTransferRequest> planned = await BackupSessionImportPlanner.PlanAsync(
            lease,
            UnboundSessionId,
            [],
            CancellationToken.None);

        Assert.True(planned.IsSuccess);

        Assert.Null(planned.Value.CampaignMapping);

        // Global scope and no identities on either side, which is a different preimage from every
        // Campaign-scoped binding — an unbound import and a bound one can never produce one owner.
        Assert.Equal(
            ProtectedSessionTransferDigests.DestinationBinding(
                CovenantScope.Global,
                destinationCampaignId: null,
                sourceCampaignId: null),
            planned.Value.DestinationBindingDigest);

    }

    [Fact]
    public async Task The_coverage_pass_refuses_the_whole_selection_when_one_Session_is_unmapped()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        // Ordered so the mapped Session comes first: a check that only looked at the Session it was
        // about to commit would let this one through and refuse on the second, after the first landed.
        Result coverage = await BackupSessionImportPlanner.ValidateCampaignCoverageAsync(
            lease.Snapshot,
            [UnboundSessionId, BoundSessionId],
            [],
            CancellationToken.None);

        Assert.True(coverage.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.CampaignBindingConflict, coverage.Error.Code);

        Assert.Contains(
            ArchivedCampaignId.ToString("D"),
            coverage.Error.Message,
            StringComparison.Ordinal);

        Assert.Contains(
            BoundSessionId.ToString("D"),
            coverage.Error.Message,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task The_coverage_pass_accepts_a_selection_whose_every_binding_is_mapped()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        Result coverage = await BackupSessionImportPlanner.ValidateCampaignCoverageAsync(
            lease.Snapshot,
            [BoundSessionId, UnboundSessionId],
            [new BackupSessionCampaignMapping(ArchivedCampaignId, DestinationCampaignId)],
            CancellationToken.None);

        Assert.True(coverage.IsSuccess);

    }

    /// <summary>
    /// The plan describes the graph the archive actually holds, not an empty one.
    /// </summary>
    /// <remarks>
    /// Six of the planner's eight identity comparisons assemble this: the Entry manifest, the
    /// attachment manifest, the finalization manifest, and the three counts beside them. Every one of
    /// them bound the lowercase rendering of a <see cref="Guid"/> against a column an archive spells
    /// uppercase, so all six returned nothing and the plan committed to a manifest and a count vector
    /// describing a Session with no content at all — which the transfer store then refused as a
    /// mismatch, if it was ever reached.
    ///
    /// <para>The counts are asserted individually rather than through the digest, because a digest
    /// comparison says only that something differs. The manifest is asserted against the exact ordered
    /// preimages the archive's rows produce, rebuilt here from what the fixture seeded — an assertion
    /// that it merely differs from the empty manifest survives any single read still returning nothing,
    /// because the other two keep the item set non-empty.</para>
    /// </remarks>
    [Fact]
    public async Task A_plan_over_an_ordinary_archive_counts_the_graph_that_archive_holds()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        Result<ImportedSessionTransferRequest> planned = await BackupSessionImportPlanner.PlanAsync(
            lease,
            UnboundSessionId,
            [],
            CancellationToken.None);

        Assert.True(planned.IsSuccess, planned.IsFailure ? planned.Error.Message : string.Empty);

        Assert.Equal(1UL, planned.Value.ManifestCounts.Sessions);

        Assert.Equal(2UL, planned.Value.ManifestCounts.Entries);

        Assert.Equal(1UL, planned.Value.ManifestCounts.Attachments);

        Assert.Equal(1UL, planned.Value.ManifestCounts.AttachmentBlobs);

        Assert.Equal(1UL, planned.Value.ManifestCounts.Finalizations);

        // The manifest is what the transfer store recomputes and refuses a mismatch on, and each of
        // the three reads contributes its own preimages to it. Pinned item by item, because one read
        // returning nothing while the other two still return rows leaves a manifest that is wrong and
        // non-empty — which is the shape a weaker assertion here let through.
        Assert.Equal(
            ProtectedSessionTransferDigests.Manifest(
            [
                Preimage($"entry:{Stored(UserEntryId)}:1"),
                Preimage($"entry:{Stored(AssistantEntryId)}:2"),
                Preimage($"attachment:{AttachmentRelativePath}:{AttachmentContentSha256}:16"),
                Preimage($"finalization:{Stored(AssistantEntryId)}"),
            ]),
            planned.Value.SourceManifestDigest);

    }

    private static byte[] Preimage(string item) => Encoding.UTF8.GetBytes(item);

    private async Task<ImportedSessionSourceLease> OpenSourceAsync()
    {

        string secret = await SeedSourceAsync();

        string attachments = Path.Combine(_root, "attachments");

        Directory.CreateDirectory(attachments);

        return ImportedSessionSourceLease.Adopt(
            await BackupRestoreDatabaseWorker.OpenAsync(
                Path.Combine(_root, "arcanum.db"),
                secret,
                readOnly: true,
                CancellationToken.None),
            attachments);

    }

    private async Task<string> SeedSourceAsync()
    {

        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        string databasePath = Path.Combine(_root, "arcanum.db");

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        GrimoireKdfSidecarFile.Write(databasePath, sidecar);

        byte[] salt = sidecar.GetSaltBytes();

        string passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(secret, salt);

        CryptographicOperations.ZeroMemory(salt);

        SqliteNativeRuntime.Instance.Initialize();

        await using (SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            new SqliteConnectionStringBuilder
            {

                DataSource = databasePath,

                Password = passphrase,

                Pooling = false,

            }.ToString(),
            CancellationToken.None))
        {

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

            const string emptyJson = "{}";

            await using SqliteCommand seed = connection.CreateCommand();

            seed.CommandText = $"""
                INSERT INTO "Campaigns"
                    ("Id", "Name", "NameLower", "Path", "Type", "Description", "Settings",
                     "SanctumConfigJson", "CreatedAt", "UpdatedAt")
                VALUES ('{Stored(ArchivedCampaignId)}', 'Alpha', 'alpha', '/archived/alpha', 0, NULL,
                        '{emptyJson}', '{emptyJson}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('{Stored(BoundSessionId)}', '{Stored(ArchivedCampaignId)}', 'Bound session',
                        'active', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('{Stored(UnboundSessionId)}', NULL, 'Unbound session', 'active',
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                       "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
                VALUES ('{Stored(UserEntryId)}', '{Stored(UnboundSessionId)}', 0, 'ask', '',
                        '2026-01-01T00:00:00Z', 1, NULL, NULL, NULL, 0);

                INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                       "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
                VALUES ('{Stored(AssistantEntryId)}', '{Stored(UnboundSessionId)}', 1, 'answer',
                        'model', '2026-01-01T00:00:00Z', 2, NULL, NULL, NULL, 0);

                INSERT INTO "SessionAttachments"
                    ("Id", "SessionId", "State", "LogicalKey", "OriginalFileName", "Version",
                     "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                     "SourceKind", "SourceStatus", "EncryptionVersion")
                VALUES ('{AttachmentId:D}', '{UnboundSessionId:D}', 'Bound', 'note',
                        'note.txt', 1, '{AttachmentRelativePath}', '{AttachmentContentSha256}',
                        'text/plain', 16, 'Text', '2026-01-01T00:00:00Z', 'WorkspaceFile',
                        'Refreshable', 0);
                """;

            _ = await seed.ExecuteNonQueryAsync();

            await SeedCommittedFinalizationAsync(connection);

            await PinStoredSpellingAsync(connection);

        }

        return secret;

    }

    /// <summary>
    /// One committed finalization, with the consumed capacity slot the schema requires beside it.
    /// </summary>
    /// <remarks>
    /// The finalization guard refuses a row with no consumed reservation for the same Session and
    /// assistant identity, and that reservation can only be minted inside an authorized turn-capacity
    /// scope — so an archive that holds a finalization holds both. Seeded here rather than in the
    /// statement above because that scope has to be opened around the insert.
    /// </remarks>
    private static async Task SeedCommittedFinalizationAsync(SqliteConnection connection)
    {

        using (CovenantSqliteAuthorizationScope capacity = CovenantSqliteConnectionInitializer.Instance
            .Authorize(connection, CovenantSqliteAuthorizationKind.TurnCapacityMutation))
        {

            await using SqliteCommand reservation = connection.CreateCommand();

            reservation.CommandText = $"""
                INSERT INTO assistant_finalization_capacity_reservations (
                    ReservationId, SessionId, AssistantEntryId, OriginCode, ClaimId, StateCode,
                    CreatedAtUtc, StateChangedAtUtc)
                VALUES ('{Stored(ReservationId)}', '{Stored(UnboundSessionId)}',
                        '{Stored(AssistantEntryId)}', 2, NULL, 2,
                        '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                """;

            _ = await reservation.ExecuteNonQueryAsync();

        }

        await using SqliteCommand finalization = connection.CreateCommand();

        finalization.CommandText = $"""
            INSERT INTO assistant_entry_finalizations (
                AssistantEntryId, SessionId, OutcomeCode, ContentSensitivityCode,
                ContentSensitivityDigest, RequestDigest, FinalReceiptDigest, SourceEvidenceDigest,
                FinalizedAtUtc)
            VALUES ('{Stored(AssistantEntryId)}', '{Stored(UnboundSessionId)}', 1, 0,
                    zeroblob(32), zeroblob(32), NULL, NULL, '2026-01-01T00:00:00.0000000Z');
            """;

        _ = await finalization.ExecuteNonQueryAsync();

    }

    /// <summary>
    /// The spelling an ordinary archive holds, which is not the one a <see cref="Guid"/> renders.
    /// </summary>
    /// <remarks>
    /// <c>"Sessions"."Id"</c>, <c>"Campaigns"."Id"</c> and <c>"Entries"."SessionId"</c> come from a
    /// <see cref="Guid"/> property mapped to TEXT, and the object-relational writer stores one as
    /// uppercase dashed text; <c>assistant_entry_finalizations</c> is bound as a <see cref="Guid"/> by
    /// the turn-commit writer and the provider renders it the same way. This fixture used to seed the
    /// lowercase form instead — the one the planner's own comparisons bound — so every case here
    /// agreed with those comparisons by accident, and the suite structurally could not have caught a
    /// planner that refuses every real backup.
    ///
    /// <para>Deliberately not applied to <c>"SessionAttachments"</c>. All three of that table's
    /// writers render the identity with <c>ToString()</c>, so an archive holds the lowercase form
    /// there, and seeding it uppercase would be this suite inventing a spelling production does not
    /// write in order to make two comparisons look broken that are not.</para>
    /// </remarks>
    private static string Stored(Guid value) => value.ToString("D").ToUpperInvariant();

    /// <summary>
    /// Proves the archive this suite plans against is spelled the way an archive is spelled.
    /// </summary>
    /// <remarks>
    /// Read back out of the rows rather than asserted about <see cref="Stored"/>, and both halves
    /// matter. Uppercase alone would pass vacuously if these identities ever lost their hex letters,
    /// because an all-digit Guid renders the same in either case — so the inequality against the
    /// default rendering is what keeps this suite exercising the mismatch rather than describing it.
    /// </remarks>
    private static async Task PinStoredSpellingAsync(SqliteConnection connection)
    {

        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = """
            SELECT "Id" FROM "Sessions" WHERE "Title" = 'Bound session';
            """;

        string stored = (string)(await read.ExecuteScalarAsync())!;

        Assert.Equal(stored.ToUpperInvariant(), stored);

        Assert.NotEqual(BoundSessionId.ToString("D"), stored);

    }

}
