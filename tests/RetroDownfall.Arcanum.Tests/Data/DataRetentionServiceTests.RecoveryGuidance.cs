using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(DataRetentionOperation.DeleteSession)]
    [InlineData(DataRetentionOperation.DeleteAttachment)]
    [InlineData(DataRetentionOperation.ResetMemory)]
    [InlineData(DataRetentionOperation.Prune)]
    [InlineData(DataRetentionOperation.ResetWorkspace)]
    public async Task Recovery_guidance_for_a_changed_plan_names_an_available_refresh_operation(DataRetentionOperation operation)
    {
        RequireSqlCipher();

        DataRetentionRequest request = await SeedRecoveryGuidanceRequestAsync(operation);

        DataRetentionService service = CreateService();

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId + "-stale"));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, result.Error.Code);

        Assert.Equal(0, await CountAllAsync("LongRunningOperations"));

        if (operation is DataRetentionOperation.Prune or DataRetentionOperation.ResetWorkspace)
        {
            Assert.Contains("dry-run", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.DoesNotContain("dry-run", result.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("again", result.Error.Message, StringComparison.OrdinalIgnoreCase);

            if (operation == DataRetentionOperation.ResetMemory)
            {
                Assert.Contains("preview", result.Error.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [SkippableTheory]
    [InlineData(DataRetentionOperation.DeleteSession)]
    [InlineData(DataRetentionOperation.DeleteAttachment)]
    public async Task Recovery_guidance_for_a_deletion_race_preserves_data_and_offers_a_real_retry(DataRetentionOperation operation)
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        byte[] concurrentBytes = [.. attachment.Bytes, 17];

        HeartbeatCountingOperationStore operations = new(new LongRunningOperationStore(_db!, TestOrdinaryConnectionFactory.For(_db!)))
        {
            AfterStartAsync = cancellationToken => File.WriteAllBytesAsync(attachment.AbsolutePath, concurrentBytes, cancellationToken),
        };

        DataRetentionService service = CreateService(operationStore: operations);

        DataRetentionRequest request = new(operation, operation == DataRetentionOperation.DeleteSession ? sessionId : attachment.AttachmentId);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.Conflict, result.Error.Code);

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));

        Assert.Equal(concurrentBytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.DoesNotContain("dry-run", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("again", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableTheory]
    [InlineData(DataRetentionOperation.DeleteSession)]
    [InlineData(DataRetentionOperation.DeleteAttachment)]
    [InlineData(DataRetentionOperation.ResetMemory)]
    [InlineData(DataRetentionOperation.Prune)]
    [InlineData(DataRetentionOperation.ResetWorkspace)]
    public async Task Recovery_guidance_describes_an_applied_change_when_completion_bookkeeping_is_refused(DataRetentionOperation operation)
    {
        RequireSqlCipher();

        DataRetentionRequest request = await SeedRecoveryGuidanceRequestAsync(operation);

        RefusesCompletionOperationStore operations = new(new LongRunningOperationStore(_db!, TestOrdinaryConnectionFactory.For(_db!)));

        DataRetentionService service = CreateService(operationStore: operations);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(operations.RefusedCompletion);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.OperationNotFinalized, result.Error.Code);

        if (operation == DataRetentionOperation.DeleteSession)
        {
            Assert.Equal(0, await CountAllAsync("Sessions"));
        }

        if (operation == DataRetentionOperation.DeleteAttachment)
        {
            Assert.Equal(0, await CountAllAsync("SessionAttachments"));
        }

        Assert.Contains("data change was applied", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("pruned", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("retry is safe", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovery_guidance_for_prune_keeps_dry_run_advice_inside_shared_deletion_helpers(bool sessions)
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        byte[] concurrentBytes = [.. attachment.Bytes, 19];

        ArcanumSettings settings = CreatePruneSettings();

        if (sessions)
        {
            settings.Retention.ArchivedSessions = EnabledRule();
        }
        else
        {
            settings.Retention.Attachments = EnabledRule();
        }

        DataRetentionService service = CreateBoundaryService(settings,
            (_, cancellationToken) => File.WriteAllBytesAsync(attachment.AbsolutePath, concurrentBytes, cancellationToken));

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Single(plan.CandidateIds);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.Conflict, result.Error.Code);

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));

        Assert.Equal(concurrentBytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.Contains("dry-run", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("deletion command again", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<DataRetentionRequest> SeedRecoveryGuidanceRequestAsync(DataRetentionOperation operation)
    {
        if (operation == DataRetentionOperation.ResetWorkspace)
        {
            return WorkspaceResetRequest(await SeedWorkspaceResetGraphAsync());
        }

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        return operation switch
        {
            DataRetentionOperation.DeleteSession => new(operation, sessionId),
            DataRetentionOperation.DeleteAttachment => new(operation, attachment.AttachmentId),
            DataRetentionOperation.ResetMemory => new(operation, MemoryScope: MemoryResetScope.Entry),
            DataRetentionOperation.Prune => new(operation),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }
}
