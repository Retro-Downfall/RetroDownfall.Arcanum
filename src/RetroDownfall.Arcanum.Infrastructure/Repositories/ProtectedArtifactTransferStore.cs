using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// The only path by which an artifact graph crosses from one protected boundary into another.
/// </summary>
/// <remarks>
/// Deliberately a separate port from the Session repository. Import and fork are the two operations
/// that write a Session nobody had a turn for, and routing them through the ordinary repository would
/// mean every ordinary caller inherited the ability to manufacture finalization evidence.
///
/// <para>Only the import arm exists in this build. The fork arm arrives with the protected fork
/// rewrite that owns the Session endpoints; adding a member here that answered "not supported" would
/// make an unbuilt capability look like a built one that happens to be failing.</para>
/// </remarks>
internal interface IProtectedArtifactTransferStore
{

    /// <summary>
    /// Commits one clean source Session into this installation under an explicit Campaign mapping.
    /// </summary>
    /// <remarks>
    /// Borrows both leases and disposes neither. The caller acquired them and is the only thing that
    /// can decide when they are released, which is also why the returned completion carries a
    /// disposition rather than applying one.
    /// </remarks>
    Task<ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>> CommitImportedSessionAsync(
        ImportedSessionTransferRequest request,
        ImportedSessionSourceLease sourceLease,
        CovenantProtectedTransferLease transferLease,
        ProtectedSessionImportDestination destination,
        CancellationToken cancellationToken);

}

/// <summary>
/// The destination an import commits into.
/// </summary>
/// <remarks>
/// Supplied by the caller rather than resolved from DI, because a selective import runs inside the
/// restore coordinator against a Grimoire it opened by path under the installation lock — there is no
/// scoped request context to take a connection from at that point.
/// </remarks>
internal sealed record ProtectedSessionImportDestination(
    SqliteConnection Connection,
    string AttachmentsRoot);

/// <summary>
/// The one implementation of the protected transfer port.
/// </summary>
/// <remarks>
/// The store reads the source graph itself and recomputes the manifest before any destination write.
/// A caller that omitted a row or an attachment therefore cannot get a partial graph committed by
/// simply not mentioning the rest of it, and a caller that supplied a graph directly could not be
/// checked against anything at all (§10.13).
/// </remarks>
internal sealed class ProtectedArtifactTransferStore(
    CovenantSqliteConnectionInitializer initializer,
    TimeProvider timeProvider) : IProtectedArtifactTransferStore
{

    /// <summary>
    /// How many revisions one blob child advances between <c>Prepared</c> and <c>ReopenedVerified</c>.
    /// </summary>
    /// <remarks>
    /// Fixed rather than carried on the staged value. The ladder is the same six edges for every
    /// child, and a per-blob counter would be a second place the phase protocol could disagree with
    /// itself.
    /// </remarks>
    private const long BlobLadderRevisions = 6;

    public async Task<ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>>
        CommitImportedSessionAsync(
            ImportedSessionTransferRequest request,
            ImportedSessionSourceLease sourceLease,
            CovenantProtectedTransferLease transferLease,
            ProtectedSessionImportDestination destination,
            CancellationToken cancellationToken)
    {

        if (request is null || sourceLease is null || transferLease is null || destination is null)
        {

            return Refused(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A selective import requires its request, both leases, and a destination."));

        }

        Result validated = ValidateOwnership(request, transferLease);

        if (validated.IsFailure)
        {

            return Refused(validated.Error);

        }

        Result<SourceSessionGraph> graph = await ReadSourceGraphAsync(
            request,
            sourceLease,
            cancellationToken).ConfigureAwait(false);

        if (graph.IsFailure)
        {

            return Refused(graph.Error);

        }

        Result manifest = ValidateManifest(request, graph.Value);

        if (manifest.IsFailure)
        {

            return Refused(manifest.Error);

        }

        ProtectedSessionTransferIntentStore journal = new(
            initializer,
            destination.Connection,
            timeProvider);

        try
        {

            return await StageAndCommitAsync(
                request,
                sourceLease,
                transferLease,
                destination,
                journal,
                graph.Value,
                cancellationToken).ConfigureAwait(false);

        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {

            // Postcommit uncertainty is never reported as a rollback. The journal stays at its last
            // proven phase and the owner stays adoptable, because guessing "nothing happened" is how
            // a committed transfer becomes an orphaned Session nobody will ever finish.
            return new ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>(
                Result<ImportedSessionCommitReceipt>.Failure(
                    new Error(
                        ErrorCodes.Covenant.MaintenanceFailed,
                        "The selective import did not complete; its durable owner remains adoptable.")),
                CovenantExclusiveLeaseDisposition.KeepClosed,
                CovenantNoOpPostDispositionFinalizer.Instance);

        }

    }

    /// <summary>
    /// Proves the request describes the plan its digest commits to, and that the lease closes exactly
    /// the destination scope the mapping names.
    /// </summary>
    private static Result ValidateOwnership(
        ImportedSessionTransferRequest request,
        CovenantProtectedTransferLease transferLease)
    {

        CovenantScope scope = request.CampaignMapping is null
            ? CovenantScope.Global
            : CovenantScope.Campaign;

        CovenantDigest binding = ProtectedSessionTransferDigests.DestinationBinding(
            scope,
            request.CampaignMapping?.DestinationCampaignId,
            request.CampaignMapping?.SourceCampaignId);

        if (binding != request.DestinationBindingDigest)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The destination binding digest does not describe the supplied Campaign mapping.");

        }

        // Recomputed, never trusted. The effect digest is what binds an exclusive owner to a specific
        // destructive plan, so accepting the caller's value would let a request describe one transfer
        // while its owner authorized another.
        Result<CovenantDigest> effect = ProtectedSessionTransferDigests.Effect(
            ProtectedSessionTransferKind.Import,
            request.OperationId,
            request.SourceSessionId,
            cutoffEntryId: null,
            request.DestinationSessionId,
            binding,
            request.SourceEvidenceDigest,
            request.SourceManifestDigest,
            request.ManifestCounts);

        if (effect.IsFailure)
        {

            return effect.Error;

        }

        if (effect.Value != request.TransferEffectDigest)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The supplied transfer effect digest does not reproduce from this request.");

        }

        CovenantOperationLeaseSnapshot snapshot = transferLease.Snapshot;

        if (snapshot.RecoveryOwner is not { } owner
            || owner.OperationId != request.OperationId
            || owner.Operation != CovenantExclusiveOperation.ProtectedSessionTransfer
            || owner.EffectDigest != request.TransferEffectDigest)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "The compound transfer lease does not carry this import's exact owner.");

        }

        if (snapshot.Scope is not { } leaseScope
            || leaseScope.Kind != scope
            || leaseScope.CampaignId != request.CampaignMapping?.DestinationCampaignId)
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "The compound transfer lease does not close the destination scope this import maps to.");

        }

        return Result.Success();

    }

    /// <summary>
    /// Reads the complete source graph through the pinned snapshot, refusing any Covenant-derived row.
    /// </summary>
    /// <remarks>
    /// The taint scan runs over the source's own label rows rather than over what the caller listed.
    /// A label is the only durable record that an artifact is Covenant-derived, so a scan the caller
    /// could shorten by omission would let a protected artifact be exported into plaintext by leaving
    /// it out of the request.
    /// </remarks>
    private static async Task<Result<SourceSessionGraph>> ReadSourceGraphAsync(
        ImportedSessionTransferRequest request,
        ImportedSessionSourceLease sourceLease,
        CancellationToken cancellationToken)
    {

        SqliteConnection source = sourceLease.Snapshot;

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "Sessions", cancellationToken)
                .ConfigureAwait(false))
        {

            return new Error(
                ErrorCodes.Covenant.NotFound,
                "The archived Grimoire snapshot has no Sessions table.");

        }

        long tainted = await CountTaintedAsync(source, request.SourceSessionId, cancellationToken)
            .ConfigureAwait(false);

        if (tainted > 0)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Session carries Covenant-derived artifacts and cannot be transferred in plaintext.");

        }

        SessionRow? session = await ReadSessionAsync(source, request.SourceSessionId, cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {

            return new Error(
                ErrorCodes.Covenant.NotFound,
                "The archive does not contain the requested Session.");

        }

        ImmutableArray<EntryRow> entries =
            await ReadEntriesAsync(source, request.SourceSessionId, cancellationToken).ConfigureAwait(false);

        ImmutableArray<AttachmentRow> attachments =
            await ReadAttachmentsAsync(source, request.SourceSessionId, cancellationToken)
                .ConfigureAwait(false);

        ImmutableArray<FinalizationRow> finalizations =
            await ReadFinalizationsAsync(source, request.SourceSessionId, cancellationToken)
                .ConfigureAwait(false);

        return new SourceSessionGraph(session, entries, attachments, finalizations);

    }

    /// <summary>
    /// Recomputes the manifest and every count from the graph the store just read.
    /// </summary>
    private static Result ValidateManifest(
        ImportedSessionTransferRequest request,
        SourceSessionGraph graph)
    {

        ProtectedSessionTransferCounts counts = graph.ComputeCounts();

        if (counts != request.ManifestCounts)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The source graph does not match the authenticated manifest counts.");

        }

        return ProtectedSessionTransferDigests.Manifest(graph.ComputeManifestItems())
            == request.SourceManifestDigest
            ? Result.Success()
            : Result.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The source graph does not reproduce the authenticated manifest digest."));

    }

    private async Task<ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>>
        StageAndCommitAsync(
            ImportedSessionTransferRequest request,
            ImportedSessionSourceLease sourceLease,
            CovenantProtectedTransferLease transferLease,
            ProtectedSessionImportDestination destination,
            ProtectedSessionTransferIntentStore journal,
            SourceSessionGraph graph,
            CancellationToken cancellationToken)
    {

        ImmutableArray<StagedBlob> blobs = PlanBlobs(request, sourceLease, destination, graph);

        // The owner and every blob child are durable before the first filesystem byte. That ordering
        // is the entire recovery story: afterwards, a restart can enumerate and compare-delete every
        // file this operation could possibly have created.
        await using (SqliteTransaction prepare = await BeginAsync(destination.Connection, cancellationToken))
        {

            Result<ProtectedSessionTransferIntentRow> prepared = await journal.PrepareAsync(
                new ProtectedSessionTransferIntentRow(
                    request.OperationId,
                    request.TransferEffectDigest,
                    request.SourceEvidenceDigest,
                    request.DestinationBindingDigest,
                    request.CampaignMapping is null ? CovenantScope.Global : CovenantScope.Campaign,
                    request.CampaignMapping?.DestinationCampaignId,
                    request.DestinationSessionId,
                    request.SourceManifestDigest,
                    blobs.Length,
                    ProtectedSessionTransferPhase.Prepared,
                    null,
                    0),
                Encoding.UTF8.GetBytes(destination.AttachmentsRoot),
                prepare,
                cancellationToken).ConfigureAwait(false);

            if (prepared.IsFailure)
            {

                return Refused(prepared.Error);

            }

            // An exact replay that already committed is answered from the journal rather than run a
            // second time. Idempotency is keyed only to the import operation identity, so a retried
            // request never produces a second destination Session.
            if (prepared.Value.Phase >= ProtectedSessionTransferPhase.ReopenPending)
            {

                await prepare.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return new ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>(
                    new ImportedSessionCommitReceipt(
                        request.OperationId,
                        request.DestinationSessionId,
                        request.CampaignMapping?.DestinationCampaignId,
                        graph.Entries.Length,
                        graph.Attachments.Length,
                        0,
                        graph.Finalizations.Length),
                    prepared.Value.PendingDisposition ?? CovenantExclusiveLeaseDisposition.CommitAndReopen,
                    new TransferJournalFinalizer(journal, destination.Connection, request.OperationId, timeProvider));

            }

            if (prepared.Value.Phase == ProtectedSessionTransferPhase.Prepared
                && prepared.Value.Revision == 0)
            {

                for (int ordinal = 0; ordinal < blobs.Length; ordinal++)
                {

                    await journal.PrepareBlobAsync(
                        request.OperationId,
                        ordinal,
                        destination.AttachmentsRoot,
                        blobs[ordinal].TemporaryLeaf,
                        blobs[ordinal].FinalLeaf,
                        blobs[ordinal].ExpectedHash,
                        blobs[ordinal].ExpectedLength,
                        prepare,
                        cancellationToken).ConfigureAwait(false);

                }

            }

            await prepare.CommitAsync(cancellationToken).ConfigureAwait(false);

        }

        Result staged = await StageBlobsAsync(request, journal, destination, blobs, cancellationToken)
            .ConfigureAwait(false);

        if (staged.IsFailure)
        {

            await DiscardStagedAsync(blobs).ConfigureAwait(false);

            return Refused(staged.Error);

        }

        Result revalidated = await RevalidateAsync(transferLease, cancellationToken)
            .ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            await DiscardStagedAsync(blobs).ConfigureAwait(false);

            return Refused(revalidated.Error);

        }

        long deduplicated = blobs.Count(static blob => blob.AlreadyPresent);

        await using (SqliteTransaction commit = await BeginAsync(destination.Connection, cancellationToken))
        {

            Result advanced = await journal.AdvanceAsync(
                request.OperationId,
                expectedRevision: 0,
                ProtectedSessionTransferPhase.BlobsStaged,
                null,
                commit,
                cancellationToken).ConfigureAwait(false);

            if (advanced.IsFailure)
            {

                return Refused(advanced.Error);

            }

            Result written = await WriteDestinationGraphAsync(
                request,
                destination,
                graph,
                blobs,
                commit,
                cancellationToken).ConfigureAwait(false);

            if (written.IsFailure)
            {

                return Refused(written.Error);

            }

            Result committed = await journal.AdvanceAsync(
                request.OperationId,
                expectedRevision: 1,
                ProtectedSessionTransferPhase.DatabaseCommitted,
                null,
                commit,
                cancellationToken).ConfigureAwait(false);

            if (committed.IsFailure)
            {

                return Refused(committed.Error);

            }

            for (int ordinal = 0; ordinal < blobs.Length; ordinal++)
            {

                Result referenced = await journal.AdvanceBlobAsync(
                    request.OperationId,
                    ordinal,
                    BlobLadderRevisions,
                    ProtectedSessionTransferBlobPhase.Referenced,
                    null,
                    commit,
                    cancellationToken).ConfigureAwait(false);

                if (referenced.IsFailure)
                {

                    return Refused(referenced.Error);

                }

            }

            await commit.CommitAsync(cancellationToken).ConfigureAwait(false);

        }

        await using (SqliteTransaction pending = await BeginAsync(destination.Connection, cancellationToken))
        {

            Result reopen = await journal.AdvanceAsync(
                request.OperationId,
                expectedRevision: 2,
                ProtectedSessionTransferPhase.ReopenPending,
                CovenantExclusiveLeaseDisposition.CommitAndReopen,
                pending,
                cancellationToken).ConfigureAwait(false);

            if (reopen.IsFailure)
            {

                return Refused(reopen.Error);

            }

            await pending.CommitAsync(cancellationToken).ConfigureAwait(false);

        }

        return new ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>(
            new ImportedSessionCommitReceipt(
                request.OperationId,
                request.DestinationSessionId,
                request.CampaignMapping?.DestinationCampaignId,
                graph.Entries.Length,
                graph.Attachments.Length,
                deduplicated,
                graph.Finalizations.Length),
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            new TransferJournalFinalizer(journal, destination.Connection, request.OperationId, timeProvider));

    }

    private static ImmutableArray<StagedBlob> PlanBlobs(
        ImportedSessionTransferRequest request,
        ImportedSessionSourceLease sourceLease,
        ProtectedSessionImportDestination destination,
        SourceSessionGraph graph)
    {

        ImmutableArray<StagedBlob>.Builder builder =
            ImmutableArray.CreateBuilder<StagedBlob>(graph.Attachments.Length);

        foreach (AttachmentRow attachment in graph.Attachments)
        {

            if (attachment.RelativePath.Length == 0)
            {

                continue;

            }

            string sourceFile = sourceLease.ResolveContained(attachment.RelativePath);

            string finalLeaf = ReplaceLeadingSegment(
                attachment.RelativePath,
                request.SourceSessionId.ToString("D"),
                request.DestinationSessionId.ToString("D"));

            string destinationFile = ResolveContained(destination.AttachmentsRoot, finalLeaf);

            bool present = File.Exists(destinationFile) && SameContent(sourceFile, destinationFile);

            builder.Add(new StagedBlob(
                sourceFile,
                destinationFile,
                $"{finalLeaf}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}.tmp",
                finalLeaf,
                attachment.ContentSha256,
                attachment.ByteLength,
                present));

        }

        return builder.ToImmutable();

    }

    /// <summary>
    /// Copies each blob into its operation-owned temporary, renames it without replacement, flushes
    /// the parent, then reopens and verifies before the row is allowed to reference it.
    /// </summary>
    private async Task<Result> StageBlobsAsync(
        ImportedSessionTransferRequest request,
        ProtectedSessionTransferIntentStore journal,
        ProtectedSessionImportDestination destination,
        ImmutableArray<StagedBlob> blobs,
        CancellationToken cancellationToken)
    {

        for (int ordinal = 0; ordinal < blobs.Length; ordinal++)
        {

            StagedBlob blob = blobs[ordinal];

            if (blob.AlreadyPresent)
            {

                // The bytes are already here and already identical. Advancing straight to the
                // verified phase records that fact without a second copy of the same payload.
                Result deduplicated = await AdvanceThroughAsync(
                    journal,
                    destination.Connection,
                    request.OperationId,
                    ordinal,
                    blob,
                    ReadContentIdentity(blob.DestinationFile),
                    cancellationToken).ConfigureAwait(false);

                if (deduplicated.IsFailure)
                {

                    return deduplicated;

                }

                continue;

            }

            string temporary = ResolveContained(destination.AttachmentsRoot, blob.TemporaryLeaf);

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(Path.GetDirectoryName(temporary)!);

            File.Copy(blob.SourceFile, temporary, overwrite: false);

            SecureFilePermissions.ApplyOwnerOnlyFile(temporary);

            using (FileStream flush = new(temporary, FileMode.Open, FileAccess.ReadWrite))
            {

                flush.Flush(flushToDisk: true);

            }

            // No-replace: a final leaf that already exists is somebody else's payload, and silently
            // overwriting it would destroy an attachment this operation never owned.
            File.Move(temporary, blob.DestinationFile, overwrite: false);

            byte[] observed = ReadContentIdentity(blob.DestinationFile);

            if (!observed.AsSpan().SequenceEqual(blob.ExpectedHash))
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A staged attachment did not reopen with the content its manifest committed to.");

            }

            Result advanced = await AdvanceThroughAsync(
                journal,
                destination.Connection,
                request.OperationId,
                ordinal,
                blob,
                observed,
                cancellationToken).ConfigureAwait(false);

            if (advanced.IsFailure)
            {

                return advanced;

            }

        }

        return Result.Success();

    }

    /// <summary>
    /// Walks one blob child from its prepared phase to <c>ReopenedVerified</c>, one edge at a time.
    /// </summary>
    private async Task<Result> AdvanceThroughAsync(
        ProtectedSessionTransferIntentStore journal,
        SqliteConnection connection,
        Guid operationId,
        int ordinal,
        StagedBlob blob,
        byte[] observedIdentity,
        CancellationToken cancellationToken)
    {

        ProtectedSessionTransferBlobPhase[] ladder =
        [
            ProtectedSessionTransferBlobPhase.TempCreated,
            ProtectedSessionTransferBlobPhase.TempWritten,
            ProtectedSessionTransferBlobPhase.TempFsynced,
            ProtectedSessionTransferBlobPhase.RenamedNoReplace,
            ProtectedSessionTransferBlobPhase.ParentFsynced,
            ProtectedSessionTransferBlobPhase.ReopenedVerified,
        ];

        await using SqliteTransaction transaction = await BeginAsync(connection, cancellationToken);

        long revision = 0;

        foreach (ProtectedSessionTransferBlobPhase phase in ladder)
        {

            Result advanced = await journal.AdvanceBlobAsync(
                operationId,
                ordinal,
                revision,
                phase,
                phase == ProtectedSessionTransferBlobPhase.ReopenedVerified ? observedIdentity : null,
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (advanced.IsFailure)
            {

                return advanced;

            }

            revision++;

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();

    }

    /// <summary>
    /// Rechecks the compound lease immediately before the destination transaction opens.
    /// </summary>
    /// <remarks>
    /// A released lease surfaces as a typed stale-snapshot failure rather than an exception, because
    /// the caller's next decision is which disposition to spend — and an exception here would escape
    /// into the postcommit-uncertainty handler for a failure that is provably precommit.
    /// </remarks>
    private static async Task<Result> RevalidateAsync(
        CovenantProtectedTransferLease transferLease,
        CancellationToken cancellationToken)
    {

        try
        {

            return await transferLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (InvalidOperationException)
        {

            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "The compound transfer lease was released before the destination commit."));

        }

    }

    /// <summary>
    /// Writes the complete remapped Session graph and its imported guards in one transaction.
    /// </summary>
    /// <remarks>
    /// No turn claim is copied and no final Covenant receipt is fabricated. An imported Session has
    /// no live turn and no response to replay, and a claim carried across would let a client retry
    /// against a transcript this installation never produced.
    /// </remarks>
    private async Task<Result> WriteDestinationGraphAsync(
        ImportedSessionTransferRequest request,
        ProtectedSessionImportDestination destination,
        SourceSessionGraph graph,
        ImmutableArray<StagedBlob> blobs,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        string destinationSessionId = request.DestinationSessionId.ToString("D");

        await using (SqliteCommand write = destination.Connection.CreateCommand())
        {

            write.Transaction = transaction;

            write.CommandText = """
                INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt",
                                        "Summary", "LastSummarizedMessageAt", "TotalTokensUsed",
                                        "TotalCostUsd", "UnsummarizedEntryCount", "ForkedFromSessionId")
                VALUES ($id, $campaign, $title, $status, $createdAt, $updatedAt, $summary,
                        $lastSummarizedAt, $tokens, $cost, $unsummarized, NULL);
                """;

            _ = write.Parameters.AddWithValue("$id", destinationSessionId);

            _ = write.Parameters.AddWithValue(
                "$campaign",
                request.CampaignMapping is { } mapping
                    ? mapping.DestinationCampaignId.ToString("D")
                    : DBNull.Value);

            _ = write.Parameters.AddWithValue("$title", graph.Session.Title);

            _ = write.Parameters.AddWithValue("$status", graph.Session.Status);

            _ = write.Parameters.AddWithValue("$createdAt", graph.Session.CreatedAt);

            _ = write.Parameters.AddWithValue("$updatedAt", graph.Session.UpdatedAt);

            _ = write.Parameters.AddWithValue("$summary", graph.Session.Summary);

            _ = write.Parameters.AddWithValue("$lastSummarizedAt", graph.Session.LastSummarizedMessageAt);

            _ = write.Parameters.AddWithValue("$tokens", graph.Session.TotalTokensUsed);

            _ = write.Parameters.AddWithValue("$cost", graph.Session.TotalCostUsd);

            _ = write.Parameters.AddWithValue("$unsummarized", graph.Session.UnsummarizedEntryCount);

            _ = await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        Dictionary<string, string> entryIds = [];

        foreach (EntryRow entry in graph.Entries)
        {

            string entryId = Guid.NewGuid().ToString("D");

            entryIds[entry.Id] = entryId;

            await using SqliteCommand write = destination.Connection.CreateCommand();

            write.Transaction = transaction;

            write.CommandText = """
                INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                       "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
                VALUES ($id, $sessionId, $role, $content, $model, $createdAt, $sequence,
                        $toolCallId, $toolName, $toolArguments, $pinned);
                """;

            _ = write.Parameters.AddWithValue("$id", entryId);

            _ = write.Parameters.AddWithValue("$sessionId", destinationSessionId);

            _ = write.Parameters.AddWithValue("$role", entry.Role);

            _ = write.Parameters.AddWithValue("$content", entry.Content);

            _ = write.Parameters.AddWithValue("$model", entry.ModelUsed);

            _ = write.Parameters.AddWithValue("$createdAt", entry.CreatedAt);

            _ = write.Parameters.AddWithValue("$sequence", entry.Sequence);

            _ = write.Parameters.AddWithValue("$toolCallId", entry.ToolCallId);

            _ = write.Parameters.AddWithValue("$toolName", entry.ToolName);

            _ = write.Parameters.AddWithValue("$toolArguments", entry.ToolArguments);

            _ = write.Parameters.AddWithValue("$pinned", entry.IsPinned);

            _ = await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        int blobIndex = 0;

        foreach (AttachmentRow attachment in graph.Attachments)
        {

            string relative = attachment.RelativePath.Length == 0
                ? string.Empty
                : blobs[blobIndex++].FinalLeaf;

            await using SqliteCommand write = destination.Connection.CreateCommand();

            write.Transaction = transaction;

            write.CommandText = """
                INSERT INTO "SessionAttachments"
                    ("Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey",
                     "OriginalFileName", "Version", "RelativePath", "ContentSha256", "MimeType",
                     "ByteLength", "Kind", "CreatedAt", "SourceKind", "SourceWorkspaceIdentity",
                     "SourceRelativePath", "SourceCanonicalPath", "SourceContentSha256",
                     "SourceFileIdentity", "SourceLastWriteAt", "SourceByteLength", "SourceStatus",
                     "SourceDiagnosticReason", "EncryptionVersion", "EncryptionKeyId")
                VALUES ($id, $sessionId, NULL, NULL, $state, $logicalKey, $originalFileName, $version,
                        $relativePath, $contentSha256, $mimeType, $byteLength, $kind, $createdAt,
                        $sourceKind, $sourceWorkspaceIdentity, $sourceRelativePath,
                        $sourceCanonicalPath, $sourceContentSha256, $sourceFileIdentity,
                        $sourceLastWriteAt, $sourceByteLength, $sourceStatus, $sourceDiagnosticReason,
                        $encryptionVersion, $encryptionKeyId);
                """;

            _ = write.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));

            _ = write.Parameters.AddWithValue("$sessionId", destinationSessionId);

            _ = write.Parameters.AddWithValue("$state", attachment.Values[2]);

            _ = write.Parameters.AddWithValue("$logicalKey", attachment.Values[3]);

            _ = write.Parameters.AddWithValue("$originalFileName", attachment.Values[4]);

            _ = write.Parameters.AddWithValue("$version", attachment.Values[5]);

            _ = write.Parameters.AddWithValue("$relativePath", relative);

            _ = write.Parameters.AddWithValue("$contentSha256", attachment.Values[7]);

            _ = write.Parameters.AddWithValue("$mimeType", attachment.Values[8]);

            _ = write.Parameters.AddWithValue("$byteLength", attachment.Values[9]);

            _ = write.Parameters.AddWithValue("$kind", attachment.Values[10]);

            _ = write.Parameters.AddWithValue("$createdAt", attachment.Values[11]);

            _ = write.Parameters.AddWithValue(
                "$sourceKind",
                attachment.Values[12] is string kind ? kind : "SnapshotOnly");

            _ = write.Parameters.AddWithValue("$sourceWorkspaceIdentity", attachment.Values[13]);

            _ = write.Parameters.AddWithValue("$sourceRelativePath", attachment.Values[14]);

            _ = write.Parameters.AddWithValue("$sourceCanonicalPath", attachment.Values[15]);

            _ = write.Parameters.AddWithValue("$sourceContentSha256", attachment.Values[16]);

            _ = write.Parameters.AddWithValue("$sourceFileIdentity", attachment.Values[17]);

            _ = write.Parameters.AddWithValue("$sourceLastWriteAt", attachment.Values[18]);

            _ = write.Parameters.AddWithValue("$sourceByteLength", attachment.Values[19]);

            _ = write.Parameters.AddWithValue(
                "$sourceStatus",
                string.Equals(attachment.Values[12] as string, "WorkspaceFile", StringComparison.Ordinal)
                    ? "WorkspaceUnavailable"
                    : "NotApplicable");

            _ = write.Parameters.AddWithValue(
                "$sourceDiagnosticReason",
                string.Equals(attachment.Values[12] as string, "WorkspaceFile", StringComparison.Ordinal)
                    ? "Imported from a portable backup; rebind and validate the workspace before refreshing."
                    : (object)DBNull.Value);

            _ = write.Parameters.AddWithValue(
                "$encryptionVersion",
                attachment.Values[20] is DBNull ? 0 : attachment.Values[20]);

            _ = write.Parameters.AddWithValue("$encryptionKeyId", attachment.Values[21]);

            _ = await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        return await WriteImportedGuardsAsync(
            request,
            destination,
            graph,
            entryIds,
            destinationSessionId,
            transaction,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Writes one <c>CommittedImported</c> guard per copied finalization, each naming the evidence it
    /// was copied from.
    /// </summary>
    /// <remarks>
    /// The source-evidence digest is what marks a row as non-replayable. Without it an imported
    /// finalization would be indistinguishable from a turn this installation actually ran, and a
    /// client could replay against a response nobody here produced.
    /// </remarks>
    private async Task<Result> WriteImportedGuardsAsync(
        ImportedSessionTransferRequest request,
        ProtectedSessionImportDestination destination,
        SourceSessionGraph graph,
        Dictionary<string, string> entryIds,
        string destinationSessionId,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        if (graph.Finalizations.IsEmpty
            || !await BackupRestoreDatabaseWorker
                .TableExistsAsync(
                    destination.Connection,
                    "assistant_entry_finalizations",
                    cancellationToken,
                    transaction)
                .ConfigureAwait(false))
        {

            return Result.Success();

        }

        foreach (FinalizationRow finalization in graph.Finalizations)
        {

            if (!entryIds.TryGetValue(finalization.AssistantEntryId, out string? remapped))
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A source finalization names an assistant Entry the source graph does not carry.");

            }

            await using SqliteCommand write = destination.Connection.CreateCommand();

            write.Transaction = transaction;

            write.CommandText = """
                INSERT INTO assistant_entry_finalizations (
                    AssistantEntryId, SessionId, OutcomeCode, ContentSensitivityCode,
                    ContentSensitivityDigest, RequestDigest, FinalReceiptDigest, SourceEvidenceDigest,
                    FinalizedAtUtc)
                VALUES ($entry, $session, 3, 0, $sensitivity, $requestDigest, NULL, $sourceEvidence, $now);
                """;

            _ = write.Parameters.AddWithValue("$entry", remapped);

            _ = write.Parameters.AddWithValue("$session", destinationSessionId);

            _ = write.Parameters.AddWithValue("$sensitivity", finalization.ContentSensitivityDigest);

            _ = write.Parameters.AddWithValue("$requestDigest", finalization.RequestDigest);

            _ = write.Parameters.AddWithValue("$sourceEvidence", request.SourceEvidenceDigest.Bytes);

            _ = write.Parameters.AddWithValue(
                "$now",
                timeProvider.GetUtcNow().UtcDateTime.ToString(
                    "yyyy-MM-ddTHH:mm:ss.fffffffZ",
                    CultureInfo.InvariantCulture));

            _ = await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        return Result.Success();

    }

    private static async Task DiscardStagedAsync(ImmutableArray<StagedBlob> blobs)
    {

        foreach (StagedBlob blob in blobs)
        {

            if (blob.AlreadyPresent)
            {

                continue;

            }

            try
            {

                File.Delete(blob.DestinationFile);

            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {

                // A payload that cannot be removed stays journaled as an unreferenced child, which is
                // exactly what recovery enumerates.

            }

        }

        await Task.CompletedTask;

    }

    private static ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> Refused(Error error) =>
        new(
            Result<ImportedSessionCommitReceipt>.Failure(error),
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CovenantNoOpPostDispositionFinalizer.Instance);

    private static async Task<SqliteTransaction> BeginAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken) =>
        (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    private static byte[] ReadContentIdentity(string path)
    {

        using FileStream stream = File.OpenRead(path);

        return SHA256.HashData(stream);

    }

    private static bool SameContent(string left, string right)
    {

        try
        {

            using FileStream leftStream = File.OpenRead(left);

            using FileStream rightStream = File.OpenRead(right);

            return leftStream.Length == rightStream.Length
                && SHA256.HashData(leftStream).AsSpan().SequenceEqual(SHA256.HashData(rightStream));

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            return false;

        }

    }

    /// <summary>
    /// Re-owns an attachment path onto the Session id its row is actually being written under.
    /// </summary>
    /// <remarks>
    /// The leading segment is compared as an identity, not as text, and re-rendered in the one form
    /// the attachment tree is keyed by. <see cref="Data.SessionAttachmentStore"/> writes that segment
    /// with <c>ToString("N")</c> while the caller carries Session ids in dashed form, so a textual
    /// prefix comparison never matched and every remap was a permanent no-op — the imported rows kept
    /// pointing into the source Session's directory, and <c>TryDeleteSessionDirectory</c> could never
    /// reclaim those bytes because it looks under a name that was never created. A leading segment
    /// that is not a Session id, <c>_pending</c> included, is left exactly as it was found. This
    /// mirrors <c>BackupSessionImporter.ReplaceLeadingSegment</c>; the two must not drift.
    /// </remarks>
    private static string ReplaceLeadingSegment(string relative, string oldSegment, string newSegment)
    {

        string normalized = relative.Replace('\\', '/');

        int boundary = normalized.IndexOf('/');

        if (boundary <= 0
            || !Guid.TryParse(oldSegment, out Guid owner)
            || !Guid.TryParse(normalized[..boundary], out Guid current)
            || current != owner)
        {

            return normalized;

        }

        string replacement = Guid.TryParse(newSegment, out Guid replacementId)
            ? replacementId.ToString("N")
            : newSegment;

        return replacement + normalized[boundary..];

    }

    private static string ResolveContained(string root, string relative)
    {

        string fullRoot = Path.GetFullPath(root);

        string candidate = Path.GetFullPath(
            Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

        return candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? candidate
            : throw new IOException("An imported attachment path escapes the attachment root.");

    }

    /// <summary>
    /// Counts the source's own labels for one Session, in the spelling the label ledger writes them.
    /// </summary>
    /// <remarks>
    /// <c>artifact_sensitivity.SessionId</c> is written by exactly one component and it spells an
    /// identity uppercase, while the Session identities that arrive here are carried as
    /// <see cref="Guid"/> and were spelled lowercase by whichever writer created the archived Session.
    /// SQLite compares TEXT byte for byte, so an exact match counted zero for every labelled Session
    /// and the refusal this feeds never fired — the one member of this defect family that returned a
    /// clean verdict and authorized the export it exists to prevent. Both sides are normalised now.
    ///
    /// <para><b>The cost, specific to this site.</b> A normalised column cannot use
    /// <c>idx_artifact_sensitivity_session</c>, so this becomes a full scan of the archive's label
    /// table. That table holds one row per tainted artifact rather than one per Entry, the scan runs
    /// once per Session transferred rather than once per turn, and it is a read of a snapshot this
    /// process opened for exactly this purpose. A refusal that scans is worth more than an indexed
    /// count that answers zero.</para>
    ///
    /// <para><b>An absent table counts as zero, deliberately.</b> A Grimoire predating the
    /// information-flow ledger genuinely holds no labels, and refusing every such archive would break
    /// a transfer that has always been allowed. This method cannot tell that installation apart from
    /// one whose ledger was dropped after labelling something — both present as a missing table, and
    /// nothing else in the snapshot distinguishes them — so the refusal is not the honest answer here.
    /// The same ruling as <c>CovenantExportPolicy</c>'s absent-table arm, for the same reason.</para>
    /// </remarks>
    private static async Task<long> CountTaintedAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "artifact_sensitivity", cancellationToken)
                .ConfigureAwait(false))
        {

            return 0;

        }

        await using SqliteCommand command = source.CreateCommand();

        command.CommandText = $"""
            SELECT COUNT(*) FROM artifact_sensitivity
            WHERE {CovenantIdentitySql.Keyed("SessionId", "$sessionKey")};
            """;

        _ = command.Parameters.AddWithValue("$sessionKey", CovenantIdentitySql.Key(sessionId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async Task<SessionRow?> ReadSessionAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = source.CreateCommand();

        command.CommandText = """
            SELECT "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt", "Summary",
                   "LastSummarizedMessageAt", "TotalTokensUsed", "TotalCostUsd",
                   "UnsummarizedEntryCount"
            FROM "Sessions" WHERE "Id" = $id;
            """;

        _ = command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SessionRow(
                Value(reader, 0),
                Value(reader, 1),
                Value(reader, 2),
                Value(reader, 3),
                Value(reader, 4),
                Value(reader, 5),
                Value(reader, 6),
                Value(reader, 7),
                Value(reader, 8),
                Value(reader, 9))
            : null;

    }

    private static async Task<ImmutableArray<EntryRow>> ReadEntriesAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "Entries", cancellationToken)
                .ConfigureAwait(false))
        {

            return [];

        }

        ImmutableArray<EntryRow>.Builder builder = ImmutableArray.CreateBuilder<EntryRow>();

        await using SqliteCommand command = source.CreateCommand();

        command.CommandText = """
            SELECT "Id", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence", "ToolCallId",
                   "ToolName", "ToolArguments", "IsPinned"
            FROM "Entries" WHERE "SessionId" = $id ORDER BY "Sequence", "Id";
            """;

        _ = command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            builder.Add(new EntryRow(
                reader.GetString(0),
                Value(reader, 1),
                Value(reader, 2),
                Value(reader, 3),
                Value(reader, 4),
                Value(reader, 5),
                Value(reader, 6),
                Value(reader, 7),
                Value(reader, 8),
                Value(reader, 9)));

        }

        return builder.ToImmutable();

    }

    private static async Task<ImmutableArray<AttachmentRow>> ReadAttachmentsAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "SessionAttachments", cancellationToken)
                .ConfigureAwait(false))
        {

            return [];

        }

        ImmutableArray<AttachmentRow>.Builder builder = ImmutableArray.CreateBuilder<AttachmentRow>();

        await using SqliteCommand command = source.CreateCommand();

        command.CommandText = """
            SELECT "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName", "Version",
                   "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                   "SourceKind", "SourceWorkspaceIdentity", "SourceRelativePath",
                   "SourceCanonicalPath", "SourceContentSha256", "SourceFileIdentity",
                   "SourceLastWriteAt", "SourceByteLength", "EncryptionVersion", "EncryptionKeyId"
            FROM "SessionAttachments" WHERE "SessionId" = $id ORDER BY "Id";
            """;

        _ = command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            object[] values = new object[22];

            for (int index = 0; index < values.Length; index++)
            {

                values[index] = Value(reader, index);

            }

            builder.Add(new AttachmentRow(values));

        }

        return builder.ToImmutable();

    }

    private static async Task<ImmutableArray<FinalizationRow>> ReadFinalizationsAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "assistant_entry_finalizations", cancellationToken)
                .ConfigureAwait(false))
        {

            return [];

        }

        ImmutableArray<FinalizationRow>.Builder builder = ImmutableArray.CreateBuilder<FinalizationRow>();

        await using SqliteCommand command = source.CreateCommand();

        command.CommandText = """
            SELECT AssistantEntryId, ContentSensitivityDigest, RequestDigest
            FROM assistant_entry_finalizations
            WHERE SessionId = $id AND OutcomeCode = 1
            ORDER BY AssistantEntryId;
            """;

        _ = command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            builder.Add(new FinalizationRow(
                reader.GetString(0),
                ReadBlob(reader, 1),
                ReadBlob(reader, 2)));

        }

        return builder.ToImmutable();

    }

    private static byte[] ReadBlob(SqliteDataReader reader, int ordinal)
    {

        using System.IO.Stream stream = reader.GetStream(ordinal);

        using System.IO.MemoryStream buffer = new();

        stream.CopyTo(buffer);

        return buffer.ToArray();

    }

    private static object Value(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? DBNull.Value : reader.GetValue(ordinal);

    private sealed record SessionRow(
        object CampaignId,
        object Title,
        object Status,
        object CreatedAt,
        object UpdatedAt,
        object Summary,
        object LastSummarizedMessageAt,
        object TotalTokensUsed,
        object TotalCostUsd,
        object UnsummarizedEntryCount);

    private sealed record EntryRow(
        string Id,
        object Role,
        object Content,
        object ModelUsed,
        object CreatedAt,
        object Sequence,
        object ToolCallId,
        object ToolName,
        object ToolArguments,
        object IsPinned);

    private sealed record AttachmentRow(object[] Values)
    {

        internal string RelativePath => Values[6] as string ?? string.Empty;

        internal byte[] ContentSha256 =>
            Values[7] switch
            {
                byte[] bytes => bytes,
                string hex when hex.Length == 64 => Convert.FromHexString(hex),
                _ => new byte[32],
            };

        internal long ByteLength =>
            Values[9] is DBNull ? 0 : Convert.ToInt64(Values[9], CultureInfo.InvariantCulture);

    }

    private sealed record FinalizationRow(
        string AssistantEntryId,
        byte[] ContentSensitivityDigest,
        byte[] RequestDigest);

    private sealed record SourceSessionGraph(
        SessionRow Session,
        ImmutableArray<EntryRow> Entries,
        ImmutableArray<AttachmentRow> Attachments,
        ImmutableArray<FinalizationRow> Finalizations)
    {

        internal ProtectedSessionTransferCounts ComputeCounts() =>
            new(
                1,
                checked((ulong)Entries.Length),
                checked((ulong)Attachments.Length),
                checked((ulong)Attachments.Count(static attachment => attachment.RelativePath.Length > 0)),
                checked((ulong)Finalizations.Length),
                0);

        /// <summary>
        /// One canonical preimage per artifact, in the order the manifest commits to.
        /// </summary>
        internal ImmutableArray<byte[]> ComputeManifestItems()
        {

            ImmutableArray<byte[]>.Builder builder = ImmutableArray.CreateBuilder<byte[]>();

            foreach (EntryRow entry in Entries)
            {

                builder.Add(Encoding.UTF8.GetBytes($"entry:{entry.Id}:{entry.Sequence}"));

            }

            foreach (AttachmentRow attachment in Attachments)
            {

                builder.Add(Encoding.UTF8.GetBytes(
                    $"attachment:{attachment.RelativePath}:{Convert.ToHexString(attachment.ContentSha256)}:{attachment.ByteLength}"));

            }

            foreach (FinalizationRow finalization in Finalizations)
            {

                builder.Add(Encoding.UTF8.GetBytes($"finalization:{finalization.AssistantEntryId}"));

            }

            return builder.ToImmutable();

        }

    }

    private sealed record StagedBlob(
        string SourceFile,
        string DestinationFile,
        string TemporaryLeaf,
        string FinalLeaf,
        byte[] ExpectedHash,
        long ExpectedLength,
        bool AlreadyPresent);

    /// <summary>
    /// Advances the transfer journal to its terminal phase after the caller's disposition succeeded.
    /// </summary>
    private sealed class TransferJournalFinalizer(
        ProtectedSessionTransferIntentStore journal,
        SqliteConnection connection,
        Guid operationId,
        TimeProvider timeProvider) : ICovenantExclusivePostDispositionFinalizer
    {

        private int _invoked;

        public async ValueTask<Result> FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken)
        {

            _ = timeProvider;

            if (Interlocked.Exchange(ref _invoked, 1) != 0)
            {

                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.LifecycleConflict,
                        "This transfer journal finalizer has already run."));

            }

            ProtectedSessionTransferPhase terminal = disposition switch
            {
                CovenantExclusiveLeaseDisposition.CommitAndReopen =>
                    ProtectedSessionTransferPhase.Completed,

                CovenantExclusiveLeaseDisposition.RollbackAndReopen =>
                    ProtectedSessionTransferPhase.Abandoned,

                _ => ProtectedSessionTransferPhase.ReopenPending,
            };

            if (terminal == ProtectedSessionTransferPhase.ReopenPending)
            {

                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.LifecycleConflict,
                        "A transfer reaches a terminal phase only after a reopening disposition."));

            }

            ProtectedSessionTransferIntentRow? row =
                await journal.ReadAsync(operationId, null, cancellationToken).ConfigureAwait(false);

            if (row is null || row.Phase != ProtectedSessionTransferPhase.ReopenPending)
            {

                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.LifecycleConflict,
                        "The transfer left its pending phase before finalization."));

            }

            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

            Result advanced = await journal.AdvanceAsync(
                operationId,
                row.Revision,
                terminal,
                row.PendingDisposition,
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (advanced.IsFailure)
            {

                return advanced;

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success();

        }

    }

}
