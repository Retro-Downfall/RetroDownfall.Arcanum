using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// The one path a Session graph takes across a protected boundary, against real SQLCipher databases.
/// </summary>
/// <remarks>
/// Two real encrypted databases and a real attachment tree, because every property under test is a
/// property of what the store reads back rather than of what the caller claimed. A faked source could
/// only ever agree with the request that described it (§10.13).
/// </remarks>
public sealed class ProtectedArtifactTransferStoreTests : IAsyncLifetime, IDisposable
{

    private static readonly string[] SourceObjects =
    [
        "Sessions",
        "Entries",
        "SessionAttachments",
        "artifact_sensitivity",
        "assistant_entry_finalizations",
    ];

    private static readonly string[] DestinationObjects =
    [
        "Sessions",
        "Entries",
        "SessionAttachments",
        "assistant_entry_finalizations",
        "protected_session_transfer_intents",
        "protected_session_transfer_blobs",
        "protected_session_transfer_intents_guard_update",
        "protected_session_transfer_intents_guard_delete",
        "protected_session_transfer_blobs_guard_update",
        "protected_session_transfer_blobs_guard_delete",
    ];

    private readonly string _root =
        Directory.CreateTempSubdirectory("arcanum-transfer-store-").FullName;

    private readonly Guid _sourceSessionId = Guid.Parse("6f0a1b2c-3d4e-4f50-8192-a3b4c5d6e7f8");

    private CovenantSchemaScratchDatabase _source = null!;

    private CovenantSchemaScratchDatabase _destination = null!;

    private ProtectedArtifactTransferStore _store = null!;

    private string _sourceAttachments = string.Empty;

    private string _destinationAttachments = string.Empty;

    public async Task InitializeAsync()
    {

        _source = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await _source.InstallCoreObjectsAsync(SourceObjects, CancellationToken.None);

        _destination = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await _destination.InstallCoreObjectsAsync(DestinationObjects, CancellationToken.None);

        _sourceAttachments = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;

        _destinationAttachments = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;

        _store = new ProtectedArtifactTransferStore(
            CovenantSqliteConnectionInitializer.Instance,
            TimeProvider.System);

        await SeedSourceSessionAsync();

    }

    [Fact]
    public async Task A_substituted_effect_digest_is_refused_before_any_destination_write()
    {

        Guid operationId = Guid.NewGuid();

        ImportedSessionTransferRequest genuine = await BuildRequestAsync(operationId, null);

        ImportedSessionTransferRequest tampered = new(
            genuine.OperationId,
            genuine.SourceSessionId,
            genuine.DestinationSessionId,
            genuine.SourceEvidenceDigest,
            genuine.SourceManifestDigest,
            genuine.ManifestCounts,
            genuine.CampaignMapping,
            genuine.DestinationBindingDigest,
            Digest(0x5A));

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(tampered);

        Assert.True(completion.Result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, completion.Result.Error.Code);
        Assert.Equal(CovenantExclusiveLeaseDisposition.RollbackAndReopen, completion.Disposition);

        Assert.Equal(0, await CountDestinationAsync("Sessions"));
        Assert.Equal(0, await CountDestinationAsync("protected_session_transfer_intents"));

    }

    [Fact]
    public async Task A_source_session_carrying_a_covenant_label_can_never_be_transferred()
    {

        await AddSensitivityLabelAsync();

        ImportedSessionTransferRequest request = await BuildRequestAsync(Guid.NewGuid(), null);

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(request);

        Assert.True(completion.Result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, completion.Result.Error.Code);

        // The refusal is the store's own scan over the source's label rows, so a caller that omitted
        // the tainted artifact from its manifest still cannot get the graph committed.
        Assert.Equal(0, await CountDestinationAsync("Sessions"));

    }

    [Fact]
    public async Task A_manifest_that_does_not_match_the_source_graph_is_refused()
    {

        Guid operationId = Guid.NewGuid();

        ImportedSessionTransferRequest genuine = await BuildRequestAsync(operationId, null);

        // One fewer Entry than the source actually holds. The store counts for itself.
        ProtectedSessionTransferCounts understated = genuine.ManifestCounts with
        {
            Entries = genuine.ManifestCounts.Entries - 1,
        };

        CovenantDigest effect = ProtectedSessionTransferDigests.Effect(
            ProtectedSessionTransferKind.Import,
            operationId,
            genuine.SourceSessionId,
            null,
            genuine.DestinationSessionId,
            genuine.DestinationBindingDigest,
            genuine.SourceEvidenceDigest,
            genuine.SourceManifestDigest,
            understated).Value;

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(new ImportedSessionTransferRequest(
                operationId,
                genuine.SourceSessionId,
                genuine.DestinationSessionId,
                genuine.SourceEvidenceDigest,
                genuine.SourceManifestDigest,
                understated,
                null,
                genuine.DestinationBindingDigest,
                effect));

        Assert.True(completion.Result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, completion.Result.Error.Code);

        Assert.Equal(0, await CountDestinationAsync("Sessions"));

    }

    [Fact]
    public async Task A_campaign_mapped_import_requires_a_lease_that_closes_that_exact_scope()
    {

        Guid destinationCampaign = Guid.NewGuid();

        ImportedSessionTransferRequest request = await BuildRequestAsync(
            Guid.NewGuid(),
            new BackupSessionCampaignMapping(Guid.NewGuid(), destinationCampaign));

        // A Global lease for a Campaign-mapped import. The destination scope the operator chose is
        // not the scope the drain closed, so nothing may be written.
        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(request, ProtectedTransferScope.Global);

        Assert.True(completion.Result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.InvalidScope, completion.Result.Error.Code);

        Assert.Equal(0, await CountDestinationAsync("Sessions"));

    }

    [Fact]
    public async Task A_committed_import_writes_the_graph_and_its_imported_guards()
    {

        Guid destinationCampaign = Guid.NewGuid();

        ImportedSessionTransferRequest request = await BuildRequestAsync(
            Guid.NewGuid(),
            new BackupSessionCampaignMapping(Guid.NewGuid(), destinationCampaign));

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(request, ProtectedTransferScope.ForCampaign(destinationCampaign));

        Assert.True(
            completion.Result.IsSuccess,
            completion.Result.IsFailure ? completion.Result.Error.Message : string.Empty);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, completion.Disposition);

        Assert.Equal(1, await CountDestinationAsync("Sessions"));
        Assert.Equal(2, await CountDestinationAsync("Entries"));
        Assert.Equal(1, await CountDestinationAsync("SessionAttachments"));

        // The explicit mapping, not a guess and not a dropped binding.
        Assert.Equal(
            destinationCampaign.ToString("D"),
            await _destination.ScalarStringAsync(
                "SELECT \"CampaignId\" FROM \"Sessions\";",
                CancellationToken.None));

        // CommittedImported, and each guard names the evidence it was copied from. Without that
        // digest an imported finalization would look like a turn this installation actually ran.
        Assert.Equal(
            1,
            await _destination.ScalarLongAsync(
                "SELECT COUNT(*) FROM assistant_entry_finalizations "
                + "WHERE OutcomeCode = 3 AND SourceEvidenceDigest IS NOT NULL;",
                CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(
            _destinationAttachments,
            request.DestinationSessionId.ToString("N"),
            "note.txt")));

        // Pending until the caller spends its lease decision.
        Assert.Equal(4, await DestinationPhaseAsync());

        Assert.True((await completion.Finalizer.FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None)).IsSuccess);

        Assert.Equal(5, await DestinationPhaseAsync());

    }

    /// <summary>
    /// Every import re-owns its blobs onto the Session id the destination row is written under.
    /// </summary>
    /// <remarks>
    /// A remap that never matches is not a cosmetic defect: the bytes land under the SOURCE Session's
    /// directory in the destination tree, the row stores that leaf, and
    /// <c>TryDeleteSessionDirectory(importedSessionId)</c> then reclaims a directory that was never
    /// created — so every non-colliding import leaks its attachments permanently.
    /// </remarks>
    [Fact]
    public async Task An_import_never_writes_its_blobs_under_the_source_sessions_owner_segment()
    {

        ImportedSessionTransferRequest request = await BuildRequestAsync(Guid.NewGuid(), null);

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(request);

        Assert.True(
            completion.Result.IsSuccess,
            completion.Result.IsFailure ? completion.Result.Error.Message : string.Empty);

        Assert.False(
            Directory.Exists(Path.Combine(_destinationAttachments, _sourceSessionId.ToString("N"))),
            "The destination tree must carry no directory named after the source Session.");

        Assert.True(File.Exists(Path.Combine(
            _destinationAttachments,
            request.DestinationSessionId.ToString("N"),
            "note.txt")));

        // The stored leaf has to agree with the bytes, or the row points at nothing.
        Assert.Equal(
            request.DestinationSessionId.ToString("N") + "/note.txt",
            await _destination.ScalarStringAsync(
                "SELECT \"RelativePath\" FROM \"SessionAttachments\";",
                CancellationToken.None));

    }

    [Fact]
    public async Task An_import_copies_no_turn_claim_and_fabricates_no_replay_authority()
    {

        ImportedSessionTransferRequest request = await BuildRequestAsync(Guid.NewGuid(), null);

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(request);

        Assert.True(completion.Result.IsSuccess);

        // No claim table was installed in the destination at all, so a store that tried to copy one
        // would have failed loudly. The guard rows that do exist carry no final receipt digest.
        Assert.Equal(
            0,
            await _destination.ScalarLongAsync(
                "SELECT COUNT(*) FROM assistant_entry_finalizations WHERE FinalReceiptDigest IS NOT NULL;",
                CancellationToken.None));

    }

    [Fact]
    public async Task A_repeated_import_is_idempotent_by_its_operation_identity()
    {

        ImportedSessionTransferRequest request = await BuildRequestAsync(Guid.NewGuid(), null);

        Assert.True((await CommitAsync(request)).Result.IsSuccess);

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> replay =
            await CommitAsync(request);

        Assert.True(
            replay.Result.IsSuccess,
            replay.Result.IsFailure ? replay.Result.Error.Message : string.Empty);

        // One destination Session, not two. Idempotency is keyed only to the import operation.
        Assert.Equal(1, await CountDestinationAsync("Sessions"));
        Assert.Equal(1, await CountDestinationAsync("protected_session_transfer_intents"));

    }

    [Fact]
    public async Task The_same_operation_identity_cannot_be_reused_for_a_different_destination()
    {

        Guid operationId = Guid.NewGuid();

        ImportedSessionTransferRequest first = await BuildRequestAsync(operationId, null);

        Assert.True((await CommitAsync(first)).Result.IsSuccess);

        ImportedSessionTransferRequest retargeted = await BuildRequestAsync(operationId, null);

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> conflict =
            await CommitAsync(retargeted);

        Assert.True(conflict.Result.IsFailure);
        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, conflict.Result.Error.Code);

        Assert.Equal(1, await CountDestinationAsync("Sessions"));

    }

    [Fact]
    public async Task The_transfer_finalizer_runs_once_and_only_for_a_reopening_disposition()
    {

        ImportedSessionTransferRequest request = await BuildRequestAsync(Guid.NewGuid(), null);

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(request);

        Assert.True(completion.Result.IsSuccess);

        Assert.True((await completion.Finalizer.FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None)).IsFailure);

        Assert.Equal(4, await DestinationPhaseAsync());

    }

    public async Task DisposeAsync()
    {

        if (_source is not null)
        {

            await _source.DisposeAsync();

        }

        if (_destination is not null)
        {

            await _destination.DisposeAsync();

        }

    }

    public void Dispose()
    {

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a suite over.
        }

    }

    private static CovenantDigest Digest(byte seed) => new([.. Enumerable.Repeat(seed, 32)]);

    /// <summary>
    /// A Bloom the schema will accept: an overflow always has bits set, so an all-zero bitset is
    /// refused as a provenance nothing could have produced.
    /// </summary>
    private static byte[] NonZeroBloom() => [.. Enumerable.Repeat((byte)0x0F, 32)];

    private async Task<ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>> CommitAsync(
        ImportedSessionTransferRequest request,
        ProtectedTransferScope? scope = null)
    {

        ProtectedTransferScope resolved = scope
            ?? (request.CampaignMapping is { } mapping
                ? ProtectedTransferScope.ForCampaign(mapping.DestinationCampaignId)
                : ProtectedTransferScope.Global);

        await using ImportedSessionSourceLease sourceLease =
            ImportedSessionSourceLease.Adopt(
                await _source.OpenAdditionalConnectionAsync(CancellationToken.None),
                _sourceAttachments);

        await using CovenantProtectedTransferLease transferLease = new(
            new StubTransferRegistration(
                new CovenantExclusiveRecoveryOwner(
                    request.OperationId,
                    CovenantExclusiveOperation.ProtectedSessionTransfer,
                    request.TransferEffectDigest),
                resolved.ToOperationScope()));

        return await _store.CommitImportedSessionAsync(
            request,
            sourceLease,
            transferLease,
            new ProtectedSessionImportDestination(_destination.Connection, _destinationAttachments),
            CancellationToken.None);

    }

    /// <summary>
    /// Builds the request exactly as the importer must: every digest and count derived from the source
    /// the store will read, never invented.
    /// </summary>
    private async Task<ImportedSessionTransferRequest> BuildRequestAsync(
        Guid operationId,
        BackupSessionCampaignMapping? mapping)
    {

        Guid destinationSessionId = Guid.NewGuid();

        CovenantDigest sourceEvidence = Digest(0x11);

        ProtectedSessionTransferCounts counts = new(
            1,
            (ulong)await CountSourceAsync("Entries"),
            (ulong)await CountSourceAsync("SessionAttachments"),
            (ulong)await CountSourceAsync("SessionAttachments"),
            (ulong)await CountSourceAsync("assistant_entry_finalizations"),
            0);

        CovenantDigest manifest = await ComputeSourceManifestAsync();

        CovenantDigest binding = ProtectedSessionTransferDigests.DestinationBinding(
            mapping is null ? CovenantScope.Global : CovenantScope.Campaign,
            mapping?.DestinationCampaignId,
            mapping?.SourceCampaignId);

        CovenantDigest effect = ProtectedSessionTransferDigests.Effect(
            ProtectedSessionTransferKind.Import,
            operationId,
            _sourceSessionId,
            null,
            destinationSessionId,
            binding,
            sourceEvidence,
            manifest,
            counts).Value;

        return new ImportedSessionTransferRequest(
            operationId,
            _sourceSessionId,
            destinationSessionId,
            sourceEvidence,
            manifest,
            counts,
            mapping,
            binding,
            effect);

    }

    /// <summary>
    /// Reproduces the manifest the store computes, from the same source rows and the same order.
    /// </summary>
    private async Task<CovenantDigest> ComputeSourceManifestAsync()
    {

        List<byte[]> items = [];

        await using (SqliteCommand entries = _source.Connection.CreateCommand())
        {

            entries.CommandText =
                "SELECT \"Id\", \"Sequence\" FROM \"Entries\" WHERE \"SessionId\" = $id ORDER BY \"Sequence\", \"Id\";";

            _ = entries.Parameters.AddWithValue("$id", _sourceSessionId.ToString("D"));

            await using SqliteDataReader reader = await entries.ExecuteReaderAsync(CancellationToken.None);

            while (await reader.ReadAsync(CancellationToken.None))
            {

                items.Add(System.Text.Encoding.UTF8.GetBytes(
                    $"entry:{reader.GetString(0)}:{reader.GetInt32(1)}"));

            }

        }

        await using (SqliteCommand attachments = _source.Connection.CreateCommand())
        {

            attachments.CommandText = """
                SELECT "RelativePath", "ContentSha256", "ByteLength"
                FROM "SessionAttachments" WHERE "SessionId" = $id ORDER BY "Id";
                """;

            _ = attachments.Parameters.AddWithValue("$id", _sourceSessionId.ToString("D"));

            await using SqliteDataReader reader =
                await attachments.ExecuteReaderAsync(CancellationToken.None);

            while (await reader.ReadAsync(CancellationToken.None))
            {

                items.Add(System.Text.Encoding.UTF8.GetBytes(
                    $"attachment:{reader.GetString(0)}:"
                    + $"{Convert.ToHexString(Convert.FromHexString(reader.GetString(1)))}:{reader.GetInt64(2)}"));

            }

        }

        await using (SqliteCommand finalizations = _source.Connection.CreateCommand())
        {

            finalizations.CommandText = """
                SELECT AssistantEntryId FROM assistant_entry_finalizations
                WHERE SessionId = $id AND OutcomeCode = 1 ORDER BY AssistantEntryId;
                """;

            _ = finalizations.Parameters.AddWithValue("$id", _sourceSessionId.ToString("D"));

            await using SqliteDataReader reader =
                await finalizations.ExecuteReaderAsync(CancellationToken.None);

            while (await reader.ReadAsync(CancellationToken.None))
            {

                items.Add(System.Text.Encoding.UTF8.GetBytes($"finalization:{reader.GetString(0)}"));

            }

        }

        return ProtectedSessionTransferDigests.Manifest([.. items]);

    }

    private async Task SeedSourceSessionAsync()
    {

        string session = _sourceSessionId.ToString("D");

        string now = DateTimeOffset.UnixEpoch.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture);

        await _source.ExecuteAsync(
            $"""
             INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt",
                                     "Summary", "LastSummarizedMessageAt", "TotalTokensUsed",
                                     "TotalCostUsd", "UnsummarizedEntryCount", "ForkedFromSessionId")
             VALUES ('{session}', NULL, 'Imported', 0, '{now}', '{now}', NULL, NULL, 0, 0, 0, NULL);
             """,
            CancellationToken.None);

        string assistantEntryId = Guid.Parse("aaaaaaaa-1111-4222-8333-444444444444").ToString("D");

        await _source.ExecuteAsync(
            $"""
             INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                    "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
             VALUES ('{Guid.Parse("bbbbbbbb-1111-4222-8333-444444444444")}', '{session}', 0, 'ask',
                     '', '{now}', 1, NULL, NULL, NULL, 0);

             INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                    "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
             VALUES ('{assistantEntryId}', '{session}', 1, 'answer', 'model', '{now}', 2,
                     NULL, NULL, NULL, 0);
             """,
            CancellationToken.None);

        // The owner segment is seeded exactly as SessionAttachmentStore.BuildRelativePath writes it —
        // ToString("N") — because the archive under import was produced by that writer. Seeding the
        // dashed form instead would agree with the remap's own spelling and hide whether the remap
        // ever matches a real attachment tree.
        string owner = _sourceSessionId.ToString("N");

        string payload = Path.Combine(_sourceAttachments, owner, "note.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);

        byte[] bytes = "attached"u8.ToArray();

        await File.WriteAllBytesAsync(payload, bytes, CancellationToken.None);

        string hash = Convert.ToHexString(SHA256.HashData(bytes));

        await _source.ExecuteAsync(
            $"""
             INSERT INTO "SessionAttachments"
                 ("Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey",
                  "OriginalFileName", "Version", "RelativePath", "ContentSha256", "MimeType",
                  "ByteLength", "Kind", "CreatedAt", "SourceKind", "SourceStatus", "EncryptionVersion")
             VALUES ('{Guid.Parse("cccccccc-1111-4222-8333-444444444444")}', '{session}', NULL, NULL,
                     0, 'note', 'note.txt', 1, '{owner}/note.txt', '{hash}', 'text/plain',
                     {bytes.Length}, 0, '{now}', 'SnapshotOnly', 'NotApplicable', 0);
             """,
            CancellationToken.None);

        await using SqliteCommand finalization = _source.Connection.CreateCommand();

        finalization.CommandText = """
            INSERT INTO assistant_entry_finalizations (
                AssistantEntryId, SessionId, OutcomeCode, ContentSensitivityCode,
                ContentSensitivityDigest, RequestDigest, FinalReceiptDigest, SourceEvidenceDigest,
                FinalizedAtUtc)
            VALUES ($entry, $session, 1, 0, $sensitivity, $request, NULL, NULL, $now);
            """;

        _ = finalization.Parameters.AddWithValue("$entry", assistantEntryId);

        _ = finalization.Parameters.AddWithValue("$session", session);

        _ = finalization.Parameters.AddWithValue("$sensitivity", Digest(0x22).Bytes);

        _ = finalization.Parameters.AddWithValue("$request", Digest(0x33).Bytes);

        _ = finalization.Parameters.AddWithValue("$now", now);

        _ = await finalization.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task AddSensitivityLabelAsync()
    {

        await using SqliteCommand command = _source.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId, ArtifactRevision,
                ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES ($label, 1, $artifact, 1, 2, NULL, $bloom, $session, NULL, NULL, 1,
                    $content, $sensitivity, NULL, NULL, NULL, $labelDigest, $now);
            """;

        _ = command.Parameters.AddWithValue("$label", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue("$artifact", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue("$bloom", NonZeroBloom());

        _ = command.Parameters.AddWithValue("$session", _sourceSessionId.ToString("D"));

        _ = command.Parameters.AddWithValue("$content", Digest(0x44).Bytes);

        _ = command.Parameters.AddWithValue("$sensitivity", Digest(0x55).Bytes);

        _ = command.Parameters.AddWithValue("$labelDigest", Digest(0x66).Bytes);

        _ = command.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UnixEpoch.UtcDateTime.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffffffZ",
                CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private Task<long> CountSourceAsync(string table) =>
        _source.ScalarLongAsync($"SELECT COUNT(*) FROM \"{table}\";", CancellationToken.None);

    private Task<long> CountDestinationAsync(string table) =>
        _destination.ScalarLongAsync($"SELECT COUNT(*) FROM \"{table}\";", CancellationToken.None);

    private Task<long> DestinationPhaseAsync() =>
        _destination.ScalarLongAsync(
            "SELECT PhaseCode FROM protected_session_transfer_intents;",
            CancellationToken.None);

    /// <summary>
    /// The compound lease a caller already acquired. Only its snapshot matters here: the store must
    /// prove the lease closes the destination scope before it writes anything.
    /// </summary>
    private sealed class StubTransferRegistration(
        CovenantExclusiveRecoveryOwner owner,
        CovenantOperationScope scope) : ICovenantExclusiveLeaseRegistration
    {

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.NewGuid(),
            1,
            CovenantLeaseKind.ProtectedTransfer,
            CovenantLeaseCoverage.Scoped,
            scope,
            Guid.NewGuid(),
            1,
            1,
            1,
            null,
            null,
            null,
            null,
            owner,
            false);

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
