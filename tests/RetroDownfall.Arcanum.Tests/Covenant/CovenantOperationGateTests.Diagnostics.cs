using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed partial class CovenantOperationGateTests
{
    [Theory]
    [InlineData("live")]
    [InlineData("kept")]
    [InlineData("disposed")]
    public async Task A_blocked_read_names_the_real_operation_and_distinguishes_live_work_from_kept_recovery(string state)
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveRecoveryOwner owner = CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair);

        await using CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(owner, Token)).Value;

        if (state == "kept")
        {
            Assert.True((await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, Token)).IsSuccess);
        }
        else if (state == "disposed")
        {
            await exclusive.DisposeAsync();
        }

        Result<CovenantInstallationReadLease> blocked = await gate.AcquireInstallationReadAsync(Token);

        Assert.True(blocked.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, blocked.Error.Code);

        Assert.Contains(nameof(CovenantExclusiveOperation.SchemaRepair), blocked.Error.Message, StringComparison.Ordinal);

        if (state == "live")
        {
            Assert.Contains("in progress", blocked.Error.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains("recovery", blocked.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("in progress", blocked.Error.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(owner.OperationId.ToString("D"), blocked.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(owner.EffectDigest.ToString(), blocked.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_conflicting_exclusive_request_names_the_existing_blocker_rather_than_the_requested_operation(bool kept)
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair), Token)).Value;

        if (kept)
        {
            Assert.True((await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, Token)).IsSuccess);
        }

        Result<CovenantExclusiveLease> refused = await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.BackupRestore), Token);

        Assert.True(refused.IsFailure);

        Assert.Contains(nameof(CovenantExclusiveOperation.SchemaRepair), refused.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(nameof(CovenantExclusiveOperation.BackupRestore), refused.Error.Message, StringComparison.Ordinal);

        Assert.Contains(kept ? "recovery" : "in progress", refused.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_with_a_different_owner_names_the_operation_that_keeps_the_scope_closed(bool kept)
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair), Token)).Value;

        if (kept)
        {
            Assert.True((await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, Token)).IsSuccess);
        }

        Result<CovenantExclusiveLease> refused = await gate.ResumeExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.BackupRestore), Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, refused.Error.Code);

        Assert.Contains(nameof(CovenantExclusiveOperation.SchemaRepair), refused.Error.Message, StringComparison.Ordinal);

        Assert.Contains(kept ? "recovery" : "in progress", refused.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Installation_refusal_prefers_actual_kept_recovery_over_a_live_other_campaign_in_either_insertion_order(bool reverse)
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        async Task<CovenantCampaignExclusiveLease> AcquireAsync(bool kept)
        {
            CovenantCampaignExclusiveLease lease = (await gate.AcquireCampaignExclusiveAsync(
                kept ? CovenantOperationGateFixture.CampaignTwo : CovenantOperationGateFixture.CampaignOne,
                CovenantOperationGateFixture.Owner(kept ? CovenantExclusiveOperation.CampaignDelete
                    : CovenantExclusiveOperation.CampaignPathMutation, operationId: Guid.NewGuid()), Token)).Value;

            if (kept)
            {
                Assert.True((await lease.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, Token)).IsSuccess);
            }

            return lease;
        }

        await using CovenantCampaignExclusiveLease first = await AcquireAsync(reverse);

        await using CovenantCampaignExclusiveLease second = await AcquireAsync(!reverse);

        Result<CovenantInstallationReadLease> blocked = await gate.AcquireInstallationReadAsync(Token);

        Assert.True(blocked.IsFailure);

        Assert.Contains(nameof(CovenantExclusiveOperation.CampaignDelete), blocked.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(nameof(CovenantExclusiveOperation.CampaignPathMutation), blocked.Error.Message, StringComparison.Ordinal);

        Assert.Contains("recovery", blocked.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(CovenantOperationGateFixture.CampaignTwo.ToString("D"), blocked.Error.Message, StringComparison.OrdinalIgnoreCase);

        Result<CovenantReadLease> global = await gate.AcquireReadAsync(CovenantOperationScope.Global, Token);

        Assert.True(global.IsSuccess);

        await global.Value.DisposeAsync();
    }

    [Fact]
    public async Task A_real_draining_exclusive_operation_reports_live_work_before_its_lease_is_returned()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantInstallationReadLease held = (await gate.AcquireInstallationReadAsync(Token)).Value;

        TaskCompletionSource revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenRegistration signal = held.Revocation.Register(() => revoked.TrySetResult());

        Task<Result<CovenantExclusiveLease>> acquisition = gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair), Token).AsTask();

        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(acquisition.IsCompleted);

        Result<CovenantInstallationReadLease> blocked = await gate.AcquireInstallationReadAsync(Token);

        Assert.True(blocked.IsFailure);

        Assert.Contains(nameof(CovenantExclusiveOperation.SchemaRepair), blocked.Error.Message, StringComparison.Ordinal);

        Assert.Contains("in progress", blocked.Error.Message, StringComparison.OrdinalIgnoreCase);

        await held.DisposeAsync();

        Result<CovenantExclusiveLease> acquired = await acquisition.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(acquired.IsSuccess);

        await using CovenantExclusiveLease exclusive = acquired.Value;

        Assert.True((await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, Token)).IsSuccess);
    }
}
