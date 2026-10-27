using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class CovenantRetentionTests
{
    [SkippableFact]
    public async Task Targeted_Covenant_reset_plan_refuses_without_inventory_or_admission()
    {
        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        IReadOnlyDictionary<string, long> before = await CountCovenantTablesAsync(CancellationToken.None);

        RecordingCovenantOperationGate gate = new();

        DataRetentionPlan plan = await CreateService(covenantGate: gate).PlanAsync(TargetedCovenantReset());

        Assert.Equal(ErrorCodes.Data.InvalidRequest, Assert.Single(plan.Blockers).ReasonCode);

        Assert.Null(plan.Covenant);

        Assert.Empty(plan.Items);

        Assert.Empty(plan.CandidateIds);

        Assert.Empty(gate.Acquisitions);

        IReadOnlyDictionary<string, long> after = await CountCovenantTablesAsync(CancellationToken.None);

        foreach ((string table, long count) in before) Assert.Equal(count, after[table]);
    }

    [SkippableTheory]
    [InlineData(DataRetentionPlanAdmissionCapability.Request, false)]
    [InlineData(DataRetentionPlanAdmissionCapability.Request, true)]
    [InlineData(DataRetentionPlanAdmissionCapability.Installation, false)]
    [InlineData(DataRetentionPlanAdmissionCapability.Installation, true)]
    public async Task Targeted_Covenant_reset_admission_refuses_before_a_read_lease(DataRetentionPlanAdmissionCapability capability, bool admissionClosed)
    {
        RequireSqlCipher();

        RecordingCovenantOperationGate gate = new()
        {
            InstallationReadFailure = admissionClosed
                ? new(ErrorCodes.Covenant.ErasureIncomplete, "Admission remains closed.") : null,
        };

        Result<DataRetentionPlanAdmission> result = await CreateService(covenantGate: gate).PlanAdmissionAsync(
            TargetedCovenantReset(), capability: capability);

        if (result.IsSuccess && result.Value.ReadLease is { } lease) await lease.DisposeAsync();

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.InvalidRequest, result.Error.Code);

        Assert.Empty(gate.Acquisitions);

        Assert.Equal(0, gate.LiveLeases);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Targeted_Covenant_reset_apply_refuses_before_lifecycle_or_operation(bool expectedPlan)
    {
        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        IReadOnlyDictionary<string, long> before = await CountCovenantTablesAsync(CancellationToken.None);

        RecordingCovenantOperationGate gate = new();

        Result<DataRetentionApplyResult> result = await CreateService(covenantGate: gate).ApplyAsync(
            new(TargetedCovenantReset(), expectedPlan ? "untrusted-preview" : null));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.InvalidRequest, result.Error.Code);

        Assert.Empty(gate.Acquisitions);

        IReadOnlyDictionary<string, long> after = await CountCovenantTablesAsync(CancellationToken.None);

        foreach ((string table, long count) in before) Assert.Equal(count, after[table]);

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM LongRunningOperations", CancellationToken.None));
    }

    private static DataRetentionRequest TargetedCovenantReset() =>
        new(DataRetentionOperation.ResetMemory, Guid.Parse("21b28cad-1bd3-4f19-b5cf-58248b6cf1d7"), MemoryResetScope.Covenant);
}
