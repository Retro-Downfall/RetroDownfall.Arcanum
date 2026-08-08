using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Infrastructure.Diagnostics;

namespace RetroDownfall.Arcanum.Cli.Diagnostics;

/// <summary>
/// The doctor catalog. Registration order is report order, and it runs cheapest-and-most-fundamental
/// first: an operator reading top-to-bottom sees "the directories are missing" before "the database
/// will not open", which is the order the causes actually run in.
///
/// <para>Adding a diagnostic is a registration, not an edit to a switchboard — and
/// <see cref="DoctorDiagnosticRunner"/> validates the resulting catalog at construction, so a
/// duplicate id, a mis-namespaced id, or a repair whose detector was never registered fails loudly
/// on the first <c>arcanum doctor</c> rather than silently omitting a subsystem.</para>
/// </summary>
public static class DoctorDiagnosticsRegistration
{

    public static IServiceCollection AddArcanumDoctorDiagnostics(this IServiceCollection services)
    {

        ArgumentNullException.ThrowIfNull(services);

        // Filesystem and configuration: no I/O beyond stat, and no dependency on the database or host.
        services.AddTransient<IDoctorCheck, ManagedDirectoriesCheck>();

        services.AddTransient<IDoctorCheck, PermissionPostureCheck>();

        services.AddTransient<IDoctorCheck, ConfigurationSemanticsCheck>();

        services.AddTransient<IDoctorCheck, ConfigurationEnvironmentOverrideCheck>();

        // Credentials: presence and store availability only; no value is ever read into a finding.
        services.AddTransient<IDoctorCheck, CredentialStoreAvailabilityCheck>();

        services.AddTransient<IDoctorCheck, DataProtectionKeyRingCheck>();

        services.AddTransient<IDoctorCheck, WebResearchCredentialCheck>();

        // Persistence. The write-ahead log is measured FIRST and deliberately: SQLite materializes
        // the -wal and -shm sidecars of a WAL database on any open, including a read-only one, so a
        // check that ran after the integrity probes would be measuring a log this command had just
        // created rather than the one the last host shutdown left behind.
        services.AddTransient<IDoctorCheck, GrimoireWalCheck>();

        services.AddTransient<IDoctorCheck, GrimoireKeyMaterialCheck>();

        services.AddTransient<IDoctorCheck, GrimoireIntegrityCheck>();

        services.AddTransient<IDoctorCheck, GrimoireForeignKeyCheck>();

        // Durable work, read locally so a crashed host does not hide it.
        services.AddTransient<IDoctorCheck, DurableOperationRepairCheck>();

        services.AddTransient<IDoctorCheck, StaleOperationLeaseCheck>();

        // Runtime residue and capacity.
        services.AddTransient<IDoctorCheck, PidFileCheck>();

        services.AddTransient<IDoctorCheck, MaintenanceLockCheck>();

        services.AddTransient<IDoctorCheck, DiskSpaceCheck>();

        // Outward-facing last: the host relay, then the one network-gated probe.
        services.AddTransient<IDoctorCheck, HostHealthComponentsCheck>();

        services.AddTransient<IDoctorCheck, ProviderReachabilityCheck>();

        services.AddTransient<IDoctorRepair, PermissionApplyOwnerOnlyRepair>();

        services.AddTransient<IDoctorRepair, ManagedDirectoriesRepair>();

        services.AddTransient<IDoctorRepair, RemoveStalePidRepair>();

        services.AddTransient<DoctorDiagnosticRunner>();

        return services;

    }

}
