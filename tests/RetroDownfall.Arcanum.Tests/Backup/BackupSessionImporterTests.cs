using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The importer merges into the <em>live</em> installation, so its failure paths are the interesting
/// ones: whatever it wrote outside the destination transaction has to come back out with it.
/// </summary>
public sealed class BackupSessionImporterTests : IDisposable
{

    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid CampaignId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Hex letters, deliberately. The protected import case below turns on the difference between the
    // uppercase spelling an archive holds and the lowercase one a Guid renders by default, and an
    // all-digit identity renders identically in both — which is how a suite proves nothing while
    // looking like it proves this.
    private static readonly Guid ArchivedSessionId =
        Guid.Parse("a6b5c4d3-e2f1-4098-8765-4a3b2c1d0e9f");

    private static readonly Guid AssistantEntryId =
        Guid.Parse("d3e2f1a6-b5c4-4987-8650-9f0e1d2c3b4a");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-session-import-" + Guid.NewGuid().ToString("N"));

    private readonly string _sourceRoot;

    private readonly string _destinationRoot;

    public BackupSessionImporterTests()
    {

        _sourceRoot = Path.Combine(_root, "archive");

        _destinationRoot = Path.Combine(_root, "installation");

        Directory.CreateDirectory(_sourceRoot);

        Directory.CreateDirectory(_destinationRoot);

    }

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task A_cancelled_import_leaves_no_attachment_payloads_in_the_live_installation()
    {

        string sourceSecret = await SeedSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        using CancellationTokenSource cancellation = new();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BackupSessionImporter.ImportAsync(
                Path.Combine(_sourceRoot, "arcanum.db"),
                Path.Combine(_destinationRoot, "arcanum.db"),
                [SessionId],
                Path.Combine(_sourceRoot, "attachments"),
                Path.Combine(_destinationRoot, "attachments"),
                destinationSecret,
                sourceSecret,
                cancellation.Token,
                beforeCommitForTests: cancellation.Cancel));

        Assert.Empty(
            Directory.EnumerateFiles(
                Path.Combine(_destinationRoot, "attachments"),
                "*",
                SearchOption.AllDirectories));

        Assert.Equal(0, await CountSessionsAsync(destinationSecret));

    }

    [Fact]
    public async Task A_completed_import_keeps_its_attachment_payloads()
    {

        string sourceSecret = await SeedSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        BackupSessionImportResult result = await BackupSessionImporter.ImportAsync(
            Path.Combine(_sourceRoot, "arcanum.db"),
            Path.Combine(_destinationRoot, "arcanum.db"),
            [SessionId],
            Path.Combine(_sourceRoot, "attachments"),
            Path.Combine(_destinationRoot, "attachments"),
            destinationSecret,
            sourceSecret,
            CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.Equal(1, result.Sessions);

        Assert.Equal(1, result.Attachments);

        Assert.Equal(
            "attachment bytes",
            await File.ReadAllTextAsync(
                Path.Combine(
                    _destinationRoot,
                    "attachments",
                    SessionId.ToString("N"),
                    "note",
                    "v1",
                    "note.bin")));

        Assert.Equal(1, await CountSessionsAsync(destinationSecret));

    }

    /// <summary>
    /// A remapped Session has to take its payload directory with it. If the attachment row keeps the
    /// archived owner segment, the imported Session's rows point into the Session it collided with:
    /// deleting that one takes the import's bytes with it, and deleting the import leaves them behind
    /// forever, because the directory it looks for was never created.
    /// </summary>
    [Fact]
    public async Task A_remapped_Session_owns_the_attachment_directory_its_rows_point_at()
    {

        string sourceSecret = await SeedSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        await SeedCollidingSessionAsync(destinationSecret);

        BackupSessionImportResult result = await BackupSessionImporter.ImportAsync(
            Path.Combine(_sourceRoot, "arcanum.db"),
            Path.Combine(_destinationRoot, "arcanum.db"),
            [SessionId],
            Path.Combine(_sourceRoot, "attachments"),
            Path.Combine(_destinationRoot, "attachments"),
            destinationSecret,
            sourceSecret,
            CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.Equal(1, result.RemappedIds);

        Guid imported = await ReadImportedSessionIdAsync(destinationSecret);

        Assert.NotEqual(SessionId, imported);

        string relative = await ReadAttachmentRelativePathAsync(destinationSecret, imported);

        Assert.StartsWith(imported.ToString("N") + "/", relative, StringComparison.Ordinal);

        Assert.Equal(
            "attachment bytes",
            await File.ReadAllTextAsync(
                Path.Combine(
                    _destinationRoot,
                    "attachments",
                    relative.Replace('/', Path.DirectorySeparatorChar))));

        // The archived id names the Session already living here. Nothing this import wrote may land
        // under it.
        Assert.False(
            Directory.Exists(
                Path.Combine(_destinationRoot, "attachments", SessionId.ToString("N"))));

    }

    /// <summary>
    /// Payload bytes are the one part of an import no transaction unwinds, so the importer deletes
    /// them itself when it does not commit. It may only delete the ones it wrote: a destination file
    /// it merely collided with belongs to the live installation, and removing it dangles that row
    /// while the returned issue says the destination is unchanged.
    /// </summary>
    [Fact]
    public async Task An_import_never_removes_destination_payload_bytes_it_did_not_write()
    {

        string sourceSecret = await SeedSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        string occupied = Path.Combine(
            _destinationRoot,
            "attachments",
            SourceRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);

        await File.WriteAllTextAsync(occupied, "bytes the destination already had");

        BackupSessionImportResult result = await BackupSessionImporter.ImportAsync(
            Path.Combine(_sourceRoot, "arcanum.db"),
            Path.Combine(_destinationRoot, "arcanum.db"),
            [SessionId],
            Path.Combine(_sourceRoot, "attachments"),
            Path.Combine(_destinationRoot, "attachments"),
            destinationSecret,
            sourceSecret,
            CancellationToken.None);

        Assert.Equal(
            "bytes the destination already had",
            await File.ReadAllTextAsync(occupied));

        Assert.Empty(result.Issues);

        string relative = await ReadAttachmentRelativePathAsync(destinationSecret, SessionId);

        Assert.NotEqual(SourceRelativePath, relative);

        Assert.Equal(
            "attachment bytes",
            await File.ReadAllTextAsync(
                Path.Combine(
                    _destinationRoot,
                    "attachments",
                    relative.Replace('/', Path.DirectorySeparatorChar))));

    }

    [Fact]
    public async Task A_Campaign_bound_Session_with_no_mapping_refuses_before_the_destination_is_opened()
    {

        string sourceSecret = await SeedSourceAsync(campaignBound: true);

        BackupSessionImportResult result = await BackupSessionImporter.ImportProtectedAsync(
            new CovenantSelectiveImportServices(
                new CovenantRestoreStagingTests.RecordingExclusiveGate(),
                new UnreachableTransferStore()),
            Path.Combine(_sourceRoot, "arcanum.db"),
            // Deliberately a path with no database and no KDF sidecar. Opening it would throw rather
            // than refuse, so reaching this refusal at all proves the coverage pass ran first — and
            // therefore that nothing was committed into a live installation before it.
            Path.Combine(_destinationRoot, "never-opened", "arcanum.db"),
            [SessionId],
            Path.Combine(_sourceRoot, "attachments"),
            Path.Combine(_destinationRoot, "attachments"),
            "destination secret",
            sourceSecret,
            [],
            CancellationToken.None);

        BackupVerifyIssue issue = Assert.Single(result.Issues);

        Assert.Equal("backup.restore_import_refused", issue.Code);

        // The typed refusal survives the wrap: the operator is still told which archived Campaign is
        // unaccounted for and which option answers it.
        Assert.Contains(CampaignId.ToString("D"), issue.Message, StringComparison.Ordinal);

        Assert.Contains("--map-campaign", issue.Message, StringComparison.Ordinal);

        Assert.Equal(0, result.Sessions);

    }

    /// <summary>
    /// A selective protected import of an ordinary archive, end to end and asserted to succeed — for a
    /// Session that carries no committed assistant turn.
    /// </summary>
    /// <remarks>
    /// The case this family never had. Every other protected-import case in the suite either refuses
    /// before the store is reached or hands the store a request a test assembled, so all of them were
    /// green while a selective protected import of a genuine backup could not work at all: the planner
    /// bound a lowercase identity against columns an ordinary archive spells uppercase, refused with
    /// "The archive does not contain Session {id}", and returned before
    /// <c>CommitImportedSessionAsync</c> was ever called.
    ///
    /// <para>Entered at <see cref="BackupSessionImporter.ImportProtectedAsync"/> — the outermost
    /// production method, the one the restore service calls — so the planner, the compound lease, the
    /// real transfer store, and the lease disposition all run. Starting one layer lower, at
    /// <c>PlanAsync</c> plus a hand-assembled request, would prove the store works on a request no
    /// production caller can currently produce.</para>
    ///
    /// <para>The counts are asserted, not just the absence of issues. A plan assembled from a graph the
    /// planner could not see would still commit — of nothing — and report success.</para>
    ///
    /// <para>The archive carries no committed finalization, and that is a limit of this case rather
    /// than a choice about what an archive holds: the destination's finalization guard requires a
    /// consumed capacity reservation for the imported assistant identity, and the transfer store
    /// writes the guard without one, so an archive with a finalization cannot commit here for a reason
    /// that has nothing to do with how an identity is spelled. The planner's own two finalization
    /// reads are covered where they can be reached — over an archive that has one — in
    /// <c>BackupSessionImportPlannerTests</c>.</para>
    /// </remarks>
    [Fact]
    public async Task A_selective_protected_import_of_an_ordinary_archive_commits_a_Session_with_no_finalization()
    {

        string sourceSecret = await SeedObjectRelationalSourceAsync();

        string destinationSecret = await SeedDestinationAsync();

        // The precondition, pinned rather than assumed. Both halves: uppercase is what an ordinary
        // archive holds, and the inequality is what proves this identity still carries hex letters —
        // an all-digit one renders the same in either case and would leave this case proving nothing.
        string stored = await ReadStoredSessionIdAsync(sourceSecret);

        Assert.Equal(stored.ToUpperInvariant(), stored);

        Assert.NotEqual(ArchivedSessionId.ToString("D"), stored);

        BackupSessionImportResult result = await BackupSessionImporter.ImportProtectedAsync(
            new CovenantSelectiveImportServices(
                new ProtectedTransferGate(),
                new ProtectedArtifactTransferStore(
                    CovenantSqliteConnectionInitializer.Instance,
                    TimeProvider.System)),
            Path.Combine(_sourceRoot, "arcanum.db"),
            Path.Combine(_destinationRoot, "arcanum.db"),
            [ArchivedSessionId],
            Path.Combine(_sourceRoot, "attachments"),
            Path.Combine(_destinationRoot, "attachments"),
            destinationSecret,
            sourceSecret,
            [],
            CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.Equal(1, result.Sessions);

        // The graph, not just the Session row. Each of these comes from a separate planner comparison
        // against a separate column, and a plan that saw none of them commits an empty import that
        // still reports success.
        Assert.Equal(2, result.Entries);

        Assert.Equal(1, result.Attachments);

        Assert.Equal(1, await CountDestinationAsync(destinationSecret, "Sessions"));

        Assert.Equal(2, await CountDestinationAsync(destinationSecret, "Entries"));

        Assert.Equal(1, await CountDestinationAsync(destinationSecret, "SessionAttachments"));

    }

    private sealed class UnreachableTransferStore : IProtectedArtifactTransferStore
    {

        public Task<ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>>
            CommitImportedSessionAsync(
                ImportedSessionTransferRequest request,
                ImportedSessionSourceLease sourceLease,
                CovenantProtectedTransferLease transferLease,
                ProtectedSessionImportDestination destination,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "A refused selection commits no protected transfer.");

    }

    /// <summary>
    /// The compound lease a selective protected import runs under, granted rather than arbitrated.
    /// </summary>
    /// <remarks>
    /// Only the protected-transfer acquisition is answered; every other member throws, because a
    /// selective import takes no other lease and a fake that quietly answered one would hide a caller
    /// that started needing it. What is under test here is the archive the planner reads and the graph
    /// the store copies — not the arbitration, which has its own suite.
    /// </remarks>
    private sealed class ProtectedTransferGate : ICovenantOperationGate
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

    // The owner segment is the session id in "N" form, exactly as SessionAttachmentStore writes it.
    // Seeding the dashed form instead would make the importer's id remap look like it worked, which
    // is how a permanent no-op survived here in the first place.
    private static string SourceRelativePath => SessionId.ToString("N") + "/note/v1/note.bin";

    private async Task<string> SeedSourceAsync(bool campaignBound = false)
    {

        string secret = await CreateDatabaseAsync(_sourceRoot);

        string payload = Path.Combine(
            _sourceRoot,
            "attachments",
            SourceRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);

        await File.WriteAllTextAsync(payload, "attachment bytes");

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_sourceRoot, "arcanum.db"),
            secret,
            readOnly: false,
            CancellationToken.None);

        await using SqliteCommand seed = connection.CreateCommand();

        string campaignId = campaignBound ? $"'{CampaignId}'" : "NULL";

        seed.CommandText = $"""
            INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
            VALUES ('{SessionId}', {campaignId}, 'Archived session', 'active',
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

            INSERT INTO "SessionAttachments"
                ("Id", "SessionId", "State", "LogicalKey", "OriginalFileName", "Version",
                 "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                 "SourceKind", "SourceStatus", "EncryptionVersion")
            VALUES ('55555555-5555-5555-5555-555555555555', '{SessionId}', 'Bound', 'note',
                    'note.txt', 1, '{SourceRelativePath}', 'abc', 'text/plain', 16, 'Text',
                    '2026-01-01T00:00:00Z', 'WorkspaceFile', 'Refreshable', 0);
            """;

        _ = await seed.ExecuteNonQueryAsync();

        return secret;

    }

    /// <summary>
    /// Seeds an archive the way an ordinary installation writes one.
    /// </summary>
    /// <remarks>
    /// <c>"Sessions"."Id"</c> and <c>"Entries"."SessionId"</c> come from a <see cref="Guid"/> property
    /// mapped to TEXT, and the object-relational writer stores one as uppercase dashed text.
    /// <c>"SessionAttachments"."SessionId"</c> is seeded lowercase in the same archive, because that
    /// is what all three of its writers render — this is a real installation's mixture rather than one
    /// spelling applied everywhere, and inventing a spelling nowhere writes would be the same mistake
    /// as seeding the one a broken read expects. Kept apart from <see cref="SeedSourceAsync"/> rather
    /// than folded into it: the unprotected merge path binds the lowercase rendering against
    /// <c>"Sessions"."Id"</c> and would refuse this archive outright, so seeding it there would replace
    /// a suite that passes over a known gap with a suite that fails over one.
    ///
    /// <para>The attachment's owner segment is the Session in <c>"N"</c> form, exactly as
    /// <c>SessionAttachmentStore</c> writes it, and its digest is the real SHA-256 of the payload,
    /// because the store reopens the copied bytes and refuses a blob that does not match.</para>
    /// </remarks>
    private async Task<string> SeedObjectRelationalSourceAsync()
    {

        string secret = await CreateDatabaseAsync(_sourceRoot);

        string session = ArchivedSessionId.ToString("D").ToUpperInvariant();

        string assistantEntryId = AssistantEntryId.ToString("D").ToUpperInvariant();

        string owner = ArchivedSessionId.ToString("N");

        string relative = owner + "/note/v1/note.bin";

        string payload = Path.Combine(
            _sourceRoot,
            "attachments",
            relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);

        byte[] bytes = "attachment bytes"u8.ToArray();

        await File.WriteAllBytesAsync(payload, bytes, CancellationToken.None);

        string digest = Convert.ToHexString(SHA256.HashData(bytes));

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_sourceRoot, "arcanum.db"),
            secret,
            readOnly: false,
            CancellationToken.None);

        await using SqliteCommand seed = connection.CreateCommand();

        seed.CommandText = $"""
            INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
            VALUES ('{session}', NULL, 'Archived session', 'active',
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

            INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                   "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
            VALUES ('{Guid.Parse("b7c8d9ea-1f20-4a31-8b42-c53d64e75f86").ToString("D").ToUpperInvariant()}',
                    '{session}', 0, 'ask', '', '2026-01-01T00:00:00Z', 1, NULL, NULL, NULL, 0);

            INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                   "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
            VALUES ('{assistantEntryId}', '{session}', 1, 'answer', 'model',
                    '2026-01-01T00:00:00Z', 2, NULL, NULL, NULL, 0);

            INSERT INTO "SessionAttachments"
                ("Id", "SessionId", "State", "LogicalKey", "OriginalFileName", "Version",
                 "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                 "SourceKind", "SourceStatus", "EncryptionVersion")
            VALUES ('{Guid.Parse("c8d9ea1f-2031-4b42-8c53-d64e75f86a97"):D}',
                    '{ArchivedSessionId:D}', 'Bound', 'note', 'note.txt', 1, '{relative}', '{digest}',
                    'text/plain', {bytes.Length}, 'Text', '2026-01-01T00:00:00Z',
                    'WorkspaceFile', 'Refreshable', 0);
            """;

        _ = await seed.ExecuteNonQueryAsync();

        return secret;

    }

    private async Task<string> ReadStoredSessionIdAsync(string sourceSecret)
    {

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_sourceRoot, "arcanum.db"),
            sourceSecret,
            readOnly: true,
            CancellationToken.None);

        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = """
            SELECT "Id" FROM "Sessions" WHERE "Title" = 'Archived session';
            """;

        return (string)(await read.ExecuteScalarAsync())!;

    }

    private async Task<long> CountDestinationAsync(string destinationSecret, string table)
    {

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_destinationRoot, "arcanum.db"),
            destinationSecret,
            readOnly: true,
            CancellationToken.None);

        await using SqliteCommand count = connection.CreateCommand();

        count.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";

        return Convert.ToInt64(await count.ExecuteScalarAsync());

    }

    private async Task<string> SeedDestinationAsync()
    {

        string secret = await CreateDatabaseAsync(_destinationRoot);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(
            Path.Combine(_destinationRoot, "attachments"));

        return secret;

    }

    /// <summary>Gives the destination a Session under the archived id, so the import must remap.</summary>
    private async Task SeedCollidingSessionAsync(string destinationSecret)
    {

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_destinationRoot, "arcanum.db"),
            destinationSecret,
            readOnly: false,
            CancellationToken.None);

        await using SqliteCommand seed = connection.CreateCommand();

        seed.CommandText = $"""
            INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
            VALUES ('{SessionId}', NULL, 'The Session already living here', 'active',
                    '2026-02-02T00:00:00Z', '2026-02-02T00:00:00Z');
            """;

        _ = await seed.ExecuteNonQueryAsync();

    }

    private async Task<Guid> ReadImportedSessionIdAsync(string destinationSecret)
    {

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_destinationRoot, "arcanum.db"),
            destinationSecret,
            readOnly: true,
            CancellationToken.None);

        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = """
            SELECT "Id" FROM "Sessions" WHERE "Title" = 'Archived session';
            """;

        return Guid.Parse((string)(await read.ExecuteScalarAsync())!);

    }

    private async Task<string> ReadAttachmentRelativePathAsync(
        string destinationSecret,
        Guid sessionId)
    {

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_destinationRoot, "arcanum.db"),
            destinationSecret,
            readOnly: true,
            CancellationToken.None);

        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = """
            SELECT "RelativePath" FROM "SessionAttachments" WHERE "SessionId" = $id;
            """;

        _ = read.Parameters.AddWithValue("$id", sessionId.ToString());

        return (string)(await read.ExecuteScalarAsync())!;

    }

    private static async Task<string> CreateDatabaseAsync(string installationRoot)
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
            CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            1536,
            CancellationToken.None);

        return secret;

    }

    private async Task<long> CountSessionsAsync(string destinationSecret)
    {

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_destinationRoot, "arcanum.db"),
            destinationSecret,
            readOnly: true,
            CancellationToken.None);

        await using SqliteCommand count = connection.CreateCommand();

        count.CommandText = """
            SELECT COUNT(*) FROM "Sessions";
            """;

        return Convert.ToInt64(await count.ExecuteScalarAsync());

    }

}
