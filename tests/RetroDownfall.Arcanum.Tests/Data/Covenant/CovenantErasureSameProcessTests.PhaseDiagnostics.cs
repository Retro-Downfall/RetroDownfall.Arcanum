using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class CovenantErasureSameProcessTests
{
    [SkippableTheory]
    [InlineData(ErrorCodes.Data.Conflict, "reported a conflict", false)]
    [InlineData(ErrorCodes.Data.Conflict, "reported a conflict", true)]
    [InlineData(ErrorCodes.Covenant.ManualArtifactErasureRequired, "require artifact-specific erasure", false)]
    [InlineData(ErrorCodes.Covenant.ManualArtifactErasureRequired, "require artifact-specific erasure", true)]
    [InlineData(ErrorCodes.Covenant.IntegrityFailure, "could not establish integrity", false)]
    [InlineData(ErrorCodes.Covenant.IntegrityFailure, "could not establish integrity", true)]
    [InlineData(ErrorCodes.Covenant.MaintenanceFailed, "maintenance checks failed", false)]
    [InlineData(ErrorCodes.Covenant.MaintenanceFailed, "maintenance checks failed", true)]
    [InlineData(ErrorCodes.Covenant.InvalidScope, "pre-effect checks could not be proved", false)]
    [InlineData(ErrorCodes.Covenant.InvalidScope, "pre-effect checks could not be proved", true)]
    public async Task Pre_effect_refusal_uses_fixed_code_level_reason_without_private_message(
        string code,
        string reason,
        bool authenticatedRetry)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Error refusal = new(code, reason + ". PRIVATE-PRE-EFFECT-CANARY /private/provider-key/catalog\r\nforged-warning");

        RouteOperationWriteObserver writes = new();

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            operationWrites: writes,
            serviceOverrides: services =>
            {
                services.RemoveAll<ICovenantErasureInventorySource>();

                services.AddScoped<ICovenantErasureInventorySource>(provider => new RouteFailureInventory(
                    RouteFailure.Rollback, provider.GetRequiredService<CovenantErasureInventorySource>(), refusal));

                if (authenticatedRetry)
                {
                    services.RemoveAll<ICovenantDisclosureWriterLifecycle>();

                    services.AddSingleton<ICovenantDisclosureWriterLifecycle>(provider =>
                        new RefusingRestartWriter(provider.GetRequiredService<CovenantDisclosureWriter>()));
                }
            });

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        DataRetentionPlan plan = await harness.PlanResetAsync();

        Result<DataRetentionApplyResult> applied = await harness.ApplyResetAsync(plan.PlanId);

        Assert.True(applied.IsFailure);

        Assert.Equal(authenticatedRetry ? ErrorCodes.Covenant.MaintenanceFailed : code, applied.Error.Code);

        TestLogEntry warning;

        if (authenticatedRetry)
        {
            await AssertBothAdmissionsClosedAsync(harness);

            PreparedAuthenticatedRecovery prepared = await harness.PrepareAuthenticatedResetAsync(writes.LastSuccessfulWrite);

            await using CovenantErasureCoordinator.AuthenticatedCovenantErasureRecoveryAdmission admission = prepared.Admission;

            Result<CovenantErasureCompletion> completion = await prepared.Coordinator.RunAuthenticatedAsync(
                prepared.Operation, prepared.Checkpoint, prepared.OwnerId, admission, CancellationToken.None);

            Assert.True(completion.IsSuccess, completion.IsFailure ? completion.Error.Message : null);

            Assert.Equal(CovenantExclusiveLeaseDisposition.RollbackAndReopen, completion.Value.Disposition);

            Assert.Equal(code, completion.Value.BlockingErrorCode);

            TestLogEntry[] warnings = [.. harness.Services.GetRequiredService<TestCapturingLogger<CovenantErasureCoordinator>>().Entries
                .Where(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("inventory refused", StringComparison.Ordinal))];

            Assert.Equal(2, warnings.Length);

            Assert.Contains("admission stays closed", warnings[0].Message);

            warning = warnings[1];
        }
        else
        {
            warning = Assert.Single(harness.Services.GetRequiredService<TestCapturingLogger<CovenantErasureCoordinator>>().Entries,
                entry => entry.Level == LogLevel.Warning && entry.Message.Contains("inventory refused", StringComparison.Ordinal));
        }

        Assert.Contains("admission reopens", warning.Message);

        AssertPreEffectDiagnostic(warning, writes.LastSuccessfulWrite.Id, code, reason);

        Result<CovenantReadLease> read = await harness.RouteGate.AcquireReadAsync(CovenantOperationScope.Global, CancellationToken.None);

        Assert.True(read.IsSuccess, read.IsFailure ? read.Error.Message : null);

        await read.Value.DisposeAsync();

        IGrimoireConnectionAdmissionGate gate = harness.Services.GetRequiredService<IGrimoireConnectionAdmissionGate>();

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SessionAttachmentIndexing, out IGrimoireWorkLease? work));

        Assert.NotNull(work);

        await work.DisposeAsync();
    }

    [SkippableFact]
    public async Task Fixed_pre_effect_reason_survives_unconfirmed_terminal_finalization()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Error refusal = new(ErrorCodes.Data.Conflict, "reported a conflict. PRIVATE-PRE-EFFECT-CANARY /private/provider-key/catalog");

        RouteOperationWriteObserver writes = new();

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            operationWrites: writes,
            terminalSuffixFaultSeam: (boundary, _) => Task.FromResult(boundary is GrimoireOfflineTransitionTerminalSuffixBoundary.DatabaseTerminalized
                ? Result.Failure(new Error(ErrorCodes.Covenant.MaintenanceFailed, "PRIVATE-PRE-EFFECT-CANARY finalization"))
                : Result.Success()),
            serviceOverrides: services =>
            {
                services.RemoveAll<ICovenantErasureInventorySource>();

                services.AddScoped<ICovenantErasureInventorySource>(provider => new RouteFailureInventory(
                    RouteFailure.Rollback, provider.GetRequiredService<CovenantErasureInventorySource>(), refusal));
            });

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await before.ReadLease.DisposeAsync();

        DataRetentionPlan plan = await harness.PlanResetAsync();

        Result<DataRetentionApplyResult> applied = await harness.ApplyResetAsync(plan.PlanId);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, applied.Error.Code);

        TestLogEntry warning = Assert.Single(harness.Services.GetRequiredService<TestCapturingLogger<CovenantErasureCoordinator>>().Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("inventory refused", StringComparison.Ordinal));

        AssertPreEffectDiagnostic(warning, writes.LastSuccessfulWrite.Id, refusal.Code, "reported a conflict");

        Assert.Contains("admission reopening is unconfirmed", warning.Message);

        await AssertBothAdmissionsClosedAsync(harness);
    }

    private static void AssertPreEffectDiagnostic(TestLogEntry warning, Guid operationId, string code, string reason)
    {
        Assert.Null(warning.Exception);

        Assert.Equal(operationId, Assert.IsType<Guid>(Assert.Single(warning.Properties!, property => property.Key == "OperationId").Value));

        Assert.Equal(CovenantResetPhase.InventoryPrepared, Assert.IsType<CovenantResetPhase>(Assert.Single(warning.Properties!, property => property.Key == "ResetPhase").Value));

        Assert.Equal(code, Assert.Single(warning.Properties!, property => property.Key == "ErrorCode").Value);

        string diagnostic = Assert.IsType<string>(Assert.Single(warning.Properties!, property => property.Key == "InventoryDiagnostic").Value);

        Assert.Contains(reason, diagnostic, StringComparison.Ordinal);

        Assert.DoesNotContain("PRIVATE-PRE-EFFECT-CANARY", warning.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("forged-warning", warning.Message, StringComparison.Ordinal);

        Assert.All(warning.Properties!, property => Assert.DoesNotContain("PRIVATE-PRE-EFFECT-CANARY", property.Value?.ToString() ?? string.Empty, StringComparison.Ordinal));
    }
}
