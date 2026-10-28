using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Tests.Cli;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The refusals and the blockers of a stopped-host global reset: what it will not measure, and what a
/// stopped host cannot drain, each answered before the daemon is touched and before any record exists.
/// </summary>
public sealed partial class InstallationResetStoppedHostCovenantTests
{
    private const string HostToolsVariable = "ARCANUM_ALLOW_HOST_PROCESS_TOOLS";

    private const string EditionVariable = "ARCANUM_EDITION";

    private const string Now = "2026-10-16T00:00:00.0000000Z";

    private const string Later = "2099-01-01T00:00:00.0000000Z";

    /// <summary>Why a Covenant inventory cannot be measured, one for each way the measurement can fail.</summary>
    public enum UnmeasurableInventory
    {
        MasterKeyAbsent = 1,

        CatalogObjectTampered = 2,

        StateRowDeleted = 3,

        CredentialStoreThrows = 4,

        HostToolsEscapeHatchArmed = 5,

        ExposureReaderFailsAfterAdmission = 6,
    }

    /// <summary>Something a stopped host cannot drain, and so cannot erase around.</summary>
    public enum UndrainableWork
    {
        ActiveOperation = 1,

        BudgetReservation = 2,

        InferenceRun = 3,

        IdempotencyLease = 4,

        Batch = 5,

        /// <summary>A parked Long Rest sweep: a host start leaves it parked, because it waits for an operator.</summary>
        ParkedLongRestSweep = 6,

        /// <summary>A Long Rest sweep request that was queued and never started: no host start drains it.</summary>
        QueuedLongRestSweep = 7,

        /// <summary>A retention operation whose attention a host start does recover.</summary>
        RecoveredAttention = 8,
    }

    public static TheoryData<bool, UnmeasurableInventory, bool> UnmeasurableInventoryCases()
    {
        TheoryData<bool, UnmeasurableInventory, bool> cases = [];

        foreach (bool feature in new[] { false, true })
        {
            foreach (UnmeasurableInventory kind in Enum.GetValues<UnmeasurableInventory>())
            {
                foreach (bool apply in new[] { false, true })
                {
                    cases.Add(feature, kind, apply);
                }
            }
        }

        return cases;
    }

    public static TheoryData<bool, UndrainableWork> UndrainableWorkCases()
    {
        TheoryData<bool, UndrainableWork> cases = [];

        foreach (bool feature in new[] { false, true })
        {
            foreach (UndrainableWork kind in Enum.GetValues<UndrainableWork>())
            {
                cases.Add(feature, kind);
            }
        }

        return cases;
    }

    /// <summary>
    /// An inventory that genuinely cannot be measured is refused whole, by its own cause, before the
    /// daemon is touched and before any reset record exists.
    /// </summary>
    /// <remarks>
    /// Every case is identified by its code and by its own message, because a refusal that merely
    /// shares a code must not be able to satisfy a case about a different one.
    /// </remarks>
    [SkippableTheory]
    [MemberData(nameof(UnmeasurableInventoryCases))]
    public async Task The_global_reset_refuses_an_unmeasurable_inventory_before_any_effect(
        bool covenantFeature,
        UnmeasurableInventory kind,
        bool apply)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(
            covenantFeature,
            saveMasterKey: kind is not UnmeasurableInventory.MasterKeyAbsent);

        (string Code, string Message) expected = kind switch
        {
            UnmeasurableInventory.MasterKeyAbsent =>
                (ErrorCodes.Covenant.OperatorAuthorityUnavailable, "The master API key is not usable"),
            UnmeasurableInventory.CatalogObjectTampered =>
                (ErrorCodes.Covenant.IntegrityFailure, "The Covenant catalog could not be proved intact"),
            UnmeasurableInventory.StateRowDeleted =>
                (ErrorCodes.Data.InventoryUnavailable, "The Grimoire holds no Covenant state row"),
            UnmeasurableInventory.CredentialStoreThrows =>
                (ErrorCodes.Covenant.Unavailable, "The master API key could not be read from the operating-system credential store"),
            UnmeasurableInventory.HostToolsEscapeHatchArmed =>
                (ErrorCodes.Covenant.HostToolsTransitionRequired, "The host-process-tools state is not a settled clean pair."),
            _ =>
                (ErrorCodes.Data.InventoryUnavailable, "The local Covenant inventory is unavailable."),
        };

        switch (kind)
        {
            case UnmeasurableInventory.CatalogObjectTampered:

                _ = await StoppedHostResetTestSupport.ExecuteAsync("DROP INDEX idx_covenant_entries_campaign;");

                break;

            case UnmeasurableInventory.StateRowDeleted:

                _ = await StoppedHostResetTestSupport.ExecuteAsync("DELETE FROM covenant_state;");

                break;
        }

        Action<IServiceCollection> configure = kind switch
        {
            UnmeasurableInventory.CredentialStoreThrows => StoppedHostResetTestSupport.MasterKeyReadThrows,
            UnmeasurableInventory.ExposureReaderFailsAfterAdmission => static services =>
                StoppedHostResetTestSupport.InterceptDataService(
                    services,
                    static inner => new StoppedHostResetTestSupport.InterceptingStoppedHostDataService(inner)
                    {
                        // A plan that was admitted and built, and then lost the exposure the reader
                        // folds into it: it succeeds and carries no inventory.
                        MapPlan = static plan => plan.IsSuccess
                            ? Result<DataRetentionPlan>.Success(plan.Value with { Covenant = null })
                            : plan,
                    }),
            _ => static _ => { }
            ,
        };

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        Dictionary<string, string> before = CaptureStableFiles();

        Dictionary<string, string> credentialsBefore = StoppedHostResetTestSupport.CredentialDigests(_credentials);

        string? previousHostTools = global::System.Environment.GetEnvironmentVariable(HostToolsVariable);

        string? previousEdition = global::System.Environment.GetEnvironmentVariable(EditionVariable);

        CliTestResult result;

        try
        {
            if (kind is UnmeasurableInventory.HostToolsEscapeHatchArmed)
            {
                // The escape hatch is armed only in the Development edition, and on an installation
                // with no completed host-process-tools transition that is a state the startup gate
                // refuses to run Covenant beside.
                global::System.Environment.SetEnvironmentVariable(EditionVariable, "Development");

                global::System.Environment.SetEnvironmentVariable(HostToolsVariable, "1");
            }

            result = await RunAsync(covenantFeature, configure, GlobalArguments(apply));
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(HostToolsVariable, previousHostTools);

            global::System.Environment.SetEnvironmentVariable(EditionVariable, previousEdition);
        }

        AssertRefusedWithoutEffect(result, before, expected.Code, expected.Message);

        Assert.Equal(0, _mutations.Calls);

        Assert.Equal(credentialsBefore, StoppedHostResetTestSupport.CredentialDigests(_credentials));
    }

    /// <summary>
    /// Work a stopped host cannot drain is a blocker the dry-run lists and the apply refuses, before
    /// the point of no return, instead of a failure after the daemon has been uninstalled.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(UndrainableWorkCases))]
    public async Task Undrainable_work_is_a_listed_blocker_and_the_apply_is_refused_before_anything_changes(
        bool covenantFeature,
        UndrainableWork kind)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        string id = Guid.NewGuid().ToString("N");

        (string Code, string Remedy) expected = kind switch
        {
            UndrainableWork.ActiveOperation => ("Data.ActiveOperation", InstallationResetService.StartHostOnceRemedy),
            UndrainableWork.BudgetReservation => ("Data.BudgetReservationOutstanding", InstallationResetService.InterruptedWorkRemedy),
            UndrainableWork.InferenceRun => ("Data.InferenceRunActive", InstallationResetService.InterruptedWorkRemedy),
            UndrainableWork.IdempotencyLease => ("Data.IdempotencyLeaseActive", InstallationResetService.LeaseExpiryRemedy),
            UndrainableWork.ParkedLongRestSweep or UndrainableWork.QueuedLongRestSweep =>
                (ErrorCodes.Data.OperationNeedsAttention, InstallationResetService.OperatorSettlesRemedy),
            UndrainableWork.RecoveredAttention => ("Data.ActiveOperation", InstallationResetService.StartHostOnceRemedy),
            _ => ("Data.BatchInProgress", InstallationResetService.StartHostOnceRemedy),
        };

        _ = kind switch
        {
            UndrainableWork.ActiveOperation => await StoppedHostResetTestSupport.ExecuteAsync(
                """
                INSERT INTO "LongRunningOperations"
                    ("Id", "Kind", "State", "RecoveryPolicy", "CreatedAt", "PublicSummary", "AttemptCount", "CheckpointVersion", "Revision")
                VALUES ($id, 'subagent', 1, 3, $now, 'left running', 0, 0, 0);
                """,
                ("$id", id),
                ("$now", Now)),
            UndrainableWork.ParkedLongRestSweep => await StoppedHostResetTestSupport.ExecuteAsync(
                """
                INSERT INTO "LongRunningOperations"
                    ("Id", "Kind", "State", "RecoveryPolicy", "CreatedAt", "PublicSummary", "AttemptCount", "CheckpointVersion", "Revision", "TerminalErrorCode")
                VALUES ($id, 'long-rest-sweep', 7, 0, $now, 'parked after a restart', 1, 1, 0, 'long_rest.interrupted');
                """,
                ("$id", id),
                ("$now", Now)),
            UndrainableWork.QueuedLongRestSweep => await StoppedHostResetTestSupport.ExecuteAsync(
                """
                INSERT INTO "LongRunningOperations"
                    ("Id", "Kind", "State", "RecoveryPolicy", "CreatedAt", "PublicSummary", "AttemptCount", "CheckpointVersion", "Revision")
                VALUES ($id, 'long-rest-sweep', 0, 0, $now, 'queued, never started', 0, 0, 0);
                """,
                ("$id", id),
                ("$now", Now)),
            UndrainableWork.RecoveredAttention => await StoppedHostResetTestSupport.ExecuteAsync(
                """
                INSERT INTO "LongRunningOperations"
                    ("Id", "Kind", "State", "RecoveryPolicy", "CreatedAt", "PublicSummary", "AttemptCount", "CheckpointVersion", "Revision", "TerminalErrorCode")
                VALUES ($id, 'data-retention-prune', 7, 1, $now, 'prune awaiting recovery', 1, 0, 0, 'Data.ReconciliationFailed');
                """,
                ("$id", id),
                ("$now", Now)),
            UndrainableWork.BudgetReservation => await StoppedHostResetTestSupport.ExecuteAsync(
                """
                INSERT INTO "BudgetReservations"
                    ("Id", "RunId", "BudgetPeriod", "Status", "ExpiresAt", "CreatedAt", "UpdatedAt")
                VALUES ($id, $run, '2026-10-16', 0, $later, $now, $now);
                """,
                ("$id", id),
                ("$run", Guid.NewGuid().ToString("N")),
                ("$later", Later),
                ("$now", Now)),
            UndrainableWork.InferenceRun => await StoppedHostResetTestSupport.ExecuteAsync(
                """
                INSERT INTO "InferenceRuns"
                    ("Id", "RequestId", "Surface", "Purpose", "StartedAt", "Status")
                VALUES ($id, 'request', 'chat', 'chat', $now, 0);
                """,
                ("$id", id),
                ("$now", Now)),
            UndrainableWork.IdempotencyLease => await StoppedHostResetTestSupport.ExecuteAsync(
                """
                INSERT INTO "IdempotencyClaims"
                    ("Id", "ClaimKeyHash", "FingerprintHash", "State", "OwnerId", "LeaseExpiresAt", "HeartbeatAt", "CreatedAt", "UpdatedAt")
                VALUES ($id, 'claim', 'fingerprint', 1, 'a-process-that-stopped', $later, $now, $now, $now);
                """,
                ("$id", id),
                ("$later", Later),
                ("$now", Now)),
            _ => await StoppedHostResetTestSupport.ExecuteAsync(
                """
                INSERT INTO "Batches" ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt")
                VALUES ($id, $file, '/v1/chat/completions', 'in_progress', $now);
                """,
                ("$id", id),
                ("$file", Guid.NewGuid().ToString("N")),
                ("$now", Now)),
        };

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        Dictionary<string, string> before = CaptureStableFiles();

        CliTestResult dryRun = await RunAsync(covenantFeature, GlobalArguments(apply: false));

        string described = $"exit {dryRun.ExitCode}\nstdout: {dryRun.Output}\nstderr: {dryRun.Error}";

        Assert.True(dryRun.ExitCode == (int)CliExitCode.Success, described);

        using (JsonDocument document = JsonDocument.Parse(dryRun.Output))
        {
            JsonElement blocker = Assert.Single(
                document.RootElement.GetProperty("blockers").EnumerateArray().ToArray(),
                candidate => candidate.GetProperty("code").GetString() == expected.Code);

            // The remedy is the one that is true for this kind of work, and it is spoken, not implied.
            string message = blocker.GetProperty("message").GetString()!;

            if (kind is UndrainableWork.ActiveOperation or UndrainableWork.RecoveredAttention
                or UndrainableWork.ParkedLongRestSweep or UndrainableWork.QueuedLongRestSweep)
            {
                Assert.Contains("operation show " + Guid.Parse(id).ToString("D"), message, StringComparison.Ordinal);

                Assert.Contains("fresh reset plan", message, StringComparison.Ordinal);

                Assert.DoesNotContain("operation retry", message, StringComparison.Ordinal);

                if (kind is UndrainableWork.QueuedLongRestSweep)
                {
                    Assert.Contains("Pending", message, StringComparison.Ordinal);

                    Assert.Contains("operation cancel " + Guid.Parse(id).ToString("D"), message, StringComparison.Ordinal);
                }
            }
            else
            {
                Assert.EndsWith(expected.Remedy, message, StringComparison.Ordinal);
            }
        }

        AssertUnchanged(before);

        CliTestResult apply = await RunAsync(covenantFeature, GlobalArguments(apply: true));

        described = $"exit {apply.ExitCode}\nstdout: {apply.Output}\nstderr: {apply.Error}";

        Assert.True(apply.ExitCode == (int)CliExitCode.GenericError, described);

        // The refusal is the plan's own blocker, spoken before a record exists: not a failure of the
        // nested erasure after the daemon registration has gone.
        Assert.Contains(ErrorCodes.Data.Blocked, apply.Output, StringComparison.Ordinal);

        Assert.Equal(0, _mutations.Calls);

        AssertUnchanged(before);

        AssertNoActiveResetOrJournal();
    }
}
