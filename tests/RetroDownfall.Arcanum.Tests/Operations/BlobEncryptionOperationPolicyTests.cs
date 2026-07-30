using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed class BlobEncryptionOperationPolicyTests
{
    [Fact]
    public void Migration_and_rotation_are_registered_as_restartable_durable_operations()
    {
        Assert.True(LongRunningOperationPolicyCatalog.IsRegistered(
            LongRunningOperationKinds.BlobEncryptionMigration,
            LongRunningOperationRecoveryPolicy.RestartIdempotently));
        Assert.True(LongRunningOperationPolicyCatalog.IsRegistered(
            LongRunningOperationKinds.BlobEncryptionKeyRotation,
            LongRunningOperationRecoveryPolicy.RestartIdempotently));
    }
}
