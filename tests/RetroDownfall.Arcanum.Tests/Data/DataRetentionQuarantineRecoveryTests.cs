using System.Globalization;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

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

    /// <summary>
    /// A committed Saga reset is recovered as committed while another store's claims are still standing.
    /// </summary>
    /// <remarks>
    /// The sibling below asks that a reset which left something behind be seen. This asks the opposite,
    /// and it is what decides which tables may answer the question at all: the residue count carries no
    /// predicate, so a table holding more than one store's rows cannot witness one store's reset. Naming
    /// one would leave a reset that had committed being recovered as failed for as long as the other
    /// store held a claim - on every retry, because nothing about it would ever change.
    ///
    /// <para>The Lexicon claim is what makes this bite. Without it the Annals are empty too, and the
    /// recovery reaches the right answer for a reason that says nothing about the question.</para>
    /// </remarks>
    [SkippableFact]

    public async Task MutationRecovery_ForACommittedSagaReset_IsNotHeldOpenByAnotherStoresClaims()
    {

        RequireSqlCipher();

        string memory = await SeedGlobalSagaMemoryAsync();

        await SeedClaimAsync(1, memory);

        string entry = await SeedClaimedLexiconEntryAsync("config", string.Empty);

        await ApplyUntargetedResetAsync(MemoryResetScope.Saga);

        Assert.Equal(0, await CountTableRowsAsync("saga_memories"));

        Assert.Equal(1, await CountAnnalClaimsAsync(2, entry));

        DataRetentionService service = CreateService();

        LongRunningOperationStore operations = new(_db!);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string ownerId = "committed-saga-reset-recovery-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Interrupted Saga memory reset.",
                now));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5));

        Assert.True(lease.Acquired);

        Assert.True(
            await operations.SaveCheckpointAsync(
                operation.Id,
                ownerId,
                expectedCheckpointVersion: 0,
                checkpointVersion: 2,
                SerializeEmptyMutationCheckpoint(
                    "reset-memory",
                    ((int)MemoryResetScope.Saga).ToString(CultureInfo.InvariantCulture)),
                checkpointReference: "retention-mutation:" + operation.Id.ToString("N"),
                "Interrupted Saga memory reset.",
                now));

        DataRetentionMutationRecoveryHandler handler = new(service);

        LongRunningOperationRecoveryResult recovered = await handler.RecoverAsync(
            Assert.IsType<LongRunningOperation>(await operations.GetAsync(operation.Id)),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);

    }

    /// <summary>
    /// Every table an Annals claim can be bound to is named among the residue of the reset that clears
    /// that store.
    /// </summary>
    /// <remarks>
    /// The other half of what lets a reset's recovery read no Annals table at all. One half is that a
    /// claim cannot outlive the durable row it describes; this is the rest of it — that the row it
    /// cannot outlive is one the residue count actually reads. Take the subject table out of a scope and
    /// nothing witnesses that store's claims any more, whatever its own removals do.
    /// </remarks>
    [SkippableFact]
    public void MemoryResetResidue_NamesTheTableEachStoresClaimsAreBoundTo()
    {

        RequireSqlCipher();

        Assert.Contains(
            "saga_memories",
            DataRetentionService.MemoryResetResidueTables(MemoryResetScope.Saga));

        Assert.Contains(
            "lexicon_entries",
            DataRetentionService.MemoryResetResidueTables(MemoryResetScope.Lexicon));

    }

    /// <summary>
    /// An interrupted Saga reset whose only surviving rows are the retirement evidence and its key is
    /// still an interrupted reset.
    /// </summary>
    /// <remarks>
    /// Recovery asks one question of the database — is any of what this reset was to clear still there —
    /// and answers "no" by declaring the interrupted operation complete. A residue list that named every
    /// Saga table except these two would answer "no" for an installation whose memories had gone and
    /// whose suppressions had not, and the reset would be marked done with the evidence still standing
    /// and no further attempt ever made.
    ///
    /// <para>The memory row is removed after the retirement, so what is left for recovery to find is
    /// the evidence and its key. The retirement is driven through the store, because a seeded
    /// suppression row would be the test choosing the answer.</para>
    /// </remarks>
    [SkippableFact]

    public async Task MutationRecovery_ForAnInterruptedSagaReset_SeesTheRetirementEvidenceLeftBehind()
    {

        RequireSqlCipher();

        _ = await WriteAndRetireSagaMemoryAsync(sessionId: null, "the operator prefers tabs");

        await ExecuteAsync("DELETE FROM saga_memories");

        Assert.Equal(1, await CountAllAsync("saga_retirement_suppressions"));

        Assert.Equal(1, await CountAllAsync("saga_suppression_key"));

        DataRetentionService service = CreateService();

        LongRunningOperationStore operations = new(_db!);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string ownerId = "saga-reset-residue-recovery-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Interrupted Saga memory reset.",
                now));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5));

        Assert.True(lease.Acquired);

        Assert.True(
            await operations.SaveCheckpointAsync(
                operation.Id,
                ownerId,
                expectedCheckpointVersion: 0,
                checkpointVersion: 2,
                SerializeEmptyMutationCheckpoint(
                    "reset-memory",
                    ((int)MemoryResetScope.Saga).ToString(CultureInfo.InvariantCulture)),
                checkpointReference: "retention-mutation:" + operation.Id.ToString("N"),
                "Interrupted Saga memory reset.",
                now));

        DataRetentionMutationRecoveryHandler handler = new(service);

        LongRunningOperationRecoveryResult recovered = await handler.RecoverAsync(
            Assert.IsType<LongRunningOperation>(await operations.GetAsync(operation.Id)),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Failed, recovered.State);

    }

    /// <summary>
    /// A Campaign-targeted Saga reset that committed is recovered as committed, with another Campaign's
    /// memories and an installation-wide retirement still standing.
    /// </summary>
    /// <remarks>
    /// Two separate things have to hold for this to reach Completed, and neither did. The journal target
    /// carries the Campaign behind the scope, so a reader that parses the whole string as the scope
    /// rejects it and throws the invalid-target exception - which the reconciler reads as a corrupt
    /// checkpoint, the disposition it re-selects forever.
    ///
    /// <para>And a targeted reset owns some of the rows in the tables it touches rather than all of
    /// them, so the count that witnesses it has to carry the reset's own predicate. The rows seeded here
    /// are what a count without one reads instead: another Campaign's memory and its embedding, and the
    /// retirement evidence and key that only a whole-store reset clears. Every one of them would report
    /// this committed reset as unfinished work, on every retry.</para>
    /// </remarks>
    [SkippableFact]

    public async Task MutationRecovery_ForACommittedCampaignTargetedSagaReset_IgnoresEveryOtherCampaignsRows()
    {

        RequireSqlCipher();

        string ownedByA = await SeedScopedSagaMemoryAsync(ResetCampaignA);

        string ownedByB = await SeedScopedSagaMemoryAsync(ResetCampaignB);

        _ = await WriteAndRetireSagaMemoryAsync(sessionId: null, "the operator prefers tabs");

        await ApplyCampaignResetAsync(MemoryResetScope.Saga, ResetCampaignA);

        Assert.Equal(0, await CountSagaAsync(ownedByA));

        Assert.Equal(1, await CountSagaAsync(ownedByB));

        Assert.Equal(1, await CountAllAsync("saga_retirement_suppressions"));

        Assert.Equal(1, await CountAllAsync("saga_suppression_key"));

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = await SeedMemoryResetJournalAsync(
            operations,
            "committed-campaign-reset-recovery-test",
            CampaignResetTarget(MemoryResetScope.Saga, ResetCampaignA));

        DataRetentionMutationRecoveryHandler handler = new(CreateService());

        LongRunningOperationRecoveryResult recovered = await handler.RecoverAsync(
            operation,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);

    }

    /// <summary>
    /// A Campaign-targeted Saga reset that was interrupted is still recovered as interrupted.
    /// </summary>
    /// <remarks>
    /// The mutation is one transaction, so an interruption leaves this Campaign's rows exactly where
    /// they were. Recovery has to see them: reporting the reset complete would close the operation with
    /// the memories the operator asked to remove still readable and no further attempt ever made. It is
    /// the other half of the sibling above - one arm says which rows may not answer, this one says the
    /// rows that must.
    ///
    /// <para>Expecting Failed is also the only way an arm binds the Campaign the target parsed back to,
    /// because a wrong one counts nothing and reaches Completed. This one binds the canonical upper-case
    /// spelling that saga_memories and session_campaign_bindings are compared on.</para>
    /// </remarks>
    [SkippableFact]

    public async Task MutationRecovery_ForAnInterruptedCampaignTargetedSagaReset_SeesThatCampaignsMemories()
    {

        RequireSqlCipher();

        string ownedByA = await SeedScopedSagaMemoryAsync(ResetCampaignA);

        Assert.Equal(1, await CountSagaAsync(ownedByA));

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = await SeedMemoryResetJournalAsync(
            operations,
            "interrupted-campaign-reset-recovery-test",
            CampaignResetTarget(MemoryResetScope.Saga, ResetCampaignA));

        DataRetentionMutationRecoveryHandler handler = new(CreateService());

        LongRunningOperationRecoveryResult recovered = await handler.RecoverAsync(
            operation,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Failed, recovered.State);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, recovered.ErrorCode);

    }

    /// <summary>
    /// The same for the other store that records an owning Campaign, where the target and the predicate
    /// spell that Campaign differently and one residue table cannot be scoped at all.
    /// </summary>
    /// <remarks>
    /// What this arm proves is that nothing outside this Campaign holds a committed reset open: another
    /// Campaign's entry, the global scope's, and the index terms of both. It cannot prove which Campaign
    /// was parsed. An identity that matches no row counts no residue and reaches Completed too, so every
    /// wrong identity satisfies an arm that expects Completed - which is what the interrupted sibling
    /// below is for.
    ///
    /// <para>lexicon_fts is what a scoped count cannot reach. It is an external-content index with no
    /// scope column, its rows go as the entries that own them do, and the two terms asserted here are
    /// the surviving scopes' - so counting it is how a committed Campaign reset reads as unfinished for
    /// as long as any other scope has an entry.</para>
    /// </remarks>
    [SkippableFact]

    public async Task MutationRecovery_ForACommittedCampaignTargetedLexiconReset_IgnoresEveryOtherScopesTerms()
    {

        RequireSqlCipher();

        await SeedScopedLexiconEntryAsync("config", ResetCampaignA.ToString());

        await SeedScopedLexiconEntryAsync("config", ResetCampaignB.ToString());

        await SeedScopedLexiconEntryAsync("config", string.Empty);

        await ApplyCampaignResetAsync(MemoryResetScope.Lexicon, ResetCampaignA);

        Assert.Equal(0, await CountAsync("lexicon_entries", "ScopeCampaignId", ResetCampaignA.ToString()));

        Assert.Equal(2, await CountLexiconFtsMatchesAsync("config"));

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = await SeedMemoryResetJournalAsync(
            operations,
            "committed-campaign-lexicon-recovery-test",
            CampaignResetTarget(MemoryResetScope.Lexicon, ResetCampaignA));

        DataRetentionMutationRecoveryHandler handler = new(CreateService());

        LongRunningOperationRecoveryResult recovered = await handler.RecoverAsync(
            operation,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);

    }

    /// <summary>
    /// A Campaign-targeted Lexicon reset that was interrupted is still recovered as interrupted.
    /// </summary>
    /// <remarks>
    /// The arm that binds the Lexicon spelling. The journal writes the Campaign in the "N" form and
    /// lexicon_entries.ScopeCampaignId holds a bare ToString(), so the target has to be read back as an
    /// identity rather than carried through as the text it arrived as - and the Saga sibling cannot say
    /// so, because it binds the canonical upper-case form instead.
    ///
    /// <para>It has to expect Failed to say anything at all. A Campaign that matched no row would leave
    /// this Campaign's entry standing and still report the reset committed, which is the reading every
    /// arm expecting Completed accepts.</para>
    /// </remarks>
    [SkippableFact]

    public async Task MutationRecovery_ForAnInterruptedCampaignTargetedLexiconReset_SeesThatCampaignsEntries()
    {

        RequireSqlCipher();

        await SeedScopedLexiconEntryAsync("config", ResetCampaignA.ToString());

        Assert.Equal(
            1,
            await CountAsync("lexicon_entries", "ScopeCampaignId", ResetCampaignA.ToString()));

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = await SeedMemoryResetJournalAsync(
            operations,
            "interrupted-campaign-lexicon-recovery-test",
            CampaignResetTarget(MemoryResetScope.Lexicon, ResetCampaignA));

        DataRetentionMutationRecoveryHandler handler = new(CreateService());

        LongRunningOperationRecoveryResult recovered = await handler.RecoverAsync(
            operation,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Failed, recovered.State);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, recovered.ErrorCode);

    }

    /// <summary>
    /// The target <c>PrepareMutationJournalAsync</c> composes for a Campaign-targeted memory reset.
    /// </summary>
    /// <remarks>
    /// Transcribed from that writer rather than shared with it, so a test seeded here still fails if the
    /// two ever disagree about what a checkpoint records.
    /// </remarks>
    private static string CampaignResetTarget(MemoryResetScope scope, Guid campaignId) =>
        ((int)scope).ToString(CultureInfo.InvariantCulture) + ":" + campaignId.ToString("N");

    /// <summary>
    /// The row a dead process leaves behind mid-reset: the version-2 journal it wrote, under a lease it
    /// no longer holds.
    /// </summary>
    private async Task<LongRunningOperation> SeedMemoryResetJournalAsync(
        LongRunningOperationStore operations,
        string ownerId,
        string target)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string summary = "Interrupted memory reset.";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                summary,
                now));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5));

        Assert.True(lease.Acquired);

        Assert.True(
            await operations.SaveCheckpointAsync(
                operation.Id,
                ownerId,
                expectedCheckpointVersion: 0,
                checkpointVersion: 2,
                SerializeEmptyMutationCheckpoint("reset-memory", target),
                checkpointReference: "retention-mutation:" + operation.Id.ToString("N"),
                summary,
                now));

        return Assert.IsType<LongRunningOperation>(await operations.GetAsync(operation.Id));

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
