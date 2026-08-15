using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Builders for mutation intents, plus the caller-owned immediate transaction the kernel runs inside.
/// </summary>
internal static class CovenantMutationFixture
{

    internal static readonly DateTimeOffset CommitTime = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    internal static readonly CovenantCompiler Compiler = new();

    internal static CovenantMutationTarget Target(
        CovenantOperationScope scope,
        string key,
        CovenantLane lane) =>
        new(scope, new CovenantKey(key), key, lane, CovenantOperationGateFixture.Digest(31));

    internal static CovenantMutationAuthorization Authorization(byte seed) =>
        new(
            CovenantOperationGateFixture.Digest(seed),
            CovenantOperationGateFixture.Digest((byte)(seed + 1)),
            CovenantOperationGateFixture.Digest((byte)(seed + 2)),
            CovenantOperationGateFixture.Digest((byte)(seed + 3)),
            CovenantAuthorizationMode.ApiMasterKey,
            WardReceiptDigest: null,
            PreflightBodyDigest: null);

    internal static CovenantMutationArtifact Artifact(string key, string authored)
    {

        CovenantCompiledContent compiled = Compiler.Compile(key, authored);

        return new CovenantMutationArtifact(
            compiled.AuthoredContent,
            compiled.Fragment,
            compiled.AuthoredHash,
            compiled.FragmentHash,
            compiled.FragmentUtf8ByteCount,
            compiled.RequiredFenceLength,
            compiled.CompilerPolicyVersion,
            compiled.RendererPolicyVersion);

    }

    internal static CovenantMutationIntent OperatorSet(
        CovenantOperationScope scope,
        string key,
        string authored,
        long expectedRevision,
        long expectedKeyEpoch,
        Guid? mutationId = null,
        bool reactivate = false,
        byte authorizationSeed = 40) =>
        new(
            mutationId ?? Guid.NewGuid(),
            CovenantMutationKind.OperatorSet,
            CovenantOperation.Set,
            CovenantOrigin.Operator,
            Target(scope, key, CovenantLane.Confirmed),
            expectedRevision,
            reactivate,
            expectedKeyEpoch,
            Artifact(key, authored),
            [],
            Authorization(authorizationSeed),
            sourceTurnId: null,
            sourceToolCallId: null,
            basePlanDigest: null,
            admissionReceiptDigest: null);

    internal static CovenantMutationIntent OperatorRetire(
        CovenantOperationScope scope,
        string key,
        CovenantLane lane,
        long expectedRevision,
        long expectedKeyEpoch,
        Guid? mutationId = null,
        byte authorizationSeed = 50) =>
        new(
            mutationId ?? Guid.NewGuid(),
            CovenantMutationKind.OperatorRetire,
            CovenantOperation.Retire,
            CovenantOrigin.Operator,
            Target(scope, key, lane),
            expectedRevision,
            reactivate: false,
            expectedKeyEpoch,
            artifact: null,
            [],
            Authorization(authorizationSeed),
            sourceTurnId: null,
            sourceToolCallId: null,
            basePlanDigest: null,
            admissionReceiptDigest: null);

    internal static CovenantMutationIntent AgentPropose(
        Guid campaignId,
        string key,
        string authored,
        long expectedRevision,
        long expectedKeyEpoch,
        Guid? mutationId = null,
        ImmutableArray<CovenantMutationProvenanceLeaf>? provenance = null,
        byte authorizationSeed = 60) =>
        new(
            mutationId ?? Guid.NewGuid(),
            CovenantMutationKind.AgentPropose,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            Target(CovenantOperationScope.ForCampaign(campaignId), key, CovenantLane.Proposed),
            expectedRevision,
            reactivate: false,
            expectedKeyEpoch,
            Artifact(key, authored),
            provenance ?? [],
            Authorization(authorizationSeed),
            sourceTurnId: Guid.NewGuid(),
            sourceToolCallId: "call-1",
            basePlanDigest: CovenantOperationGateFixture.Digest(70),
            admissionReceiptDigest: CovenantOperationGateFixture.Digest(71));

    internal static CovenantMutationBatch Batch(
        Guid datasetGeneration,
        params CovenantMutationIntent[] intents) =>
        new(datasetGeneration, 1, 1, CommitTime, [.. intents]);

    /// <summary>
    /// Builds a batch bound to the fixture's live key-reclamation and Campaign-registry epochs.
    /// </summary>
    /// <remarks>
    /// Registering a Campaign advances the registry epoch, so a test that seeds Campaigns and then
    /// mutates has to bind the epoch it will actually meet. Hard-coding one would make every such
    /// test fail as a stale snapshot for a reason that has nothing to do with what it is testing.
    /// </remarks>
    internal static async Task<CovenantMutationBatch> LiveBatchAsync(
        CovenantCanonicalFixture fixture,
        CancellationToken cancellationToken,
        params CovenantMutationIntent[] intents) =>
        new(
            await fixture.ReadDatasetGenerationAsync(cancellationToken),
            await ScalarAsync(fixture, "SELECT KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;", cancellationToken),
            await ScalarAsync(fixture, "SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;", cancellationToken),
            CommitTime,
            [.. intents]);

    private static async Task<long> ScalarAsync(
        CovenantCanonicalFixture fixture,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

    }

    /// <summary>
    /// Opens the immediate write transaction the caller owns, runs the batch, and commits when the
    /// kernel succeeds. The kernel itself never opens, commits, or retries a transaction.
    /// </summary>
    internal static async Task<Result<IReadOnlyList<CovenantMutationReceipt>>> ApplyAsync(
        CovenantCanonicalFixture fixture,
        CovenantMutationBatch batch,
        CancellationToken cancellationToken,
        bool commit = true)
    {

        await using SqliteTransaction transaction = (SqliteTransaction)await fixture.Connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        CovenantMutationTransaction owned = new(fixture.Connection, transaction);

        Result<IReadOnlyList<CovenantMutationReceipt>> receipts =
            await new CovenantMutationKernel().ApplyBatchAsync(batch, owned, cancellationToken);

        if (receipts.IsSuccess && commit)
        {

            await transaction.CommitAsync(cancellationToken);

        }
        else
        {

            await transaction.RollbackAsync(cancellationToken);

        }

        return receipts;

    }

}
