using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Pins that each Covenant maintenance sweep is reached by something that runs.
/// </summary>
/// <remarks>
/// All three sweeps were registered in the container, covered by their own suites, and called by
/// nothing under <c>src</c> for the whole of this feature's life. Every one of those suites passed the
/// entire time, because a suite that composes a worker itself proves the algorithm and says nothing
/// about whether the algorithm ever runs. The consequences were real and silent: owner deletions were
/// journalled for the encrypted tier and never applied, the canonical outbox only ever grew, and turn
/// receipts accumulated against a per-Session ceiling nothing could fold them below.
///
/// <para>An inventory assertion rather than a behaviour test, because the failure being prevented is a
/// missing call site rather than a wrong result — the same reason the unsupplied-parameter and wrapper
/// entry-point rules are inventory assertions. What each sweep does under its lease is proven by
/// <c>CovenantMaintenanceCoordinatorTests</c> and by the suites the workers already had.</para>
/// </remarks>
public sealed class CovenantMaintenanceWiringTests
{

    /// <summary>
    /// Each sweep's entry point, and the file that declares it.
    /// </summary>
    /// <remarks>
    /// The declaring file is excluded from the search because a method's own declaration names it, and
    /// counting that would let a sweep satisfy this rule by existing.
    /// </remarks>
    public static TheoryData<string, string> Sweeps() => new()
    {
        { "RunBatchAsync", "CovenantCleanupWorker.cs" },
        { "SynchronizeAsync", "CovenantSearchOutboxWorker.cs" },
        { "FoldAsync", "CovenantTurnReceiptCompactor.cs" },
    };

    [Theory]
    [MemberData(nameof(Sweeps))]
    public void Every_maintenance_sweep_is_called_by_production_code(string entryPoint, string declaringFile)
    {

        string[] callers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source => !source.Is(declaringFile))
                .Where(source => source.Names($".{entryPoint}("))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            callers.Length > 0,
            $"{entryPoint} is declared in {declaringFile} and called by nothing under src, so the sweep "
            + "cannot run however green its own suite is.");

    }

    [Fact]
    public void The_maintenance_driver_is_registered_on_the_long_running_host()
    {

        string composition = ProductionSourceInventory.Sources()
            .Single(static source => source.Is("ServiceCollectionExtensions.cs"))
            .Text;

        // Reset-aware rather than plain: a pass must not open a transaction against a dataset the
        // installation is replacing, and the plain registration would start it before recovery settles.
        Assert.Contains(
            "AddInstallationResetRecoveryAwareHostedService<CovenantMaintenanceHostedService>",
            composition,
            StringComparison.Ordinal);

        // The coordinators sit with the persistence composition rather than the host one, so the CLI
        // resolves them too; only the driver is server-only.
        Assert.Contains("new CovenantOwnerCleanupCoordinator(", composition, StringComparison.Ordinal);

        Assert.Contains("new CovenantSearchOutboxCoordinator(", composition, StringComparison.Ordinal);

        Assert.Contains("new CovenantTurnReceiptCompactionCoordinator(", composition, StringComparison.Ordinal);

    }

}
