using System.Globalization;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{

    [SkippableTheory]

    [InlineData("uploaded-file")]

    [InlineData("attachment")]

    [InlineData("session")]

    [InlineData("audit-log")]

    public async Task RecoverPruneAsync_WithOperationScopedQuarantine_RecoversBeforeAdvancingCursor(
        string kind)
    {

        RequireSqlCipher();

        QuarantineRecoveryCandidate seeded =
            await SeedQuarantineRecoveryCandidateAsync(kind);

        DataRetentionService service = CreateService(seeded.Settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(seeded.CandidateId, Assert.Single(plan.CandidateIds));

        LongRunningOperationStore operations = new(_db!);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string ownerId = "quarantine-recovery-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionPrune,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Interrupted after quarantine.",
                now));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5));

        Assert.True(lease.Acquired);

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                seeded.OriginalPath,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact));

        string rootRole = kind switch
        {

            "uploaded-file" => "files",

            "audit-log" => "logs",

            _ => "attachments",

        };

        string managedRoot = rootRole switch
        {

            "files" => _filesRoot,

            "logs" => _logsRoot,

            _ => _attachmentsRoot,

        };

        byte[] pendingJournal = SerializeMutationCheckpoint(
            "prune-candidate",
            seeded.CandidateId,
            Path.GetRelativePath(managedRoot, seeded.OriginalPath),
            artifact.Metadata,
            rootRole);

        byte[] checkpoint = Encoding.UTF8.GetBytes(
            "ARCADATA2\n"
            + plan.PlanId
            + "\n0\nG:"
            + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(plan.GeneratedAt.ToString("o")))
            + "\nP:"
            + Convert.ToBase64String(pendingJournal)
            + "\nC:"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(seeded.CandidateId))
            + ":"
            + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(plan.GeneratedAt.AddDays(-30).ToString("o")))
            + "\n");

        Assert.True(
            await operations.SaveCheckpointAsync(
                operation.Id,
                ownerId,
                expectedCheckpointVersion: 0,
                checkpointVersion: 2,
                checkpoint,
                checkpointReference: "retention-prune:" + operation.Id.ToString("N"),
                "Interrupted after quarantine.",
                now));

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                $".arcanum-retention-{operation.Id:N}-",
                out IdentityOwnedFileSystemQuarantine quarantine));

        string recoveryDirectory = quarantine.Directory.Path;

        Assert.False(File.Exists(seeded.OriginalPath));

        Assert.True(Directory.Exists(recoveryDirectory));

        LongRunningOperation interrupted = Assert.IsType<LongRunningOperation>(
            await operations.GetAsync(operation.Id));

        LongRunningOperationRecoveryResult recovered = await service.RecoverPruneAsync(
            interrupted,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);

        Assert.False(await seeded.Exists());

        Assert.False(File.Exists(seeded.OriginalPath));

        Assert.False(Directory.Exists(recoveryDirectory));

    }

    [SkippableTheory]

    [InlineData(false)]

    [InlineData(true)]

    public async Task MutationRecovery_WithOperationScopedQuarantine_UsesDatabaseAsCommitAuthority(
        bool databaseCommitSucceeded)
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        DataRetentionService service = CreateService();

        LongRunningOperationStore operations = new(_db!);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string ownerId = "mutation-quarantine-recovery-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Interrupted attachment deletion.",
                now));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5));

        Assert.True(lease.Acquired);

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                attachment.AbsolutePath,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact));

        byte[] mutationCheckpoint = SerializeMutationCheckpoint(
            "delete-attachment",
            attachment.AttachmentId.ToString("D"),
            Path.GetRelativePath(_attachmentsRoot, attachment.AbsolutePath),
            artifact.Metadata);

        Assert.True(
            await operations.SaveCheckpointAsync(
                operation.Id,
                ownerId,
                expectedCheckpointVersion: 0,
                checkpointVersion: 2,
                mutationCheckpoint,
                checkpointReference: "retention-mutation:" + operation.Id.ToString("N"),
                "Interrupted attachment deletion.",
                now));

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                $".arcanum-retention-{operation.Id:N}-",
                out IdentityOwnedFileSystemQuarantine quarantine));

        if (databaseCommitSucceeded)
        {

            await ExecuteAsync(
                "DELETE FROM SessionAttachments WHERE lower(replace(Id, '-', '')) = @id",
                ("@id", attachment.AttachmentId.ToString("N")));

        }

        DataRetentionMutationRecoveryHandler handler = new(service);

        LongRunningOperationRecoveryResult recovered = await handler.RecoverAsync(
            Assert.IsType<LongRunningOperation>(await operations.GetAsync(operation.Id)),
            CancellationToken.None);

        Assert.Equal(
            databaseCommitSucceeded
                ? LongRunningOperationState.Completed
                : LongRunningOperationState.Failed,
            recovered.State);

        Assert.Equal(!databaseCommitSucceeded, File.Exists(attachment.AbsolutePath));

        Assert.False(Directory.Exists(quarantine.Directory.Path));

        Assert.Equal(
            databaseCommitSucceeded ? 0 : 1,
            await CountNormalizedKeyAsync(
                "SessionAttachments",
                "Id",
                Canonical(attachment.AttachmentId)));

    }

    [SkippableTheory]

    [InlineData(false)]

    [InlineData(true)]

    public async Task MutationRecovery_WithZeroFileJournal_UsesExactTargetAuthority(
        bool databaseCommitSucceeded)
    {

        RequireSqlCipher();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        DataRetentionService service = CreateService();

        LongRunningOperationStore operations = new(_db!);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string ownerId = "zero-file-mutation-recovery-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Interrupted zero-file session deletion.",
                now,
                SessionId: sessionId));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5));

        Assert.True(lease.Acquired);

        byte[] checkpoint = SerializeEmptyMutationCheckpoint(
            "delete-session",
            sessionId.ToString("D"));

        Assert.True(
            await operations.SaveCheckpointAsync(
                operation.Id,
                ownerId,
                expectedCheckpointVersion: 0,
                checkpointVersion: 2,
                checkpoint,
                checkpointReference: "retention-mutation:" + operation.Id.ToString("N"),
                "Interrupted zero-file session deletion.",
                now));

        if (databaseCommitSucceeded)
        {

            await ExecuteAsync(
                "DELETE FROM Entries WHERE lower(replace(SessionId, '-', '')) = @id",
                ("@id", sessionId.ToString("N")));

            await ExecuteSessionRetentionAsync(
                "DELETE FROM Sessions WHERE lower(replace(Id, '-', '')) = @id",
                ("@id", sessionId.ToString("N")));

        }

        DataRetentionMutationRecoveryHandler handler = new(service);

        LongRunningOperationRecoveryResult recovered = await handler.RecoverAsync(
            Assert.IsType<LongRunningOperation>(await operations.GetAsync(operation.Id)),
            CancellationToken.None);

        Assert.Equal(
            databaseCommitSucceeded
                ? LongRunningOperationState.Completed
                : LongRunningOperationState.Failed,
            recovered.State);

    }

    [SkippableFact]

    public async Task MutationRecovery_WithEmptyPreparedQuarantine_RemovesDirectoryAndClassifiesPrecommit()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                attachment.AbsolutePath,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact));

        DataRetentionService service = CreateService();

        LongRunningOperationStore operations = new(_db!);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string ownerId = "empty-quarantine-recovery-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Interrupted before quarantine move.",
                now));

        Assert.True(
            (await operations.TryAcquireLeaseAsync(
                operation.Id,
                ownerId,
                now,
                now.AddMinutes(5))).Acquired);

        byte[] checkpoint = SerializeMutationCheckpoint(
            "delete-attachment",
            attachment.AttachmentId.ToString("D"),
            Path.GetRelativePath(_attachmentsRoot, attachment.AbsolutePath),
            artifact.Metadata);

        Assert.True(
            await operations.SaveCheckpointAsync(
                operation.Id,
                ownerId,
                expectedCheckpointVersion: 0,
                checkpointVersion: 2,
                checkpoint,
                checkpointReference: "retention-mutation:" + operation.Id.ToString("N"),
                "Interrupted before quarantine move.",
                now));

        string emptyDirectory = Path.Combine(
            Path.GetDirectoryName(attachment.AbsolutePath)!,
            $".arcanum-retention-{operation.Id:N}-{Guid.NewGuid():N}");

        SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath(emptyDirectory);

        DataRetentionMutationRecoveryHandler handler = new(service);

        LongRunningOperationRecoveryResult recovered = await handler.RecoverAsync(
            Assert.IsType<LongRunningOperation>(await operations.GetAsync(operation.Id)),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Failed, recovered.State);

        Assert.False(Directory.Exists(emptyDirectory));

        Assert.True(File.Exists(attachment.AbsolutePath));

    }

    [SkippableFact]

    public async Task MutationRecovery_WhenInterruptedBeforeItsJournal_TerminalizesAndUnblocksRetention()
    {

        RequireSqlCipher();

        DataRetentionService service = CreateService();

        LongRunningOperationStore operations = new(_db!);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string ownerId = "pre-journal-mutation-recovery-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Interrupted before its durable journal.",
                now));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5));

        Assert.True(lease.Acquired);

        LongRunningOperation stranded = Assert.IsType<LongRunningOperation>(
            await operations.GetAsync(operation.Id));

        Assert.Equal(0, stranded.CheckpointVersion);

        Assert.Null(stranded.CheckpointPayload);

        Assert.Null(stranded.CheckpointReference);

        DataRetentionMutationRecoveryHandler handler = new(service);

        LongRunningOperationRecoveryResult recovered = await handler.RecoverAsync(
            stranded,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, recovered.State);

        Assert.True(
            await operations.TryTransitionAsync(
                stranded.Id,
                stranded.Revision,
                ownerId,
                recovered.State,
                now,
                recovered.ErrorCode));

        LongRunningOperation? nextOperation = await operations.TryStartSingleFlightAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionPrune,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "A later retention sweep must not be blocked by the stranded row.",
                now),
            "later-retention-owner",
            now,
            now.AddMinutes(5));

        Assert.NotNull(nextOperation);

    }

    private async Task<QuarantineRecoveryCandidate> SeedQuarantineRecoveryCandidateAsync(
        string kind)
    {

        ArcanumSettings settings = CreatePruneSettings();

        switch (kind)
        {

            case "uploaded-file":
            {

                Guid fileId = Guid.NewGuid();

                string path = Path.Combine(_filesRoot, fileId.ToString("N"));

                await File.WriteAllBytesAsync(path, [1, 2, 3]);

                await SeedUploadedFileAsync(fileId, 3);

                settings.Retention.UploadedFiles = EnabledRule();

                return new QuarantineRecoveryCandidate(
                    settings,
                    "file:" + fileId.ToString("D"),
                    path,
                    async () => await CountNormalizedKeyAsync(
                        "UploadedFiles",
                        "Id",
                        fileId.ToString()) > 0);

            }

            case "attachment":
            {

                (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

                SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

                settings.Retention.Attachments = EnabledRule();

                return new QuarantineRecoveryCandidate(
                    settings,
                    "attachment:" + attachment.AttachmentId.ToString("D"),
                    attachment.AbsolutePath,
                    async () => await CountNormalizedKeyAsync(
                        "SessionAttachments",
                        "Id",
                        Canonical(attachment.AttachmentId)) > 0);

            }

            case "session":
            {

                (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

                SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

                settings.Retention.ArchivedSessions = EnabledRule();

                return new QuarantineRecoveryCandidate(
                    settings,
                    "session:" + sessionId.ToString("D"),
                    attachment.AbsolutePath,
                    async () => await CountNormalizedKeyAsync(
                        "Sessions",
                        "Id",
                        sessionId.ToString()) > 0);

            }

            case "audit-log":
            {

                string fileName = "audit-20000101.jsonl";

                string path = Path.Combine(_logsRoot, fileName);

                await File.WriteAllTextAsync(path, "{}\n");

                File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch);

                settings.Retention.AuditLogs = EnabledRule();

                return new QuarantineRecoveryCandidate(
                    settings,
                    "audit-log:" + fileName,
                    path,
                    () => Task.FromResult(File.Exists(path)));

            }

            default:
                throw new InvalidOperationException("Unknown quarantine recovery scenario.");

        }

    }

    private sealed record QuarantineRecoveryCandidate(
        ArcanumSettings Settings,
        string CandidateId,
        string OriginalPath,
        Func<Task<bool>> Exists);

    private static byte[] SerializeMutationCheckpoint(
        string subtype,
        string target,
        string relativePath,
        FileHandleMetadata metadata,
        string rootRole = "attachments")
    {

        StringBuilder body = new();

        body.Append("ARCAMUT2\n")
            .Append(subtype)
            .Append('\n')
            .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(target)))
            .Append("\n1\nE:")
            .Append(rootRole)
            .Append(':')
            .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(relativePath)))
            .Append(':')
            .Append(metadata.Identity.VolumeId.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(metadata.Identity.FileId.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(metadata.HardLinkCount.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(((int)metadata.Kind).ToString(CultureInfo.InvariantCulture))
            .Append('\n');

        byte[] canonical = Encoding.UTF8.GetBytes(body.ToString());

        body.Append("H:")
            .Append(Convert.ToHexString(SHA256.HashData(canonical)))
            .Append('\n');

        return Encoding.UTF8.GetBytes(body.ToString());

    }

    private static byte[] SerializeEmptyMutationCheckpoint(
        string subtype,
        string target)
    {

        StringBuilder body = new();

        body.Append("ARCAMUT2\n")
            .Append(subtype)
            .Append('\n')
            .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(target)))
            .Append("\n0\n");

        byte[] canonical = Encoding.UTF8.GetBytes(body.ToString());

        body.Append("H:")
            .Append(Convert.ToHexString(SHA256.HashData(canonical)))
            .Append('\n');

        return Encoding.UTF8.GetBytes(body.ToString());

    }

}
