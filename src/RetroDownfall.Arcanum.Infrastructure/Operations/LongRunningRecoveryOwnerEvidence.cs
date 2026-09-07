using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

internal readonly record struct LongRunningOperationRecoveryFingerprint(
    Guid OperationId,
    string Kind,
    int CheckpointVersion,
    long Revision);

internal abstract class LongRunningRecoveryOwnerEvidence
{
    private protected LongRunningRecoveryOwnerEvidence(
        CovenantExclusiveRecoveryOwner owner,
        LongRunningOperationRecoveryFingerprint expectedOperation)
    {
        Owner = owner;

        ExpectedOperation = expectedOperation;
    }

    internal CovenantExclusiveRecoveryOwner Owner { get; }

    internal LongRunningOperationRecoveryFingerprint ExpectedOperation { get; }
}
