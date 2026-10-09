using System.Globalization;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task PlanAndApplyAsync_SagaLongRest_ReportsAnExistingClaimWithoutAReceipt()
    {
        RequireSqlCipher();

        string memory = await SeedGlobalSagaMemoryAsync();

        await SeedClaimAsync(1, memory);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SagaMemories = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(2L, plan.DerivedRecords);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(plan.DerivedRecords, applied.Value.DerivedRecordsDeleted);

        Assert.Equal(0, await CountTableRowsAsync("annal_claims"));
    }

    [SkippableTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task PlanAndApplyAsync_SagaLongRest_CountsSharedReceiptCompanionsOnce(bool appliedReceipt, bool bothAged)
    {
        RequireSqlCipher();

        string source = await SeedGlobalSagaMemoryAsync();

        string survivor = await SeedGlobalSagaMemoryAsync();

        string bystander = await SeedGlobalSagaMemoryAsync();

        await SeedClaimAsync(1, source);

        await SeedClaimAsync(1, survivor);

        await SeedClaimAsync(1, bystander);

        string fresh = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        await ExecuteAsync("UPDATE saga_memories SET CreatedAt = @at WHERE Id = @id;", ("@at", fresh), ("@id", bystander));

        if (!bothAged)
        {
            await ExecuteAsync("UPDATE saga_memories SET CreatedAt = @at WHERE Id = @id;", ("@at", fresh), ("@id", survivor));
        }

        await SeedRetentionLongRestReceiptAsync(source, survivor, appliedReceipt);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SagaMemories = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        long selected = bothAged ? 2 : 1;

        // Existing Saga inventory counts each embedding and claim. The transformation adds one
        // receipt, two input snapshots, and (when applied) one exact-version suppression.
        Assert.Equal(selected, plan.Rows);

        Assert.Equal((selected * 2) + 3 + (appliedReceipt ? 1 : 0), plan.DerivedRecords);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(plan.DerivedRecords, applied.Value.DerivedRecordsDeleted);

        Assert.True(applied.Value.Reconciled);

        Assert.Equal(0, await CountTableRowsAsync("long_rest_receipts"));

        Assert.Equal(0, await CountTableRowsAsync("long_rest_receipt_inputs"));

        Assert.Equal(0, await CountTableRowsAsync("long_rest_suppressions"));

        Assert.Equal(bothAged ? 1 : 2, await CountTableRowsAsync("saga_memories"));

        Assert.Equal(1, await CountAnnalClaimsAsync(1, bystander));

        Assert.Equal(bothAged ? 0 : 1, await CountAnnalClaimsAsync(1, survivor));
    }

    private async Task SeedRetentionLongRestReceiptAsync(string source, string survivor, bool applied)
    {
        await ExecuteAsync(
            """
            INSERT INTO long_rest_receipts
                (ReceiptId, PolicyVersion, KindCode, OutcomeCode, ReasonCode, InputHash, OutputHash,
                 SurvivorMemoryId, SurvivorVersionId, CreatedAtUtc)
            SELECT 'retention-receipt', 1, 1, @outcome, @reason, @inputHash, @outputHash,
                   CASE WHEN @outcome = 1 THEN @survivor END,
                   CASE WHEN @outcome = 1 THEN v.VersionId END, @at
            FROM annal_claims c JOIN annal_versions v ON v.ClaimId = c.ClaimId
            WHERE c.SubjectStoreCode = 1 AND c.SubjectId = @survivor;

            INSERT INTO long_rest_receipt_inputs
                (ReceiptId, Ordinal, MemoryId, VersionId, ContentHashFormatCode, ContentHash, SnapshotHash, SnapshotJson)
            SELECT 'retention-receipt', CASE WHEN c.SubjectId = @source THEN 1 ELSE 2 END,
                   c.SubjectId, v.VersionId, v.ContentHashFormatCode, v.ContentHash, @inputHash, '{}'
            FROM annal_claims c JOIN annal_versions v ON v.ClaimId = c.ClaimId
            WHERE c.SubjectStoreCode = 1 AND c.SubjectId IN (@source, @survivor);

            INSERT INTO long_rest_suppressions (SourceVersionId, SurvivorVersionId, ReceiptId)
            SELECT source.VersionId, survivor.VersionId, 'retention-receipt'
            FROM annal_claims sourceClaim JOIN annal_versions source ON source.ClaimId = sourceClaim.ClaimId
            JOIN annal_claims survivorClaim ON survivorClaim.SubjectId = @survivor AND survivorClaim.SubjectStoreCode = 1
            JOIN annal_versions survivor ON survivor.ClaimId = survivorClaim.ClaimId
            WHERE sourceClaim.SubjectStoreCode = 1 AND sourceClaim.SubjectId = @source AND @outcome = 1;
            """,
            ("@source", source), ("@survivor", survivor), ("@outcome", applied ? 1 : 2), ("@reason", applied ? 0 : 1),
            ("@inputHash", new string('A', 64)), ("@outputHash", new string('B', 64)), ("@at", OldTimestamp));
    }
}
