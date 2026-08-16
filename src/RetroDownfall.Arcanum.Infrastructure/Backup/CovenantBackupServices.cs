using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The capabilities a physical backup runs under while Covenant is enabled.
/// </summary>
/// <remarks>
/// Grouped and optional rather than separate constructor parameters, because with the gate off there
/// is nothing to lease and nothing to disclose. A backup that demanded these anyway would refuse on
/// installations that never enabled the feature, and one that took them piecemeal could end up with
/// a disclosure boundary but no lease — which is a backup that accounts for a read it never fenced.
/// </remarks>
internal sealed record CovenantBackupServices(
    ICovenantOperationGate Gate,
    ICovenantBackupDisclosureBoundary Boundary);
