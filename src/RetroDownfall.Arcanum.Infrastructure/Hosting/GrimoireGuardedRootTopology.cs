using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal static class GrimoireGuardedRootTopology
{

    internal static void EnsureOwnedRootIsSafe(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedRoot)
    {

        heldInstallationLock.AssertHeldFor(guardedRoot);

        Result<NoFollowPathTopologyKind> classified =
            NoFollowPathTopology.Classify(guardedRoot);

        if (classified.IsFailure
            || classified.Value is NoFollowPathTopologyKind.RegularFile)
        {

            throw UnsafeTopology();

        }

    }

    internal static void EnsureCoexistingRootIsOrdinaryDirectory(
        string guardedRoot)
    {

        Result<NoFollowPathTopologyKind> classified =
            NoFollowPathTopology.Classify(guardedRoot);

        if (classified.IsFailure
            || classified.Value is not NoFollowPathTopologyKind.Directory)
        {

            throw UnsafeTopology();

        }

    }

    private static InvalidOperationException UnsafeTopology() =>
        new("The guarded installation root topology could not be validated safely.");

}
