using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The behavioural contract that replaces the source-scanning identity register: every stored
/// identity these two writers produce holds the canonical spelling the object-relational writer
/// renders, proven by driving each one through its own outermost production entry point against a
/// real encrypted database and reading the rows back out.
/// </summary>
/// <remarks>
/// <para><b>Why a production entry point, not the store's method directly.</b> A hand-assembled
/// request only ever agrees with itself. <c>BackupSessionImporter.ImportProtectedAsync</c> and
/// <c>BackupSessionImporter.ImportAsync</c> are what <c>BackupRestoreService</c> actually calls, so
/// entering there runs the planner, the compound lease, the real store and the merge loop exactly as
/// production does.</para>
///
/// <para><b>Why the unprotected merge case forces a remap.</b> The merge path's own existence gate
/// binds an unnormalised identity against an archive the object-relational writer fills uppercase, so
/// it is unreachable for a Session id that carries a hex letter — see the design note this suite's
/// sibling <see cref="BackupSessionImporterTests"/> carries on <c>SessionId</c>. Reaching the writer at
/// all therefore requires an all-digit Session id, whose own spelling cannot tell a fix from a
/// regression because upper and lower render identically. Forcing a collision in the destination makes
/// the importer mint a fresh <see cref="Guid.NewGuid()"/> for the remapped Session — a value that
/// almost certainly carries a letter — which is what actually exercises the conversion.</para>
///
/// <para><b>What is deliberately not covered.</b> This proves the writers, not the comparisons: no
/// case here depends on a read normalising anything, and neither drive method below is changed by the
/// reader-side reversions a later task in this series makes.</para>
///
/// <para><b>Why <c>SessionAttachmentStore</c> is not a third case here.</b> Every existing
/// <c>SessionAttachments</c> row is the minority form, so converting that store's writer ahead of a
/// data migration would make new rows disagree with old rows and with the
/// <c>session_attachment_chunks</c>/<c>session_attachment_index_state</c> foreign-key children that
/// key off <c>SessionAttachments.Id</c> unconditionally in whatever spelling they were given — see
/// <c>task-1-report.md</c>. That conversion, this suite's third case, and the
/// <c>SessionAttachmentIndexRepository</c> sites that would need to move with it all belong to the
/// task that also carries the data migration, so the foreign key never sees a mismatch.</para>
/// </remarks>
public sealed class IdentitySpellingContractTests
{

    /// <summary>
    /// Drives <see cref="RetroDownfall.Arcanum.Infrastructure.Repositories.ProtectedArtifactTransferStore"/>
    /// through <c>BackupSessionImporter.ImportProtectedAsync</c>, over a source that carries a
    /// committed finalization so <c>WriteImportedGuardsAsync</c>'s write is exercised too.
    /// </summary>
    /// <remarks>
    /// Say plainly what this proves and what it does not, because a green run of a test that disables
    /// a guard is exactly the shape this whole defect family has hidden inside before. This exact
    /// finalization write cannot succeed against a live, undropped destination schema today — the
    /// harness drops <c>assistant_entry_finalizations_validate_insert</c> first (see
    /// <see cref="IdentitySpellingHarness.DropFinalizationCapacityGuardAsync"/>) because the write has
    /// no way to satisfy it from outside. So this case proves the identity that write lands is
    /// rendered canonically once the write is allowed to happen. It proves nothing about whether a
    /// protected import of a Session with a committed assistant turn can actually commit against a
    /// real, undropped installation — that is a separate, pre-existing, spelling-unrelated gap the
    /// design spec already records as out of scope (§9), and this test is not evidence either way.
    /// </remarks>
    [SkippableFact]
    public async Task The_protected_transfer_store_writes_only_canonical_identities()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using IdentitySpellingHarness harness = await IdentitySpellingHarness.CreateAsync();

        await harness.CommitImportedSessionThroughTheStoreAsync();

        Assert.Empty(await harness.NonCanonicalAsync());

    }

    [SkippableFact]
    public async Task The_backup_session_importer_writes_only_canonical_identities()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using IdentitySpellingHarness harness = await IdentitySpellingHarness.CreateAsync();

        await harness.ImportSessionThroughTheUnprotectedMergeAsync();

        Assert.Empty(await harness.NonCanonicalAsync());

    }

}

/// <summary>
/// One offending row: the table and column it was found in, the exact value stored, and which half
/// of "canonical" it failed — wrong case, wrong shape (not 36 characters with dashes at 8/13/18/23),
/// or both.
/// </summary>
internal readonly record struct NonCanonicalIdentity(string Table, string Column, string Value, string Reason);

/// <summary>
/// Owns whichever real encrypted database a drive method populates, and can read every identity
/// column this family covers back out of it afterwards.
/// </summary>
/// <remarks>
/// One harness drives exactly one component per test, so it only ever tracks one destination to
/// scan: the KDF-sidecar-backed Grimoire a backup import writes into. <see cref="NonCanonicalAsync"/>
/// reads the same fixed column list from whichever drive method populated it.
/// </remarks>
internal sealed class IdentitySpellingHarness : IAsyncDisposable
{

    /// <summary>
    /// Every identity column any of the three converted writers can reach, table-qualified. Not the
    /// full defect-family register — just the columns these three components fill, which is exactly
    /// what a regression in one of them would corrupt.
    /// </summary>
    private static readonly (string Table, string Column)[] IdentityColumns =
    [

        ("Sessions", "Id"),

        ("Sessions", "CampaignId"),

        ("Entries", "Id"),

        ("Entries", "SessionId"),

        ("SessionAttachments", "Id"),

        ("SessionAttachments", "SessionId"),

        ("SessionAttachments", "EntryId"),

        ("assistant_entry_finalizations", "AssistantEntryId"),

        ("assistant_entry_finalizations", "SessionId"),

    ];

    private readonly string _root;

    private string? _kdfDatabasePath;

    private string? _kdfSecret;

    private IdentitySpellingHarness(string root) => _root = root;

    public static Task<IdentitySpellingHarness> CreateAsync() =>
        Task.FromResult(
            new IdentitySpellingHarness(
                Directory.CreateDirectory(
                    Path.Combine(
                        Path.GetTempPath(),
                        "arcanum-identity-spelling-" + Guid.NewGuid().ToString("N"))).FullName));

    /// <summary>
    /// Drives <see cref="ProtectedArtifactTransferStore"/> through
    /// <see cref="BackupSessionImporter.ImportProtectedAsync"/>, over an archive written the way the
    /// object-relational writer actually writes one — canonical uppercase, a Campaign binding included
    /// so the destination-Campaign rendering is exercised too — for a Session id that carries a hex
    /// letter, so a spelling defect at any of the sites this component fills has somewhere to show up.
    /// </summary>
    public async Task CommitImportedSessionThroughTheStoreAsync(
        CancellationToken cancellationToken = default)
    {

        string sourceRoot = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;

        string destinationRoot = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;

        string sourceAttachments = Directory.CreateDirectory(
            Path.Combine(sourceRoot, "attachments")).FullName;

        string destinationAttachments = Directory.CreateDirectory(
            Path.Combine(destinationRoot, "attachments")).FullName;

        string sourceSecret = await CreateGrimoireDatabaseAsync(sourceRoot, cancellationToken)
            .ConfigureAwait(false);

        string destinationSecret = await CreateGrimoireDatabaseAsync(destinationRoot, cancellationToken)
            .ConfigureAwait(false);

        await DropFinalizationCapacityGuardAsync(destinationRoot, destinationSecret, cancellationToken)
            .ConfigureAwait(false);

        Guid archivedSessionId = Guid.Parse("a6b5c4d3-e2f1-4098-8765-4a3b2c1d0e9f");

        Guid sourceCampaignId = Guid.Parse("caff1e00-1111-4a2b-8c3d-4e5f60718293");

        Guid destinationCampaignId = Guid.NewGuid();

        string session = archivedSessionId.ToString("D").ToUpperInvariant();

        string campaign = sourceCampaignId.ToString("D").ToUpperInvariant();

        string entryOne = Guid.Parse("b7c8d9ea-1f20-4a31-8b42-c53d64e75f86").ToString("D").ToUpperInvariant();

        string assistantEntryId =
            Guid.Parse("f00d1e57-1111-4a2b-8c3d-4e5f60718293").ToString("D").ToUpperInvariant();

        string owner = archivedSessionId.ToString("N");

        string relative = owner + "/note/v1/note.bin";

        byte[] payload = "attachment bytes"u8.ToArray();

        string payloadPath = Path.Combine(sourceAttachments, relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);

        await File.WriteAllBytesAsync(payloadPath, payload, cancellationToken).ConfigureAwait(false);

        string digest = Convert.ToHexString(SHA256.HashData(payload));

        await using (SqliteConnection connection = await BackupRestoreDatabaseWorker
            .OpenAsync(
                Path.Combine(sourceRoot, "arcanum.db"),
                sourceSecret,
                readOnly: false,
                cancellationToken)
            .ConfigureAwait(false))
        {

            await using SqliteCommand seed = connection.CreateCommand();

            seed.CommandText = $"""
                INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('{session}', '{campaign}', 'Archived session', 'active',
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                       "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
                VALUES ('{entryOne}', '{session}', 0, 'ask', '', '2026-01-01T00:00:00Z', 1, NULL, NULL,
                        NULL, 0);

                INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                       "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
                VALUES ('{assistantEntryId}', '{session}', 1, 'answer', 'model', '2026-01-01T00:00:00Z',
                        2, NULL, NULL, NULL, 0);

                INSERT INTO "SessionAttachments"
                    ("Id", "SessionId", "State", "LogicalKey", "OriginalFileName", "Version",
                     "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                     "SourceKind", "SourceStatus", "EncryptionVersion")
                VALUES ('{Guid.Parse("c8d9ea1f-2031-4b42-8c53-d64e75f86a97"):D}', '{session}', 'Bound',
                        'note', 'note.txt', 1, '{relative}', '{digest}', 'text/plain', {payload.Length},
                        'Text', '2026-01-01T00:00:00Z', 'WorkspaceFile', 'Refreshable', 0);
                """;

            _ = await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // A committed finalization, so WriteImportedGuardsAsync's AssistantEntryId/SessionId
            // write is actually exercised rather than only argued correct by substitution. The guard
            // refuses a row with no consumed reservation for the same Session and assistant identity,
            // so an archive that holds a finalization holds both — the reservation is minted inside an
            // authorized turn-capacity scope, matching how a real committed turn produces one.
            using (CovenantSqliteAuthorizationScope capacity = CovenantSqliteConnectionInitializer
                .Instance
                .Authorize(connection, CovenantSqliteAuthorizationKind.TurnCapacityMutation))
            {

                await using SqliteCommand reservation = connection.CreateCommand();

                reservation.CommandText = $"""
                    INSERT INTO assistant_finalization_capacity_reservations (
                        ReservationId, SessionId, AssistantEntryId, OriginCode, ClaimId, StateCode,
                        CreatedAtUtc, StateChangedAtUtc)
                    VALUES ('{Guid.NewGuid():D}', '{session}', '{assistantEntryId}', 2, NULL, 2,
                            '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                    """;

                _ = await reservation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            }

            await using SqliteCommand finalization = connection.CreateCommand();

            finalization.CommandText = $"""
                INSERT INTO assistant_entry_finalizations (
                    AssistantEntryId, SessionId, OutcomeCode, ContentSensitivityCode,
                    ContentSensitivityDigest, RequestDigest, FinalReceiptDigest, SourceEvidenceDigest,
                    FinalizedAtUtc)
                VALUES ('{assistantEntryId}', '{session}', 1, 0, zeroblob(32), zeroblob(32), NULL, NULL,
                        '2026-01-01T00:00:00.0000000Z');
                """;

            _ = await finalization.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        CovenantSelectiveImportServices services = new(
            new GrantingProtectedTransferGate(),
            new ProtectedArtifactTransferStore(CovenantSqliteConnectionInitializer.Instance, TimeProvider.System));

        BackupSessionImportResult result = await BackupSessionImporter.ImportProtectedAsync(
            services,
            Path.Combine(sourceRoot, "arcanum.db"),
            Path.Combine(destinationRoot, "arcanum.db"),
            [archivedSessionId],
            sourceAttachments,
            destinationAttachments,
            destinationSecret,
            sourceSecret,
            [new BackupSessionCampaignMapping(sourceCampaignId, destinationCampaignId)],
            cancellationToken).ConfigureAwait(false);

        if (result.Issues.Length != 0 || result.Sessions != 1 || result.Entries != 2 || result.Attachments != 1)
        {

            throw new InvalidOperationException(
                "The harness's protected-transfer drive did not commit the graph it seeded: "
                + string.Join(
                    "; ",
                    result.Issues.Select(static issue => issue.Code + " " + issue.Message)));

        }

        _kdfDatabasePath = Path.Combine(destinationRoot, "arcanum.db");

        _kdfSecret = destinationSecret;

        // The finalization write is exercised, not just staged: confirmed by reading it back rather
        // than trusted from the result counts above, which say nothing about this table.
        await using (SqliteConnection verify = await BackupRestoreDatabaseWorker
            .OpenAsync(_kdfDatabasePath, _kdfSecret, readOnly: true, cancellationToken)
            .ConfigureAwait(false))
        {

            await using SqliteCommand count = verify.CreateCommand();

            count.CommandText = "SELECT COUNT(*) FROM assistant_entry_finalizations;";

            long finalizations = (long)(await count.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false))!;

            if (finalizations != 1)
            {

                throw new InvalidOperationException(
                    "The harness's protected-transfer drive did not write the finalization it seeded.");

            }

        }

    }

    /// <summary>
    /// Drives <see cref="BackupSessionImporter"/>'s unprotected merge path through
    /// <see cref="BackupSessionImporter.ImportAsync"/>, forcing a Session id collision so the importer
    /// mints a fresh remapped identity — a value the writer under test actually has to canonicalise,
    /// unlike the all-digit id the existence gate requires to reach this path at all.
    /// </summary>
    public async Task ImportSessionThroughTheUnprotectedMergeAsync(
        CancellationToken cancellationToken = default)
    {

        string sourceRoot = Directory.CreateDirectory(Path.Combine(_root, "unprotected-source")).FullName;

        string destinationRoot = Directory.CreateDirectory(
            Path.Combine(_root, "unprotected-destination")).FullName;

        string sourceAttachments = Directory.CreateDirectory(
            Path.Combine(sourceRoot, "attachments")).FullName;

        string destinationAttachments = Directory.CreateDirectory(
            Path.Combine(destinationRoot, "attachments")).FullName;

        string sourceSecret = await CreateGrimoireDatabaseAsync(sourceRoot, cancellationToken)
            .ConfigureAwait(false);

        string destinationSecret = await CreateGrimoireDatabaseAsync(destinationRoot, cancellationToken)
            .ConfigureAwait(false);

        // Deliberately all-digit hex. The unprotected merge path's own existence gate binds this
        // rendering unnormalised against the archive, and only an identity with no hex letters
        // renders the same either way — see the design note on IdentitySpellingContractTests.
        Guid sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        // Lowercase, and unlike sessionId above that is inert rather than deliberate: nothing in this
        // case reads these two back, but a real archive would hold Entries.Id canonical uppercase
        // (the object-relational writer's own form) — a future read added against this harness should
        // not take this seed as evidence of what a genuine archive looks like.
        string sourceEntryId = Guid.NewGuid().ToString();

        string sourceAttachmentId = Guid.NewGuid().ToString();

        string relative = sessionId.ToString("N") + "/note/v1/note.bin";

        string payloadPath = Path.Combine(sourceAttachments, relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);

        await File.WriteAllTextAsync(payloadPath, "attachment bytes", cancellationToken)
            .ConfigureAwait(false);

        await using (SqliteConnection connection = await BackupRestoreDatabaseWorker
            .OpenAsync(
                Path.Combine(sourceRoot, "arcanum.db"),
                sourceSecret,
                readOnly: false,
                cancellationToken)
            .ConfigureAwait(false))
        {

            await using SqliteCommand seed = connection.CreateCommand();

            seed.CommandText = $"""
                INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('{sessionId}', NULL, 'Archived session', 'active',
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                       "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
                VALUES ('{sourceEntryId}', '{sessionId}', 0, 'ask', '', '2026-01-01T00:00:00Z', 1, NULL,
                        NULL, NULL, 0);

                INSERT INTO "SessionAttachments"
                    ("Id", "SessionId", "State", "LogicalKey", "OriginalFileName", "Version",
                     "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                     "SourceKind", "SourceStatus", "EncryptionVersion")
                VALUES ('{sourceAttachmentId}', '{sessionId}', 'Bound', 'note', 'note.txt', 1,
                        '{relative}', 'abc', 'text/plain', 16, 'Text', '2026-01-01T00:00:00Z',
                        'WorkspaceFile', 'Refreshable', 0);
                """;

            _ = await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        // The destination already has a Session under this exact id, so the merge must remap — which
        // is what makes it mint the fresh, letter-bearing Guid this test actually needs.
        await using (SqliteConnection connection = await BackupRestoreDatabaseWorker
            .OpenAsync(
                Path.Combine(destinationRoot, "arcanum.db"),
                destinationSecret,
                readOnly: false,
                cancellationToken)
            .ConfigureAwait(false))
        {

            await using SqliteCommand seed = connection.CreateCommand();

            seed.CommandText = $"""
                INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('{sessionId}', NULL, 'The Session already living here', 'active',
                        '2026-02-02T00:00:00Z', '2026-02-02T00:00:00Z');
                """;

            _ = await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        BackupSessionImportResult result = await BackupSessionImporter.ImportAsync(
            Path.Combine(sourceRoot, "arcanum.db"),
            Path.Combine(destinationRoot, "arcanum.db"),
            [sessionId],
            sourceAttachments,
            destinationAttachments,
            destinationSecret,
            sourceSecret,
            cancellationToken).ConfigureAwait(false);

        if (result.Issues.Length != 0 || result.RemappedIds != 1 || result.Sessions != 1
            || result.Entries != 1 || result.Attachments != 1)
        {

            throw new InvalidOperationException(
                "The harness's unprotected-merge drive did not remap and commit the graph it seeded: "
                + string.Join(
                    "; ",
                    result.Issues.Select(static issue => issue.Code + " " + issue.Message)));

        }

        _kdfDatabasePath = Path.Combine(destinationRoot, "arcanum.db");

        _kdfSecret = destinationSecret;

    }

    /// <summary>
    /// Every non-canonical <c>(table, column, value)</c> in whichever database a drive method
    /// populated.
    /// </summary>
    public async Task<IReadOnlyList<NonCanonicalIdentity>> NonCanonicalAsync(
        CancellationToken cancellationToken = default)
    {

        await using SqliteConnection connection = await OpenForScanAsync(cancellationToken)
            .ConfigureAwait(false);

        List<NonCanonicalIdentity> found = [];

        foreach ((string table, string column) in IdentityColumns)
        {

            await using SqliteCommand exists = connection.CreateCommand();

            exists.CommandText = """
                SELECT 1 FROM sqlite_master WHERE "type" = 'table' AND "name" = $table LIMIT 1;
                """;

            _ = exists.Parameters.AddWithValue("$table", table);

            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {

                continue;

            }

            await using SqliteCommand scan = connection.CreateCommand();

            // Case alone is only half of canonical. Guid.ToString("N") renders 32 hex characters
            // with no dashes at all, all uppercase if the caller happened to uppercase it — a form
            // "<> upper(...)" alone cannot see, since it is already its own upper() image. The shape
            // half — 36 characters, dashes at the fixed positions a dashed Guid always has them —
            // catches that. Neither check subsumes the other: a lowercase-dashed value fails only the
            // first, a dash-free uppercase value fails only the second.
            scan.CommandText = $"""
                SELECT "{column}" FROM "{table}"
                WHERE "{column}" IS NOT NULL
                  AND (
                      "{column}" <> upper("{column}")
                      OR length("{column}") <> 36
                      OR substr("{column}", 9, 1) <> '-'
                      OR substr("{column}", 14, 1) <> '-'
                      OR substr("{column}", 19, 1) <> '-'
                      OR substr("{column}", 24, 1) <> '-'
                  );
                """;

            await using SqliteDataReader reader = await scan.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                string value = reader.GetString(0);

                found.Add(new NonCanonicalIdentity(table, column, value, DescribeFailure(value)));

            }

        }

        return found;

    }

    /// <summary>
    /// Which half of "canonical uppercase dashed, 36 characters" a value fails, or both.
    /// </summary>
    /// <remarks>
    /// Computed here rather than in SQL: SQLite's <c>substr</c> is forgiving of an out-of-range
    /// offset on a short string, which is exactly what makes it safe to use for the filter above, but
    /// building a readable two-part message is clearer as ordinary string indexing once the row is
    /// already known to be one of the offenders.
    /// </remarks>
    private static string DescribeFailure(string value)
    {

        bool wrongCase = !string.Equals(value, value.ToUpperInvariant(), StringComparison.Ordinal);

        bool wrongShape = value.Length != 36
            || value[8] != '-'
            || value[13] != '-'
            || value[18] != '-'
            || value[23] != '-';

        return (wrongCase, wrongShape) switch
        {

            (true, true) => "wrong case and wrong shape (not 36 characters dashed at 8/13/18/23)",

            (true, false) => "wrong case",

            (false, true) => "wrong shape (not 36 characters dashed at 8/13/18/23)",

            (false, false) => "unknown — matched the SQL filter but neither check on the C# side agrees",

        };

    }

    public async ValueTask DisposeAsync()
    {

        SqliteConnection.ClearAllPools();

        try
        {

            if (Directory.Exists(_root))
            {

                Directory.Delete(_root, recursive: true);

            }

        }
        catch (IOException)
        {

            // Scratch under the OS temp root; a scanner still holding a handle must not fail a test
            // that has already made its assertions.

        }
        catch (UnauthorizedAccessException)
        {

            // Same.

        }

    }

    private Task<SqliteConnection> OpenForScanAsync(CancellationToken cancellationToken)
    {

        if (_kdfDatabasePath is null)
        {

            throw new InvalidOperationException(
                "NonCanonicalAsync was called before any drive method populated a database.");

        }

        return BackupRestoreDatabaseWorker.OpenAsync(
            _kdfDatabasePath,
            _kdfSecret!,
            readOnly: true,
            cancellationToken);

    }

    /// <summary>
    /// Drops <c>assistant_entry_finalizations_validate_insert</c> in full, so the protected-transfer
    /// case's seeded finalization can be written at all.
    /// </summary>
    /// <remarks>
    /// <c>DROP TRIGGER</c> removes the whole trigger body, not one clause of it — both the
    /// capacity-reservation <c>RAISE</c> this case actually needs out of the way, and the separate
    /// erased-assistant-entry <c>RAISE</c> alongside it. Nothing in this test exercises the erasure
    /// clause, so the drop is harmless here, but it is not surgical: this removes both guards, not
    /// only the one blocking the write.
    ///
    /// <para>The capacity-reservation guard refuses a row with no consumed reservation for the exact
    /// (Session, AssistantEntryId) pair being written. The destination pair is minted fresh by the
    /// store on every import — <c>WriteDestinationGraphAsync</c> gives every copied Entry a new
    /// <see cref="Guid.NewGuid()"/>, and the destination Session id is chosen the same way — so no
    /// reservation for it can be seeded in advance from outside the write. This is the documented,
    /// pre-existing capacity-reservation gap the design spec records as deliberately out of scope for
    /// identity spelling (§9), not something this task introduces or is fixing.
    /// <see cref="RetroDownfall.Arcanum.Tests.Repositories.ProtectedArtifactTransferStoreTests"/>
    /// carries the same limitation and resolves it the same way, one layer earlier: its destination's
    /// selective schema install never includes this trigger in the first place. Dropping it here
    /// after a full install reaches the same end state without giving up full-schema fidelity for
    /// every other guard this harness relies on for its other assertions.</para>
    /// </remarks>
    private static async Task DropFinalizationCapacityGuardAsync(
        string installationRoot,
        string secret,
        CancellationToken cancellationToken)
    {

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker
            .OpenAsync(
                Path.Combine(installationRoot, "arcanum.db"),
                secret,
                readOnly: false,
                cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand drop = connection.CreateCommand();

        drop.CommandText = "DROP TRIGGER IF EXISTS assistant_entry_finalizations_validate_insert;";

        _ = await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task<string> CreateGrimoireDatabaseAsync(
        string installationRoot,
        CancellationToken cancellationToken)
    {

        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        string databasePath = Path.Combine(installationRoot, "arcanum.db");

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        GrimoireKdfSidecarFile.Write(databasePath, sidecar);

        byte[] salt = sidecar.GetSaltBytes();

        string passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(secret, salt);

        CryptographicOperations.ZeroMemory(salt);

        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            new SqliteConnectionStringBuilder
            {

                DataSource = databasePath,

                Password = passphrase,

                Pooling = false,

            }.ToString(),
            cancellationToken).ConfigureAwait(false);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, cancellationToken)
            .ConfigureAwait(false);

        return secret;

    }

}

/// <summary>
/// A compound lease gate that grants exactly the one capability a selective protected import takes,
/// and refuses every other. What is under test through this harness is the archive the planner reads
/// and the graph the store copies, not the lease arbitration, which has its own suite.
/// </summary>
internal sealed class GrantingProtectedTransferGate : ICovenantOperationGate
{

    public ValueTask<Result<CovenantProtectedTransferLease>> AcquireProtectedTransferAsync(
        ProtectedTransferScope scope,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            Result<CovenantProtectedTransferLease>.Success(
                new CovenantProtectedTransferLease(
                    new GrantedRegistration(owner, scope.ToOperationScope()))));

    public ValueTask<Result<CovenantInstallationReadLease>> AcquireInstallationReadAsync(
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A selective import takes no installation read lease.");

    public ValueTask<Result<CovenantReadLease>> AcquireReadAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A selective import takes no nested read lease.");

    public ValueTask<Result<CovenantWriteLease>> AcquireWriteAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A selective import takes no nested write lease.");

    public ValueTask<Result<CovenantTurnLease>> AcquireTurnAsync(
        CanonicalCampaignContext context,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A selective import runs no turn.");

    public ValueTask<Result<CovenantMcpLease>> AcquireMcpAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A selective import runs no MCP mutation.");

    public ValueTask<Result<CovenantAcceleratorLease>> AcquireAcceleratorAsync(
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A selective import synchronizes no accelerator.");

    public ValueTask<Result<CovenantCleanupLease>> AcquireCleanupAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A selective import runs no owner cleanup.");

    public ValueTask<Result<CovenantCampaignExclusiveLease>> AcquireCampaignExclusiveAsync(
        Guid campaignId,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A selective import closes no Campaign.");

    public ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A selective import closes no installation.");

    public ValueTask<Result<CovenantExclusiveLease>> ResumeOrAcquireExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A live import resumes nothing.");

    public ValueTask<Result<CovenantCampaignExclusiveLease>> ResumeCampaignExclusiveAsync(
        Guid campaignId,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A live import resumes no Campaign scope.");

    public ValueTask<Result<CovenantProtectedTransferLease>> ResumeProtectedTransferAsync(
        ProtectedTransferScope scope,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A live import acquires; only startup resumes.");

    public ValueTask<Result<CovenantExclusiveLease>> ResumeExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A live import acquires; only startup resumes.");

    private sealed class GrantedRegistration(
        CovenantExclusiveRecoveryOwner owner,
        CovenantOperationScope scope) : ICovenantExclusiveLeaseRegistration
    {

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.NewGuid(),
            RuntimeAuthorityGeneration: 1,
            CovenantLeaseKind.ProtectedTransfer,
            CovenantLeaseCoverage.Scoped,
            scope,
            DatasetGeneration: Guid.NewGuid(),
            CapabilityGeneration: 1,
            AuthorityEpoch: 1,
            CanonicalSequence: 1,
            CampaignAvailabilityGeneration: null,
            CampaignPathRevision: null,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            owner,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        public Result ExecuteWhileHeld(Func<Result> callback) => callback();

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> CompleteAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;

    }

}
