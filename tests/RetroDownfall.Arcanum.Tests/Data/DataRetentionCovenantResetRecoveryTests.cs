using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Issue #118 — how the shipped recovery handler treats each retention checkpoint version.
/// </summary>
/// <remarks>
/// Three arms, and each one has a different right answer. A version-0 row never reached its durable
/// journal and must close, because a parked retention row blocks every later retention operation. A
/// version-2 row is an ordinary mutation and reconciles exactly as it always did — this build
/// changed its priority, not its payload. A version-3 row carrying a Covenant arm is an interrupted
/// erasure: its owner is rebuilt from the checkpoint alone and it parks, because resuming it needs
/// the exclusive erasure coordinator this build does not have, and restarting it would run a second
/// dataset replacement over a family whose canonical arm may already be gone (§10.20.3).
/// </remarks>
public sealed partial class DataRetentionServiceTests
{

    private static readonly string CovenantResetEffect = new('a', 64);

    private static readonly string CovenantScopeCode =
        ((int)MemoryResetScope.Covenant).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static TheoryData<CovenantResetPhase> EveryResetPhase()
    {

        TheoryData<CovenantResetPhase> data = [];

        foreach (CovenantResetPhase phase in CovenantResetPhaseMachine.Ordered)
        {

            data.Add(phase);

        }

        return data;

    }

    [SkippableTheory]

    [MemberData(nameof(EveryResetPhase))]
    public async Task RecoverMutationAsync_FromEveryV3Phase_IsIdempotentAndChangesNothing(
        CovenantResetPhase phase)
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = await SeedCovenantResetCheckpointAsync(operations, phase);

        DataRetentionService service = CreateService(operationStore: operations);

        LongRunningOperationRecoveryResult first = await service.RecoverMutationAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        LongRunningOperationRecoveryResult second = await service.RecoverMutationAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, first.State);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, first.ErrorCode);

        Assert.Equal(first.State, second.State);

        Assert.Equal(first.ErrorCode, second.ErrorCode);

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        // The checkpoint is still active and still at the exact phase the crash left it at, so the
        // next start faces the same durable evidence rather than a phase this pass invented.
        Assert.Equal(DataRetentionMutationCheckpointV3.CurrentVersion, after.CheckpointVersion);

        Assert.Equal(
            phase,
            CovenantRecoveryCheckpointCodec
                .DecodeDataRetentionMutation(after.CheckpointPayload!)
                .Value
                .Covenant!
                .Phase);

    }

    /// <summary>
    /// The handler rebuilds the owner itself, from the payload it decoded, and reports the exact
    /// phase it found.
    /// </summary>
    /// <remarks>
    /// Asserted through the log rather than the result code, because every failure of a Covenant
    /// erasure deliberately carries the same single code. The recorded phase is the one thing that
    /// distinguishes a decoded, adopted checkpoint from a refused one, and without this the whole
    /// method could be replaced by a constant return.
    /// </remarks>
    [SkippableTheory]

    [MemberData(nameof(EveryResetPhase))]
    public async Task RecoverMutationAsync_FromEveryV3Phase_AdoptsTheCheckpointAndReportsItsPhase(
        CovenantResetPhase phase)
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = await SeedCovenantResetCheckpointAsync(operations, phase);

        RecordingLogger<DataRetentionService> log = new();

        LongRunningOperationRecoveryResult result =
            await CreateService(operationStore: operations, logger: log).RecoverMutationAsync(
                (await operations.GetAsync(operation.Id))!,
                CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        string adopted = Assert.Single(
            log.Messages,
            message => message.Contains("Covenant reset was interrupted", StringComparison.Ordinal));

        Assert.Contains(phase.ToString(), adopted, StringComparison.Ordinal);

        Assert.Contains(operation.Id.ToString(), adopted, StringComparison.Ordinal);

    }

    /// <summary>
    /// A V3 arm naming another operation is refused rather than adopted. Adopting it would hand a
    /// closed scope to an operation that never closed it.
    /// </summary>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithV3ArmNamingAnotherOperation_RefusesWithoutAdopting()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            DataRetentionMutationCheckpointV3.CurrentVersion,
            payload: null,
            checkpointFactory: _ => CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionMutationCheckpointV3(
                    DataRetentionMutationCheckpointV3.CurrentVersion,
                    Subtype: "reset-memory",
                    Target: CovenantScopeCode,
                    new CovenantResetEffectArmV1(
                        Guid.Parse("deadbeef-dead-4eef-8eef-deadbeefdead"),
                        CovenantResetEffect,
                        CovenantExclusiveOperation.CovenantReset,
                        CovenantResetPhase.CanonicalApplied))));

        RecordingLogger<DataRetentionService> log = new();

        LongRunningOperationRecoveryResult result =
            await CreateService(operationStore: operations, logger: log).RecoverMutationAsync(
                (await operations.GetAsync(operation.Id))!,
                CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, result.ErrorCode);

        Assert.DoesNotContain(
            log.Messages,
            message => message.Contains("Covenant reset was interrupted", StringComparison.Ordinal));

    }

    /// <summary>
    /// A V3 row whose payload this build cannot read parks rather than being abandoned. Abandoning
    /// would discard the only durable record that a family is half erased.
    /// </summary>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithUnreadableV3Payload_ParksWithoutAdopting()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            DataRetentionMutationCheckpointV3.CurrentVersion,
            "not a checkpoint"u8.ToArray());

        RecordingLogger<DataRetentionService> log = new();

        LongRunningOperationRecoveryResult result =
            await CreateService(operationStore: operations, logger: log).RecoverMutationAsync(
                (await operations.GetAsync(operation.Id))!,
                CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, result.ErrorCode);

        Assert.DoesNotContain(
            log.Messages,
            message => message.Contains("Covenant reset was interrupted", StringComparison.Ordinal));

    }

    /// <summary>
    /// A V3 row with no Covenant arm closed no admission, so it has no exclusive scope to adopt and
    /// no erasure to attribute to it. It is an ordinary reconciliation failure rather than a
    /// Covenant escalation, and the two codes must stay distinguishable.
    /// </summary>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithV3ArmAbsent_IsAnOrdinaryReconciliationFailure()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            DataRetentionMutationCheckpointV3.CurrentVersion,
            payload: null,
            checkpointFactory: static _ => CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionMutationCheckpointV3(
                    DataRetentionMutationCheckpointV3.CurrentVersion,
                    Subtype: "delete-session",
                    Target: "f0f0f0f0f0f04f0f8f0f0f0f0f0f0f0f",
                    Covenant: null)));

        LongRunningOperationRecoveryResult result =
            await CreateService(operationStore: operations).RecoverMutationAsync(
                (await operations.GetAsync(operation.Id))!,
                CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.ErrorCode);

    }

    /// <summary>
    /// The V2 arm is untouched by this slice. An ordinary reset-memory journal written before the
    /// V3 shape existed still decodes, still reconciles against the database that is the commit
    /// authority, and still completes — it never falls through the new V3 branch and never runs a
    /// second dataset replacement.
    /// </summary>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithLegacyV2Journal_StillResumesAndCompletes()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            checkpointVersion: 2,
            LegacyResetMemoryJournal());

        DataRetentionService service = CreateService(operationStore: operations);

        LongRunningOperationRecoveryResult result = await service.RecoverMutationAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, result.State);

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        Assert.Equal(2, after.CheckpointVersion);

    }

    /// <summary>
    /// A version-0 row still closes. The V3 branch must not capture the window between the
    /// single-flight insert and the first journal, because parking it would wedge retention.
    /// </summary>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithVersionZeroRow_StillAbandonsAsNeverStarted()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Interrupted before its journal.",
                DateTimeOffset.UtcNow));

        DataRetentionService service = CreateService(operationStore: operations);

        LongRunningOperationRecoveryResult result = await service.RecoverMutationAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, result.State);

        Assert.Equal(
            LongRunningOperationRecoveryOutcomes.RetentionMutationNeverStarted,
            result.ErrorCode);

    }

    [SkippableTheory]

    [MemberData(nameof(EveryResetPhase))]
    public async Task RecoverFactoryResetAsync_FromEveryV1Phase_ParksWithoutRestartingTheErasure(
        CovenantResetPhase phase)
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            DataRetentionFactoryResetCheckpointV1.CurrentVersion,
            payload: null,
            LongRunningOperationKinds.DataRetentionFactoryReset,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            checkpointFactory: id => CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionFactoryResetCheckpointV1(
                    DataRetentionFactoryResetCheckpointV1.CurrentVersion,
                    id,
                    CovenantResetEffect,
                    CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                    phase)));

        DataRetentionService service = CreateService(operationStore: operations);

        LongRunningOperationRecoveryResult first = await service.RecoverFactoryResetAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        LongRunningOperationRecoveryResult second = await service.RecoverFactoryResetAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, first.State);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, first.ErrorCode);

        Assert.Equal(first.State, second.State);

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        Assert.Equal(
            phase,
            CovenantRecoveryCheckpointCodec
                .DecodeDataRetentionFactoryReset(after.CheckpointPayload!)
                .Value
                .Phase);

    }

    private async Task<LongRunningOperation> SeedCovenantResetCheckpointAsync(
        LongRunningOperationStore operations,
        CovenantResetPhase phase) =>
        await SeedCheckpointAsync(
            operations,
            DataRetentionMutationCheckpointV3.CurrentVersion,
            payload: null,
            checkpointFactory: id => CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionMutationCheckpointV3(
                    DataRetentionMutationCheckpointV3.CurrentVersion,
                    Subtype: "reset-memory",
                    Target: CovenantScopeCode,
                    new CovenantResetEffectArmV1(
                        id,
                        CovenantResetEffect,
                        CovenantExclusiveOperation.CovenantReset,
                        phase))));

    /// <summary>
    /// Leaves the row in the exact state a dead process leaves behind: the checkpoint it managed to
    /// write, under a lease that has already expired.
    /// </summary>
    private async Task<LongRunningOperation> SeedCheckpointAsync(
        LongRunningOperationStore operations,
        int checkpointVersion,
        byte[]? payload,
        string kind = LongRunningOperationKinds.DataRetentionMutation,
        LongRunningOperationRecoveryPolicy policy =
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
        Func<Guid, byte[]>? checkpointFactory = null)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string ownerId = "covenant-reset-recovery-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                kind,
                policy,
                "Interrupted Covenant erasure.",
                now));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5));

        Assert.True(lease.Acquired);

        string reference = kind == LongRunningOperationKinds.DataRetentionMutation
            ? "retention-mutation:" + operation.Id.ToString("N")
            : "retention-factory-reset:" + operation.Id.ToString("N");

        Assert.True(
            await operations.SaveCheckpointAsync(
                operation.Id,
                ownerId,
                expectedCheckpointVersion: 0,
                checkpointVersion,
                checkpointFactory?.Invoke(operation.Id) ?? payload,
                reference,
                operation.PublicSummary,
                now));

        return operation;

    }

    /// <summary>
    /// Captures the recovery handler's own log, which is the only surface that distinguishes an
    /// adopted checkpoint from a refused one — every Covenant erasure failure deliberately carries
    /// the same single error code.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {

        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

    }

    /// <summary>
    /// The exact <c>ARCAMUT2</c> payload a pre-#118 build wrote for an entry-scope memory reset: no
    /// captured file entries, and the digest line the parser verifies.
    /// </summary>
    private static byte[] LegacyResetMemoryJournal()
    {

        string body =
            "ARCAMUT2\n"
            + "reset-memory\n"
            + Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(
                    ((int)MemoryResetScope.Entry).ToString(
                        System.Globalization.CultureInfo.InvariantCulture)))
            + "\n0\n";

        string digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(body)));

        return System.Text.Encoding.UTF8.GetBytes(body + "H:" + digest + "\n");

    }

}
