using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The three seams a pre-bootstrap transition recovery reaches, recorded in the order it reached them.
/// </summary>
/// <remarks>
/// One double for all three, because what is under test is the order and the short-circuits rather
/// than any one collaborator: three separate doubles would each be right while the sequence between
/// them went unobserved. The catalog it hands back wraps a real in-memory connection, so "the probe
/// was closed before the handler ran" is a fact the test reads off the handle rather than a flag the
/// double sets for itself.
/// </remarks>
internal sealed class RecordingRecoveryDispatchSeam(
    List<string> steps,
    string? failAt,
    LongRunningOperationSettlementOutcome settlement,
    CovenantExclusiveOperation exclusiveOperation = CovenantExclusiveOperation.CovenantReset)
    : IGrimoireRecoveryOnlyUnlock,
        ICovenantRecoveryAuthorityBootstrapper,
        ICovenantClosedRecoveryHandoff,
        IGrimoireOfflineTransitionHandlerDispatch
{

    private SqliteConnection? _connection;

    internal bool Disposed =>
        _connection is not null && _connection.State != ConnectionState.Open;

    public Guid OperationId { get; } = Guid.Parse("11111111-1111-4111-8111-111111111111");

    public CovenantExclusiveRecoveryOwner Owner => new(
        OperationId,
        exclusiveOperation,
        new CovenantDigest(Convert.FromHexString(new string('a', 64))));

    public LongRunningOperationRecoveryFingerprint ExpectedOperation => new(
        OperationId,
        exclusiveOperation is CovenantExclusiveOperation.CovenantReset
            ? LongRunningOperationKinds.DataRetentionMutation
            : LongRunningOperationKinds.DataRetentionFactoryReset,
        CheckpointVersion: exclusiveOperation is CovenantExclusiveOperation.CovenantReset ? 4 : 2,
        Revision: 7);

    internal LongRunningRecoveryOwnerEvidence? CapturedOwnerEvidence { get; private set; }

    public async Task<Result<GrimoireRecoveryUnlockedCatalog>> OpenExistingAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        string databasePath,
        CancellationToken cancellationToken)
    {

        steps.Add("unlock");

        if (failAt == "unlock")
        {

            return Result<GrimoireRecoveryUnlockedCatalog>.Failure(Refusal);

        }

        _connection = new SqliteConnection("Data Source=:memory:");

        // The close is the production catalog's to perform, so it is observed rather than simulated.
        // A double that recorded the step itself would pass a recovery pass that never closed anything.
        _connection.StateChange += (_, change) =>
        {

            if (change.CurrentState == ConnectionState.Closed)
            {

                steps.Add("close");

            }

        };

        await _connection.OpenAsync(cancellationToken);

        return new GrimoireRecoveryUnlockedCatalog(_connection);

    }

    public Task<Result<ICovenantClosedRecoveryHandoff>> LoadAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection recoveryConnection,
        GrimoireOfflineTransitionRecoveryEvidence evidence,
        CancellationToken cancellationToken)
    {

        steps.Add("load");

        Assert.Equal(ConnectionState.Open, recoveryConnection.State);

        return Task.FromResult(
            failAt == "load"
                ? Result<ICovenantClosedRecoveryHandoff>.Failure(Refusal)
                : Result<ICovenantClosedRecoveryHandoff>.Success(this));

    }

    public Task<Result> ConsumeAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        GrimoireOfflineTransitionRecoveryEvidence evidence,
        SqliteConnection recoveryConnection,
        CancellationToken cancellationToken)
    {

        steps.Add("consume");

        Assert.Equal(ConnectionState.Open, recoveryConnection.State);

        return Task.FromResult(
            failAt == "consume" ? Result.Failure(Refusal) : Result.Success());

    }

    public Task<Result<LongRunningOperationSettlementOutcome>> DispatchAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        LongRunningRecoveryOwnerEvidence ownerEvidence,
        CancellationToken cancellationToken)
    {

        // Recorded before the assertion so a probe still open at dispatch time fails as a wrong order
        // rather than as a missing step.
        steps.Add("dispatch");

        Assert.True(Disposed);

        Assert.Equal(OperationId, ownerEvidence.ExpectedOperation.OperationId);

        CapturedOwnerEvidence = ownerEvidence;

        return Task.FromResult(
            failAt == "dispatch"
                ? Result<LongRunningOperationSettlementOutcome>.Failure(Refusal)
                : Result<LongRunningOperationSettlementOutcome>.Success(settlement));

    }

    private static Error Refusal =>
        new(ErrorCodes.Covenant.ManualRecoveryRequired, "Recording seam refusal.");

}
