using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task Refusal_contract_keeps_a_pending_attachment_and_reports_its_blocker()
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await ExecuteAsync("UPDATE SessionAttachments SET State = 'Pending' WHERE Id = @id", ("@id", Canonical(attachment.AttachmentId)));

        DataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.DeleteAttachment, attachment.AttachmentId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        DataRetentionBlocker blocker = Assert.Single(plan.Blockers);

        Assert.Equal("Data.AttachmentInFlight", blocker.ReasonCode);

        Assert.Empty(plan.Items);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.Blocked, result.Error.Code);

        Assert.Equal(blocker.Message, result.Error.Message);

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));

        Assert.Equal(1, await CountAllAsync("session_attachment_chunks"));

        Assert.Equal(attachment.Bytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.Equal(0, await CountAllAsync("LongRunningOperations"));
    }

    [SkippableTheory]
    [InlineData(MemoryResetScope.Entry)]
    [InlineData(MemoryResetScope.Attachments)]
    [InlineData(MemoryResetScope.Workspace)]
    public async Task Refusal_contract_classifies_an_unowned_campaign_reset_as_invalid_input(MemoryResetScope scope)
    {
        RequireSqlCipher();

        DataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.ResetMemory, ResetCampaignA, scope);

        DataRetentionPlan plan = await service.PlanAsync(request);

        DataRetentionBlocker blocker = Assert.Single(plan.Blockers);

        Assert.Equal(ErrorCodes.Data.InvalidRequest, blocker.ReasonCode);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.InvalidRequest, result.Error.Code);

        Assert.Equal(blocker.Message, result.Error.Message);

        Assert.Contains("Saga", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("Lexicon", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("Campaign summaries", result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(0, await CountAllAsync("LongRunningOperations"));
    }

    [SkippableTheory]
    [InlineData(MemoryResetScope.Entry)]
    [InlineData(MemoryResetScope.Attachments)]
    [InlineData(MemoryResetScope.Workspace)]
    public async Task Refusal_contract_preview_names_all_supported_campaign_stores(MemoryResetScope scope)
    {
        RequireSqlCipher();

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.ResetMemory, ResetCampaignA, scope));

        string message = Assert.Single(plan.Blockers).Message;

        Assert.Contains("Saga", message, StringComparison.Ordinal);

        Assert.Contains("Lexicon", message, StringComparison.Ordinal);

        Assert.Contains("Campaign summaries", message, StringComparison.Ordinal);

        Assert.DoesNotContain("only those two", message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Refusal_contract_keeps_missing_attachment_as_not_found()
    {
        RequireSqlCipher();

        Result<DataRetentionApplyResult> result = await CreateService().ApplyAsync(new(new(DataRetentionOperation.DeleteAttachment, Guid.NewGuid())));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.NotFound, result.Error.Code);

        Assert.Equal(0, await CountAllAsync("LongRunningOperations"));
    }

    [SkippableTheory]
    [InlineData(MemoryResetScope.Saga)]
    [InlineData(MemoryResetScope.Lexicon)]
    [InlineData(MemoryResetScope.CampaignSummary)]
    public async Task Refusal_contract_accepts_supported_campaign_reset_scopes(MemoryResetScope scope)
    {
        RequireSqlCipher();

        await SeedCampaignRowAsync(ResetCampaignA);

        DataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.ResetMemory, ResetCampaignA, scope);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Empty(plan.Blockers);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(result.IsSuccess, result.Error.Message);
    }
}
