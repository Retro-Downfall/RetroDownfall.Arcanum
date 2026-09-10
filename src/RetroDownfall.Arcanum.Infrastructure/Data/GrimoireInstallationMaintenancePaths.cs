using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Where this installation's maintenance purposes point, and the only production answer.
/// </summary>
/// <remarks>
/// A type rather than two literals inside the gate, because the gate is the one component that must
/// never be told which file to open and the one that a suite most needs to point somewhere else. An
/// installation-path read inlined there would make every test that drives a real erasure operate on
/// the developer's own Grimoire and then prove the bytes were gone.
///
/// <para>Both paths are computed rather than stored, so a process whose testing home is established
/// after this instance is constructed still resolves correctly. The staging path is derived from the
/// canonical one by the same helper the residual-artifact classifier uses, so the file the gate hands
/// out and the file the absence proof sweeps for cannot drift apart.</para>
/// </remarks>
internal sealed class GrimoireInstallationMaintenancePaths : IGrimoireMaintenancePathAuthority
{

    /// <summary>The one production instance, held because it carries no state to vary.</summary>
    internal static GrimoireInstallationMaintenancePaths Instance { get; } = new();

    public string CanonicalDatabasePath => ArcanumPaths.GrimoireDatabaseFile;

    public string ExportStagingDatabasePath(Guid operationId) =>
        CovenantResidualArtifacts.ExportStagingPath(CanonicalDatabasePath, operationId);

}
