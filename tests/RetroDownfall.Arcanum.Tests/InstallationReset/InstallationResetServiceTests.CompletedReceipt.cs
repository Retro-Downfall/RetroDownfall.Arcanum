using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// A nested erasure that stored its completion receipt is never retired on the strength of an error code.
/// </summary>
/// <remarks>
/// The receipt records that the nested transition reached its terminal verdict, which a rollback before
/// any effect reaches as well as a commit, so what it keeps is the evidence of the verdict.
/// </remarks>
public sealed partial class InstallationResetServiceTests
{
    /// <summary>
    /// The codes the data step answers with before it has changed anything. A call that failed on the
    /// way into an erasure which had already committed returns the same ones, so none of them can be
    /// allowed to retire the record once the receipt records the nested transition's terminal verdict.
    /// </summary>
    [Theory]
    [InlineData(ErrorCodes.Data.InvalidRequest)]
    [InlineData(ErrorCodes.Data.PlanChanged)]
    [InlineData(ErrorCodes.Data.Blocked)]
    [InlineData(ErrorCodes.Data.Conflict)]
    [InlineData(ErrorCodes.Data.NotFound)]
    [InlineData(ErrorCodes.Data.ConfirmationRequired)]
    [InlineData(ErrorCodes.Data.InventoryUnavailable)]
    [InlineData(ErrorCodes.Covenant.ForbiddenAuthority)]
    public async Task A_refusal_after_the_nested_erasure_stored_its_completion_keeps_the_record_whatever_code_it_carries(
        string code)
    {
        FakeActiveStore active = new() { RequireCurrentPublication = true };

        FakeDataService data = new(CreateDataPlan("global-data"))
        {
            ApplyResult = Result<DataRetentionApplyResult>.Failure(new Error(code, "A read failed on the way in.")),
        };

        data.BeforeApply = () =>
        {
            InstallationResetNestedTransitionReceiptV1 claimed = Assert.IsType<
                InstallationResetNestedTransitionReceiptV1>(active.Record!.NestedTransitionReceipt);

            active.Publish(active.Record with
            {
                NestedTransitionReceipt = claimed with
                {
                    Phase = InstallationResetNestedTransitionPhase.Completed,
                    NestedEffectDigest = Digest(0x71),
                    TerminalWinnerDigest = Digest(0x72),
                },
            });
        };

        InstallationResetService service = CreateService(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest request = new(InstallationResetScope.Global, "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(request, CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(
            service,
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.True(result.Value.ResumeRequired);

        Assert.True(result.Value.PointOfNoReturn);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Value.ErrorCode);

        Assert.False(active.Retired);

        Assert.Empty(active.RetiredOperationIds);

        InstallationResetActiveRecord kept = Assert.IsType<InstallationResetActiveRecord>(active.Record);

        Assert.Equal(InstallationResetPhase.Prepared, kept.Phase);

        Assert.True(kept.PointOfNoReturn);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, kept.LastErrorCode);

        Assert.Equal(
            InstallationResetNestedTransitionPhase.Completed,
            kept.NestedTransitionReceipt?.Phase);
    }
}
