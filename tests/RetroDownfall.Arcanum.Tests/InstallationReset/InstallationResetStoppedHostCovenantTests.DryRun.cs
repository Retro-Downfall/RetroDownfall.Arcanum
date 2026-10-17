using System.Text.Json;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Tests.Cli;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The dry run of a global reset beside an offline-transition journal that recovery would have to
/// repair: it reports what recovery would find and repairs nothing.
/// </summary>
public sealed partial class InstallationResetStoppedHostCovenantTests
{
    /// <summary>A shape the journal slot can be left in that recovery converges.</summary>
    public enum JournalNeedingConvergence
    {
        /// <summary>The anchor is closed and the retired journal's last file is still on disk.</summary>
        ClosedAnchorWithLeftoverRetiringFile = 1,

        /// <summary>The anchor is active and the canonical file is one revision ahead of it.</summary>
        ActiveAnchorWithCanonicalOneRevisionAhead = 2,

        /// <summary>The anchor is active, the canonical file is one ahead, and the predecessor is a working file.</summary>
        ActiveAnchorWithWorkingSibling = 3,
    }

    public static TheoryData<bool, JournalNeedingConvergence> JournalNeedingConvergenceCases()
    {
        TheoryData<bool, JournalNeedingConvergence> cases = [];

        foreach (bool feature in new[] { false, true })
        {
            foreach (JournalNeedingConvergence shape in Enum.GetValues<JournalNeedingConvergence>())
            {
                cases.Add(feature, shape);
            }
        }

        return cases;
    }

    /// <summary>
    /// A dry run changes nothing, and that includes the repairs recovery would make: it names the
    /// transition that is still active as a blocker and leaves every file and credential as it found
    /// them.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(JournalNeedingConvergenceCases))]
    public async Task A_global_dry_run_beside_a_journal_recovery_would_repair_repairs_nothing(
        bool covenantFeature,
        JournalNeedingConvergence shape)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        await LeaveTheJournalSlotAsync(covenantFeature, shape);

        Dictionary<string, string> filesBefore = CaptureStableFiles();

        Dictionary<string, string> credentialsBefore = StoppedHostResetTestSupport.CredentialDigests(_credentials);

        CliTestResult result = await RunAsync(
            covenantFeature,
            "--json",
            "data",
            "factory-reset",
            "--global",
            "--dry-run");

        string described = $"exit {result.ExitCode}\nstdout: {result.Output}\nstderr: {result.Error}";

        Assert.True(result.ExitCode == (int)CliExitCode.Success, described);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        string[] blockerCodes =
        [
            .. document.RootElement.GetProperty("blockers")
                .EnumerateArray()
                .Select(static blocker => blocker.GetProperty("code").GetString()!),
        ];

        // Recovery would resume an active transition, so the plan lists it; it would only tidy the
        // files of a retired one, so the plan does not.
        Assert.True(
            (shape is not JournalNeedingConvergence.ClosedAnchorWithLeftoverRetiringFile)
                == blockerCodes.Contains(ErrorCodes.Covenant.LifecycleConflict),
            described);

        AssertUnchanged(filesBefore);

        Assert.Equal(credentialsBefore, StoppedHostResetTestSupport.CredentialDigests(_credentials));

        AssertNoSidecars();

        AssertNoActiveResetOrJournal();
    }

    /// <summary>
    /// Leaves a standalone Covenant reset journal in the slot, in a shape that only recovery would
    /// change, over the installation the prior Grimoire is.
    /// </summary>
    private async Task LeaveTheJournalSlotAsync(bool covenantFeature, JournalNeedingConvergence shape)
    {
        Guid installation = await ReadTheInstallationIdentityAsync();

        await using ServiceProvider provider = Compose(covenantFeature).BuildServiceProvider();

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock held = acquired.BorrowAcquiredLock();

        GrimoireOfflineTransitionJournalLocation location = new GrimoireOfflineTransitionJournalFileStore()
            .ResolveLocation(ArcanumPaths.GrimoireDirectory)
            .Value;

        Assert.Equal(
            installation,
            new BackupRestoreJournalInstallationIdentityProvider(_credentials)
                .SeedFromDatabase(held, ArcanumPaths.GrimoireDirectory, location.ProfileNamespace, installation)
                .Value);

        GrimoireOfflineTransitionLifecycleStore lifecycle = provider.GetRequiredService<GrimoireOfflineTransitionLifecycleStore>();

        CovenantResetOfflineTransitionPayloadV1 prepared = PreparedCovenantResetPayload();

        GrimoireOfflineTransitionTypedPublication begun = (await lifecycle.BeginBoundAsync(
            held,
            ArcanumPaths.GrimoireDirectory,
            installation,
            prepared.Binding.OperationId,
            GrimoireOfflineTransitionKind.CovenantReset,
            payloadVersion: 1,
            slotEpoch => Result<IGrimoireOfflineTransitionPayload>.Success(
                prepared with { Binding = prepared.Binding with { SlotEpoch = slotEpoch } }),
            CancellationToken.None)).Value;

        if (shape is JournalNeedingConvergence.ClosedAnchorWithLeftoverRetiringFile)
        {
            GrimoireOfflineTransitionJournalStore failing = new(
                _credentials,
                new GrimoireOfflineTransitionJournalFileStore(failBeforeStep: step => step == "file:retiring-unlinked"),
                new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

            Assert.True((await failing.RetireAsync(held, begun.Raw, CancellationToken.None)).IsFailure);

            Assert.True(File.Exists(location.RetiringPath));

            return;
        }

        GrimoireOfflineTransitionLifecycleStore interrupted = new(
            new GrimoireOfflineTransitionJournalStore(
                _credentials,
                new GrimoireOfflineTransitionJournalFileStore(failBeforeStep: step => step == "file:permissions-verified"),
                new GrimoireOfflineTransitionJournalAnchorStore(_credentials)),
            GrimoireOfflineTransitionHandlerRegistry.Production);

        CovenantResetOfflineTransitionPayloadV1 closing = begun.Payload as CovenantResetOfflineTransitionPayloadV1
            ?? throw new Xunit.Sdk.XunitException("The opened journal is not a Covenant reset.");

        Assert.True((await interrupted.AdvanceAsync(
            held,
            begun,
            closing with { Lifecycle = closing.Lifecycle with { State = GrimoireOfflineTransitionState.Closing } },
            CancellationToken.None)).IsFailure);

        if (shape is JournalNeedingConvergence.ActiveAnchorWithWorkingSibling)
        {
            File.Move(location.PreviousPath, location.WorkingPath);
        }
    }

    private async Task<Guid> ReadTheInstallationIdentityAsync()
    {
        await using ArcanumDbContext context = _fixture.CreateContext(ArcanumPaths.GrimoireDatabaseFile);

        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT InstallationIdentity FROM covenant_authority_state WHERE StateKey = 1;";

        return Guid.Parse((string)(await command.ExecuteScalarAsync(CancellationToken.None))!);
    }

    private static CovenantResetOfflineTransitionPayloadV1 PreparedCovenantResetPayload() => new(
        new(
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            GrimoireOfflineTransitionKind.CovenantReset,
            PayloadVersion: 1,
            SlotEpoch: 1,
            Digest(0x11),
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
            new(1, 2, 3),
            new(2, 3, 4),
            Digest(0x12),
            ExpectedDatabaseOperationRevision: 4,
            ParentReceiptBindingDigest: null),
        new(
            GrimoireOfflineTransitionState.Prepared,
            GrimoireOfflineTransitionTerminalIntent.Undecided,
            new(false, false, false, false, false, null),
            new(false, false, false),
            ReconciliationEvidence: null,
            Blocker: null),
        CovenantResetPhase.InventoryPrepared,
        InFlightPhase: null,
        InFlightBeforeState: null,
        ReplacementEvidence: null);

    private static CovenantDigest Digest(byte value) => new(Enumerable.Repeat(value, 32).ToArray());
}
