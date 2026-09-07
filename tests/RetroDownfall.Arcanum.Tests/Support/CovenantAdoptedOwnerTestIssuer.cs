using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Gives downstream tests an adopted-owner capability only by driving the production row validator
/// and gate-adoption path that issues it.
/// </summary>
internal static class CovenantAdoptedOwnerTestIssuer
{
    private static readonly Guid SourceGeneration =
        Guid.Parse("44444444-4444-4444-8444-444444444444");

    private static readonly Guid TargetGeneration =
        Guid.Parse("55555555-5555-4555-8555-555555555555");

    internal static LongRunningOperation BuildLaunch(
        CovenantExclusiveRecoveryOwner owner,
        long revision = 7)
    {
        string kind;

        LongRunningOperationRecoveryPolicy policy;

        int version;

        byte[] payload;

        CovenantOfflineTransitionEpochsV1 source = new(11, 22, 33);

        CovenantOfflineTransitionEpochsV1 target = new(12, 23, 34);

        switch (owner.Operation)
        {
            case CovenantExclusiveOperation.CovenantReset:
                kind = LongRunningOperationKinds.DataRetentionMutation;

                policy = LongRunningOperationRecoveryPolicy.ReconcileAndComplete;

                version = CovenantOfflineTransitionLaunchV4.CurrentVersion;

                payload = CovenantRecoveryCheckpointCodec.Encode(
                    new CovenantOfflineTransitionLaunchV4(
                        version,
                        owner.OperationId,
                        kind,
                        nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
                        owner.Operation,
                        CovenantRecoveryCheckpointCodec.EncodeEffectDigest(owner.EffectDigest),
                        SourceGeneration,
                        TargetGeneration,
                        source,
                        target,
                        revision));

                break;

            case CovenantExclusiveOperation.HealthyCatalogFactoryErasure:
                kind = LongRunningOperationKinds.DataRetentionFactoryReset;

                policy = LongRunningOperationRecoveryPolicy.RestartIdempotently;

                version = DataRetentionFactoryTransitionLaunchV2.CurrentVersion;

                payload = CovenantRecoveryCheckpointCodec.Encode(
                    new DataRetentionFactoryTransitionLaunchV2(
                        version,
                        owner.OperationId,
                        kind,
                        nameof(LongRunningOperationRecoveryPolicy.RestartIdempotently),
                        owner.Operation,
                        CovenantRecoveryCheckpointCodec.EncodeEffectDigest(owner.EffectDigest),
                        SourceGeneration,
                        TargetGeneration,
                        source,
                        target,
                        revision));

                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(owner),
                    "Only current offline Covenant-erasure owners have adopted launch rows.");
        }

        DateTimeOffset createdAt = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

        return new LongRunningOperation(
            owner.OperationId,
            kind,
            LongRunningOperationState.Running,
            policy,
            RootOperationId: null,
            ParentOperationId: null,
            SessionId: null,
            RunId: null,
            InferenceRunId: null,
            BudgetReservationId: null,
            IdempotencyClaimId: null,
            createdAt,
            StartedAt: createdAt,
            HeartbeatAt: createdAt,
            CompletedAt: null,
            LeaseOwner: "crashed-owner",
            LeaseExpiresAt: createdAt.AddMinutes(-1),
            AttemptCount: 1,
            CheckpointVersion: version,
            CheckpointPayload: payload,
            CheckpointReference: CovenantResetCheckpointInitiator.CheckpointReference(
                kind,
                owner.OperationId),
            PublicSummary: "Interrupted Covenant erasure.",
            TerminalErrorCode: null,
            Revision: revision);
    }

    internal static async Task<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner> IssueAsync(
        LongRunningOperation launch)
    {
        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = new("Data Source=:memory:;Pooling=False");

        await connection.OpenAsync();

        await using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE "LongRunningOperations" (
                    "Id" TEXT NOT NULL,
                    "Kind" TEXT NOT NULL,
                    "State" INTEGER NOT NULL,
                    "RecoveryPolicy" INTEGER NOT NULL,
                    "CheckpointVersion" INTEGER NOT NULL,
                    "CheckpointReference" TEXT,
                    "CheckpointPayload" BLOB,
                    "CreatedAt" TEXT NOT NULL,
                    "Revision" INTEGER NOT NULL
                );
                """;

            _ = await create.ExecuteNonQueryAsync();
        }

        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO "LongRunningOperations" (
                    "Id", "Kind", "State", "RecoveryPolicy", "CheckpointVersion",
                    "CheckpointReference", "CheckpointPayload", "CreatedAt", "Revision")
                VALUES (
                    @id, @kind, @state, @policy, @version,
                    @reference, @payload, @created, @revision);
                """;

            _ = insert.Parameters.AddWithValue("@id", launch.Id.ToString("N"));

            _ = insert.Parameters.AddWithValue("@kind", launch.Kind);

            _ = insert.Parameters.AddWithValue("@state", (int)launch.State);

            _ = insert.Parameters.AddWithValue("@policy", (int)launch.RecoveryPolicy);

            _ = insert.Parameters.AddWithValue("@version", launch.CheckpointVersion);

            _ = insert.Parameters.AddWithValue(
                "@reference",
                (object?)launch.CheckpointReference ?? DBNull.Value);

            _ = insert.Parameters.AddWithValue(
                "@payload",
                (object?)launch.CheckpointPayload ?? DBNull.Value);

            _ = insert.Parameters.AddWithValue("@created", launch.CreatedAt.ToString("O"));

            _ = insert.Parameters.AddWithValue("@revision", launch.Revision);

            _ = await insert.ExecuteNonQueryAsync();
        }

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> issued =
            await new CovenantErasureStartupRecoveryOwnerAdopter(gate)
                .AdoptBeforeReadinessAsync(connection, CancellationToken.None);

        if (issued.IsFailure || issued.Value is null)
        {
            throw new InvalidOperationException(
                issued.IsFailure
                    ? issued.Error.Message
                    : "The production launch-gap adopter issued no owner.");
        }

        return issued.Value;
    }
}
