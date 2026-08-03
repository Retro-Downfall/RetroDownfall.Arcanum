using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{

    [SkippableTheory]

    [InlineData("entry")]

    [InlineData("attachment")]

    [InlineData("entry-embedding")]

    public async Task PlanAsync_Prune_ReportsBoundedProtectedDiagnosticsWithoutSpendingCandidateQuota(
        string scenario)
    {

        RequireSqlCipher();

        (Guid blockedSessionId, Guid blockedEntryId) = await SeedSessionAsync(
            pinned: false);

        (Guid eligibleSessionId, Guid eligibleEntryId) = await SeedSessionAsync(
            pinned: false);

        const string blockedAt = "1999-01-01T00:00:00.0000000+00:00";

        const string eligibleAt = "2000-01-01T00:00:00.0000000+00:00";

        ArcanumSettings settings = CreatePruneSettings();

        string blockedResourceId;

        string expectedCandidate;

        RetentionDataClass expectedBlockerClass;

        string expectedReasonCode;

        switch (scenario)
        {

            case "entry":
            {

                await ExecuteAsync(
                    "UPDATE Entries SET IsPinned = 1, CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
                    ("@at", blockedAt),
                    ("@id", blockedEntryId.ToString("N")));

                await ExecuteAsync(
                    "UPDATE Entries SET CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
                    ("@at", eligibleAt),
                    ("@id", eligibleEntryId.ToString("N")));

                settings.Retention.Entries = EnabledRule();

                blockedResourceId = blockedEntryId.ToString("D");

                expectedCandidate = "entry:" + eligibleEntryId.ToString("D");

                expectedBlockerClass = RetentionDataClass.Entries;

                expectedReasonCode = "Data.PinnedEntry";

                break;

            }

            case "attachment":
            {

                SeededAttachment blocked = await SeedAttachmentAsync(
                    blockedSessionId,
                    blockedEntryId);

                SeededAttachment eligible = await SeedAttachmentAsync(
                    eligibleSessionId,
                    eligibleEntryId);

                await SeedContextPinAsync(
                    blockedSessionId,
                    SessionContextPinKind.Attachment,
                    blocked.AttachmentId.ToString());

                await ExecuteAsync(
                    "UPDATE SessionAttachments SET CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
                    ("@at", blockedAt),
                    ("@id", blocked.AttachmentId.ToString("N")));

                await ExecuteAsync(
                    "UPDATE SessionAttachments SET CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
                    ("@at", eligibleAt),
                    ("@id", eligible.AttachmentId.ToString("N")));

                settings.Retention.Attachments = EnabledRule();

                blockedResourceId = blocked.AttachmentId.ToString("D");

                expectedCandidate = "attachment:" + eligible.AttachmentId.ToString("D");

                expectedBlockerClass = RetentionDataClass.AttachmentVersions;

                expectedReasonCode = "Data.PinnedAttachment";

                break;

            }

            case "entry-embedding":
            {

                await SeedEntryEmbeddingAsync(blockedEntryId);

                await SeedEntryEmbeddingAsync(eligibleEntryId);

                await ExecuteAsync(
                    "UPDATE Entries SET IsPinned = 1, CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
                    ("@at", blockedAt),
                    ("@id", blockedEntryId.ToString("N")));

                await ExecuteAsync(
                    "UPDATE Entries SET CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
                    ("@at", eligibleAt),
                    ("@id", eligibleEntryId.ToString("N")));

                settings.Retention.SessionEntryEmbeddings = EnabledRule();

                blockedResourceId = blockedEntryId.ToString("D");

                expectedCandidate = "entry-embedding:" + eligibleEntryId.ToString("D");

                expectedBlockerClass = RetentionDataClass.SessionEntryEmbeddings;

                expectedReasonCode = "Data.PinnedEntry";

                break;

            }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(scenario),
                    scenario,
                    "Unknown protected diagnostic scenario.");

        }

        IDataRetentionService service = CreateService(settings);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune),
            CancellationToken.None);

        Assert.Equal(expectedCandidate, Assert.Single(plan.CandidateIds));

        DataRetentionBlocker blocker = Assert.Single(
            plan.Blockers,
            candidate => candidate.DataClass == expectedBlockerClass
                && candidate.ResourceId == blockedResourceId
                && candidate.ReasonCode == expectedReasonCode);

        Assert.NotNull(blocker);

        Assert.DoesNotContain(
            plan.CandidateIds,
            candidate => candidate.EndsWith(blockedResourceId, StringComparison.Ordinal));

    }

    [SkippableTheory]

    [InlineData(BatchStatuses.InProgress, true)]

    [InlineData(BatchStatuses.Completed, false)]

    public async Task PlanAsync_PruneUploadedFiles_WithMaxOne_ReferencedOldestDoesNotStarveEligible(
        string batchStatus,
        bool expectActiveConflict)
    {

        RequireSqlCipher();

        Guid blockedFileId = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff0");

        Guid eligibleFileId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        Guid batchId = Guid.NewGuid();

        await SeedUploadedFileAsync(blockedFileId, 0);

        await SeedUploadedFileAsync(eligibleFileId, 0);

        await ExecuteAsync(
            "UPDATE UploadedFiles SET CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
            ("@at", "1999-01-01T00:00:00.0000000+00:00"),
            ("@id", blockedFileId.ToString("N")));

        await ExecuteAsync(
            "UPDATE UploadedFiles SET CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
            ("@at", "2000-01-01T00:00:00.0000000+00:00"),
            ("@id", eligibleFileId.ToString("N")));

        await ExecuteAsync(
            """
            INSERT INTO Batches
                (Id, InputFileId, Endpoint, Status, CreatedAt, CompletedAt)
            VALUES
                (@id, @fileId, '/v1/chat/completions', @status, @at,
                 CASE WHEN @status = 'completed' THEN @at ELSE NULL END)
            """,
            ("@id", batchId.ToString()),
            ("@fileId", blockedFileId.ToString()),
            ("@status", batchStatus),
            ("@at", "1999-01-01T00:00:00.0000000+00:00"));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune),
            CancellationToken.None);

        Assert.Equal(
            "file:" + eligibleFileId.ToString("D"),
            Assert.Single(plan.CandidateIds));

        Assert.Contains(
            plan.Blockers,
            blocker => blocker.DataClass == RetentionDataClass.UploadedFiles
                && blocker.ResourceId == blockedFileId.ToString("D")
                && blocker.ReasonCode == "Data.BatchReference");

        Assert.Equal(
            expectActiveConflict,
            plan.Conflicts.Any(conflict =>
                conflict.Code == "Data.BatchInProgress"
                && conflict.ResourceId == batchId.ToString("D")));

    }

    [SkippableFact]

    public async Task PlanAsync_PruneEntries_PreservesAgeOrderWhenGuidOrderOpposesIt()
    {

        RequireSqlCipher();

        (_, Guid originalOlderEntryId) = await SeedSessionAsync(pinned: false);

        (_, Guid originalNewerEntryId) = await SeedSessionAsync(pinned: false);

        Guid olderEntryId = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff0");

        Guid newerEntryId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        await ExecuteAsync(
            "UPDATE Entries SET Id = @newId, CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @oldId",
            ("@newId", olderEntryId.ToString()),
            ("@at", "1999-01-01T00:00:00.0000000+00:00"),
            ("@oldId", originalOlderEntryId.ToString("N")));

        await ExecuteAsync(
            "UPDATE Entries SET Id = @newId, CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @oldId",
            ("@newId", newerEntryId.ToString()),
            ("@at", "2000-01-01T00:00:00.0000000+00:00"),
            ("@oldId", originalNewerEntryId.ToString("N")));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune),
            CancellationToken.None);

        Assert.Equal(
            [
                "entry:" + olderEntryId.ToString("D"),
                "entry:" + newerEntryId.ToString("D"),
            ],
            plan.CandidateIds);

    }

    [SkippableFact]

    public async Task GetStatusAsync_CountsEveryFactoryOwnedMetadataTableInItsCanonicalClass()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedContextPinAsync(
            sessionId,
            SessionContextPinKind.SessionEntry,
            entryId.ToString());

        await ExecuteAsync(
            """
            INSERT INTO WorkspaceContexts
                (Id, RootPath, SerializedSnapshot, CreatedAt)
            VALUES
                (@id, '/workspace', '{}', @at)
            """,
            ("@id", Guid.NewGuid().ToString()),
            ("@at", "2000-01-01T00:00:00.0000000+00:00"));

        await ExecuteAsync(
            """
            INSERT INTO IdempotencyKeys
                (KeyHash, StatusCode, ContentType, ResponseBody, CreatedAt)
            VALUES
                ('legacy-retention-key', 200, 'application/json', '{}', @at)
            """,
            ("@at", "2000-01-01T00:00:00.0000000+00:00"));

        IDataRetentionService service = CreateService();

        DataRetentionStatus status = await service.GetStatusAsync(
            CancellationToken.None);

        AssertStatusRows(
            status,
            RetentionDataClass.Entries,
            await SumTableCountsAsync(
                "Entries",
                "Entries_fts",
                "attachment_memory_consultations",
                "SessionContextPins"),
            "SessionContextPins");

        AssertStatusRows(
            status,
            RetentionDataClass.WorkspaceChunks,
            await SumTableCountsAsync(
                "workspace_file_chunks",
                "WorkspaceContexts"),
            "WorkspaceContexts");

        AssertStatusRows(
            status,
            RetentionDataClass.IdempotencyClaims,
            await SumTableCountsAsync(
                "IdempotencyClaims",
                "IdempotencyKeys"),
            "IdempotencyKeys");

    }

}
