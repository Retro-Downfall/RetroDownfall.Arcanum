using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed partial class LongRestSweepSchedulingTests
{
    [Fact]
    public async Task Fresh_clean_repeat_after_reboot_readmits_the_existing_never_started_identity_and_preserves_protected_work()
    {
        await using SchedulingHarness h = await SchedulingHarness.CreateAsync();

        Guid clean = await h.RequestAsync(CanonicalCampaignContext.GlobalOnly);

        Guid protectedId = LongRestSweepHarness.Require(await h.Coordinator.RequestAsync(h.F.Scope, h.F.Partition, CancellationToken.None)).Id;

        await h.AssertPendingAsync(clean);

        await h.AssertPendingAsync(protectedId);

        LongRestConfigurationStamp old = h.F.Epoch.Capture().Stamp;

        await h.F.RebootAsync();

        // The broad reboot helper expires all fixture leases; these requests have never held one.
        await h.F.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = NULL WHERE Id IN ($clean, $protected);",
            ("$clean", clean.ToString("N")), ("$protected", protectedId.ToString("N")));

        h.RecreateWorker();

        LongRestConfigurationStamp current = h.F.Epoch.Capture().Stamp;

        Assert.NotEqual(old.BootId, current.BootId);

        Assert.Equal(old.ValueDigest, current.ValueDigest);

        LongRunningOperation cleanBefore = await h.AssertPendingAsync(clean);

        LongRunningOperation protectedBefore = await h.AssertPendingAsync(protectedId);

        var protectedScope = LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(protectedId, CancellationToken.None));

        long claims = await h.CountAsync("SELECT count(*) FROM session_turn_claims;");

        long receipts = await h.CountAsync("SELECT count(*) FROM external_disclosure_receipts;");

        long operations = await h.CountAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';");

        Guid repeated = await h.RequestAsync(CanonicalCampaignContext.GlobalOnly);

        Assert.Equal(clean, repeated);

        Assert.Equal(operations, await h.CountAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));

        var admitted = LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(clean, CancellationToken.None));

        Assert.Equal(current, admitted.Configuration);

        Assert.Equal(cleanBefore.AttemptCount, (await h.AssertPendingAsync(clean)).AttemptCount);

        Assert.Null(LongRestSweepHarness.Require(await h.F.H.Store.ReadGenerationMetadataAsync(clean, CancellationToken.None)));

        // Discard the new hint too: the newly admitted durable stamp must carry authority.
        h.RecreateWorker();

        h.Expect(clean);

        await h.StartAsync();

        await h.WaitAndStopAsync();

        await h.AssertCompletedAsync();

        Assert.Equal(protectedBefore, await h.ReadAsync(protectedId));

        Assert.Equal(protectedScope, LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(protectedId, CancellationToken.None)));

        Assert.Null(LongRestSweepHarness.Require(await h.F.H.Store.ReadGenerationMetadataAsync(protectedId, CancellationToken.None)));

        Assert.Equal(claims, await h.CountAsync("SELECT count(*) FROM session_turn_claims;"));

        Assert.Equal(receipts, await h.CountAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        Assert.Equal(0L, await h.CountAsync("SELECT count(*) FROM LongRunningOperations o LEFT JOIN long_rest_sweeps s ON s.OperationId = o.Id WHERE o.Kind = 'long-rest-sweep' AND s.OperationId IS NULL;"));
    }

    [Fact]
    public async Task Fresh_clean_repeat_after_same_boot_gate_ABA_retires_only_the_never_started_scope_and_admits_current_work()
    {
        await using SchedulingHarness h = await SchedulingHarness.CreateAsync();

        Guid oldId = await h.RequestAsync(CanonicalCampaignContext.GlobalOnly);

        LongRunningOperation before = await h.AssertPendingAsync(oldId);

        var oldScope = LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(oldId, CancellationToken.None));

        object? stampBytes = await h.F.H.ScalarAsync($"SELECT ConfigurationJson FROM long_rest_sweeps WHERE OperationId = '{oldId:N}';");

        ChangeGateAndRestore(h);

        LongRestConfigurationStamp current = h.F.Epoch.Capture().Stamp;

        Assert.Equal(oldScope.Configuration.BootId, current.BootId);

        Assert.NotEqual(oldScope.Configuration.Epoch, current.Epoch);

        Assert.Equal(oldScope.Configuration.ValueDigest, current.ValueDigest);

        var repeat = await h.Coordinator.RequestAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None);

        // Check atomicity even on a refusal before asserting the intended successful disposition.
        Assert.Equal(0L, await h.CountAsync("SELECT count(*) FROM LongRunningOperations o LEFT JOIN long_rest_sweeps s ON s.OperationId = o.Id WHERE o.Kind = 'long-rest-sweep' AND s.OperationId IS NULL;"));

        Guid newId = LongRestSweepHarness.Require(repeat).Id;

        Assert.NotEqual(oldId, newId);

        LongRunningOperation retired = await h.ReadAsync(oldId);

        Assert.Equal(LongRunningOperationState.Abandoned, retired.State);

        Assert.NotNull(retired.CompletedAt);

        Assert.Equal(before.AttemptCount, retired.AttemptCount);

        Assert.Equal(before.CheckpointVersion, retired.CheckpointVersion);

        Assert.Null(retired.CheckpointPayload);

        Assert.Null(retired.LeaseOwner);

        Assert.Null(retired.LeaseExpiresAt);

        Assert.Equal(before.CreatedAt, retired.CreatedAt);

        Assert.Equal(oldScope, LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(oldId, CancellationToken.None)));

        Assert.Equal(stampBytes, await h.F.H.ScalarAsync($"SELECT ConfigurationJson FROM long_rest_sweeps WHERE OperationId = '{oldId:N}';"));

        Assert.Equal((long)LongRestSweepPhase.Invalidated, await h.F.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '{oldId:N}';"));

        Assert.Null(LongRestSweepHarness.Require(await h.F.H.Store.ReadGenerationMetadataAsync(oldId, CancellationToken.None)));

        Assert.Equal(2L, await h.CountAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));

        Assert.Equal(1L, await h.CountAsync("SELECT count(*) FROM long_rest_sweeps WHERE PhaseCode NOT IN (6, 7);"));

        await h.AssertPendingAsync(newId);

        Assert.Equal(current, LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(newId, CancellationToken.None)).Configuration);

        h.RecreateWorker();

        h.Expect(newId);

        await h.StartAsync();

        await h.WaitAndStopAsync();

        await h.AssertCompletedAsync();

        Assert.Equal(retired, await h.ReadAsync(oldId));
    }

    [Theory]
    [InlineData("started")]
    [InlineData("protected")]
    [InlineData("claimed")]
    public async Task Stale_scope_that_is_started_protected_or_process_claimed_is_refused_without_any_write(string blocker)
    {
        await using SchedulingHarness h = await SchedulingHarness.CreateAsync();

        bool protectedScope = blocker == "protected";

        Guid oldId = protectedScope
            ? LongRestSweepHarness.Require(await h.Coordinator.RequestAsync(h.F.Scope, h.F.Partition, CancellationToken.None)).Id
            : await h.RequestAsync(CanonicalCampaignContext.GlobalOnly);

        Guid ownershipToken = Guid.Empty;

        if (blocker == "started")
        {
            DateTimeOffset now = h.F.Clock.GetUtcNow();

            var lease = await h.F.Operations.TryAcquireLeaseAsync(oldId, "request-admission-control", now, now.AddMinutes(5));

            Assert.True(lease.Acquired);

            Assert.True(await h.F.Operations.SaveCheckpointAsync(oldId, "request-admission-control", 0, 1,
                [1], null, "Started control.", now));
        }
        else if (blocker == "claimed")
        {
            Assert.True(h.Ownership.TryClaim(oldId, out ownershipToken));
        }

        try
        {
            ChangeGateAndRestore(h);

            LongRunningOperation before = await h.ReadAsync(oldId);

            var stored = LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(oldId, CancellationToken.None));

            long writes = await h.CountAsync("SELECT total_changes();");

            var repeat = protectedScope
                ? await h.Coordinator.RequestAsync(h.F.Scope, h.F.Partition, CancellationToken.None)
                : await h.Coordinator.RequestAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None);

            Assert.Equal(writes, await h.CountAsync("SELECT total_changes();"));

            Assert.True(repeat.IsFailure);

            LongRunningOperation after = await h.ReadAsync(oldId);

            Assert.Equal(before with { CheckpointPayload = null }, after with { CheckpointPayload = null });

            Assert.Equal(before.CheckpointPayload, after.CheckpointPayload);

            Assert.Equal(stored, LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(oldId, CancellationToken.None)));

            Assert.Equal(1L, await h.CountAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));

            Assert.Equal(1L, await h.CountAsync("SELECT count(*) FROM long_rest_sweeps WHERE PhaseCode NOT IN (6, 7);"));

            Assert.Null(LongRestSweepHarness.Require(await h.F.H.Store.ReadGenerationMetadataAsync(oldId, CancellationToken.None)));

            Assert.Equal(0, h.F.Handler.SendCount);

            if (ownershipToken != Guid.Empty) Assert.True(h.Ownership.IsClaimedBy(oldId, ownershipToken));
        }
        finally
        {
            if (ownershipToken != Guid.Empty) Assert.True(h.Ownership.Release(oldId, ownershipToken));
        }
    }

    private static void ChangeGateAndRestore(SchedulingHarness h)
    {
        var disabled = LongRestConfigurationEpochTests.Settings();

        disabled.Features.LongRest = false;

        h.F.Options.Set(disabled);

        h.F.Options.Set(LongRestConfigurationEpochTests.Settings());
    }
}
