using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Cli;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// A read that fails once after the point of no return: whichever read it is, the reset answers with
/// the typed recovery refusal and keeps its record, and the next clean resume finishes it.
/// </summary>
public sealed partial class InstallationResetStoppedHostCovenantTests
{
    /// <summary>What goes wrong with the key material the data step prepares its database from.</summary>
    public enum KeyDamage
    {
        /// <summary>The salt sidecar beside the database is gone.</summary>
        SidecarMissing = 1,

        /// <summary>The salt sidecar is there and empty.</summary>
        SidecarEmpty = 2,

        /// <summary>The dedicated Grimoire secret in the secret-store directory is gone.</summary>
        SecretFileMissing = 3,
    }

    /// <summary>The one read that fails once while a resume re-enters the erasure that already committed.</summary>
    public enum OneFailedRead
    {
        /// <summary>The authenticated reset record, read by the facade to confirm its claim.</summary>
        TheResetRecord = 1,

        /// <summary>The dedicated Grimoire secret, read to key the database.</summary>
        TheGrimoireSecret = 2,
    }

    public static TheoryData<bool, KeyDamage, bool> KeyDamageCases()
    {
        TheoryData<bool, KeyDamage, bool> cases = [];

        foreach (bool feature in new[] { false, true })
        {
            foreach (KeyDamage damage in Enum.GetValues<KeyDamage>())
            {
                foreach (bool resumed in new[] { false, true })
                {
                    cases.Add(feature, damage, resumed);
                }
            }
        }

        return cases;
    }

    public static TheoryData<bool, EndOfProcess, OneFailedRead> CommittedErasureCases()
    {
        TheoryData<bool, EndOfProcess, OneFailedRead> cases = [];

        foreach (bool feature in new[] { false, true })
        {
            foreach (EndOfProcess end in new[]
            {
                EndOfProcess.ReceiptStoredRetirementLeft,
                EndOfProcess.JournalRetiredRecordPrepared,
            })
            {
                foreach (OneFailedRead read in Enum.GetValues<OneFailedRead>())
                {
                    cases.Add(feature, end, read);
                }
            }
        }

        return cases;
    }

    /// <summary>
    /// Key material the data step cannot prepare is a typed recovery refusal, not an exception, whether
    /// the apply is a fresh one or a resume, and the record the refusal leaves is the one a clean
    /// resume finishes.
    /// </summary>
    /// <remarks>
    /// The failure comes after the daemon registration is gone and the claim is durable, so there is
    /// nothing to fall back to: the operator has to be told, in the protocol's own words, that the
    /// reset needs recovery, with the record kept for it. Nothing was erased, because the erasure was
    /// never entered.
    /// </remarks>
    [SkippableTheory]
    [MemberData(nameof(KeyDamageCases))]
    public async Task Key_material_the_data_step_cannot_prepare_is_a_typed_recovery_refusal_that_keeps_the_record(
        bool covenantFeature,
        KeyDamage damage,
        bool resumed)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        Guid memoryId = Guid.NewGuid();

        await LabeledSagaMemorySeed.SeedAsync(_fixture, memoryId);

        _ = await StoppedHostResetTestSupport.SeedOrdinarySessionAsync(_fixture);

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        if (resumed)
        {
            CliTestResult ended = await RunAsync(
                covenantFeature,
                services => EndTheProcess(services, EndOfProcess.ClaimWithNoOperation),
                GlobalArguments(apply: true));

            Assert.NotEqual((int)CliExitCode.Success, ended.ExitCode);
        }

        using DamagedKeyMaterial material = new();

        CliTestResult refused = await RunAsync(
            covenantFeature,
            services => StoppedHostResetTestSupport.InterceptDataService(
                services,
                inner => new StoppedHostResetTestSupport.InterceptingStoppedHostDataService(inner)
                {
                    BeforeApply = (_, _) =>
                    {
                        material.Damage(damage);

                        return Task.CompletedTask;
                    },
                }),
            GlobalArguments(apply: true));

        material.Restore();

        string described = $"exit {refused.ExitCode}\nstdout: {refused.Output}\nstderr: {refused.Error}\nrecord: {await DescribeTheRecordAsync()}";

        Assert.NotEqual((int)CliExitCode.Success, refused.ExitCode);

        Assert.Contains(ErrorCodes.Data.RecoveryRequired, refused.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(InstallationResetService.ClaimRetiredNote, refused.Error, StringComparison.Ordinal);

        InstallationResetActivePayloadV3 kept = await ReadTheKeptRecordAsync(described);

        Assert.Equal(InstallationResetPhase.Prepared, kept.Phase);

        Assert.True(kept.PointOfNoReturn, described);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, kept.LastErrorCode);

        Assert.Equal(
            InstallationResetNestedTransitionPhase.Claimed,
            Assert.IsType<InstallationResetNestedTransitionReceiptV1>(kept.NestedTransitionReceipt).Phase);

        // Nothing was erased: the erasure was never entered.
        Assert.Equal(0, await CountFactoryResetOperationsAsync());

        Assert.Equal(1, await StoppedHostResetTestSupport.ScalarAsync("SELECT COUNT(*) FROM saga_memories;"));

        Assert.Equal(1, await StoppedHostResetTestSupport.ScalarAsync("SELECT COUNT(*) FROM \"Sessions\";"));

        Assert.True(File.Exists(ArcanumPaths.GrimoireDatabaseFile), described);

        await AssertTheHostRefusesToStartAsync(covenantFeature);

        CliTestResult finished = await RunAsync(covenantFeature, GlobalArguments(apply: true));

        described = $"exit {finished.ExitCode}\nstdout: {finished.Output}\nstderr: {finished.Error}\nrecord: {await DescribeTheRecordAsync()}";

        Assert.True(finished.ExitCode == (int)CliExitCode.Success, described);

        using JsonDocument document = JsonDocument.Parse(finished.Output);

        Assert.Equal("Completed", document.RootElement.GetProperty("phase").GetString());

        Assert.False(Directory.Exists(ArcanumPaths.GrimoireDirectory), described);

        Assert.False(Directory.Exists(ArcanumPaths.SecretStoreDirectory), described);
    }

    /// <summary>
    /// A resume that re-enters an erasure which already committed, and fails one read on the way in,
    /// keeps its record: a read that failed says nothing about whether the erasure happened, and the
    /// erasure did.
    /// </summary>
    /// <remarks>
    /// Retiring the record here would skip the offline cleanup, the credential cleanup and the
    /// verification, and would leave the host free to start over a Grimoire that was already erased,
    /// with the refusal blaming authority or an unopenable database for what was a single failed read.
    /// </remarks>
    [SkippableTheory]
    [MemberData(nameof(CommittedErasureCases))]
    public async Task A_resume_of_a_committed_erasure_that_fails_one_read_keeps_its_record_and_the_next_resume_finishes(
        bool covenantFeature,
        EndOfProcess end,
        OneFailedRead read)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        Guid memoryId = Guid.NewGuid();

        await LabeledSagaMemorySeed.SeedAsync(_fixture, memoryId);

        _ = await StoppedHostResetTestSupport.SeedOrdinarySessionAsync(_fixture);

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        CliTestResult interrupted = await RunAsync(
            covenantFeature,
            services => EndTheProcess(services, end),
            GlobalArguments(apply: true));

        Assert.NotEqual((int)CliExitCode.Success, interrupted.ExitCode);

        string described = $"first process: exit {interrupted.ExitCode}\nstderr: {interrupted.Error}\nrecord: {await DescribeTheRecordAsync()}";

        // The erasure committed before the process ended: the reset record carries the stored receipt.
        Assert.Equal(
            InstallationResetNestedTransitionPhase.Completed,
            Assert.IsType<InstallationResetNestedTransitionReceiptV1>(
                (await ReadTheKeptRecordAsync(described)).NestedTransitionReceipt).Phase);

        StoppedHostResetTestSupport.OneShotFault fault = new();

        using DamagedKeyMaterial material = new();

        CliTestResult refused = await RunAsync(
            covenantFeature,
            services =>
            {
                if (read is OneFailedRead.TheResetRecord)
                {
                    StoppedHostResetTestSupport.FailActiveRecoveryOnce(services, fault);
                }

                StoppedHostResetTestSupport.InterceptDataService(
                    services,
                    inner => new StoppedHostResetTestSupport.InterceptingStoppedHostDataService(inner)
                    {
                        BeforeApply = (_, _) =>
                        {
                            if (read is OneFailedRead.TheResetRecord)
                            {
                                fault.Arm();
                            }
                            else
                            {
                                material.Damage(KeyDamage.SecretFileMissing);
                            }

                            return Task.CompletedTask;
                        },
                    });
            },
            GlobalArguments(apply: true));

        material.Restore();

        described = $"resume: exit {refused.ExitCode}\nstdout: {refused.Output}\nstderr: {refused.Error}\nrecord: {await DescribeTheRecordAsync()}";

        Assert.NotEqual((int)CliExitCode.Success, refused.ExitCode);

        Assert.Contains(ErrorCodes.Data.RecoveryRequired, refused.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(ErrorCodes.Covenant.ForbiddenAuthority, refused.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(ErrorCodes.Data.InventoryUnavailable, refused.Output, StringComparison.Ordinal);

        InstallationResetActivePayloadV3 kept = await ReadTheKeptRecordAsync(described);

        Assert.Equal(InstallationResetPhase.Prepared, kept.Phase);

        Assert.True(kept.PointOfNoReturn, described);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, kept.LastErrorCode);

        Assert.Equal(
            InstallationResetNestedTransitionPhase.Completed,
            Assert.IsType<InstallationResetNestedTransitionReceiptV1>(kept.NestedTransitionReceipt).Phase);

        // The offline cleanup has not run: the Grimoire and the secret store are still there.
        Assert.True(File.Exists(ArcanumPaths.GrimoireDatabaseFile), described);

        Assert.True(Directory.Exists(ArcanumPaths.SecretStoreDirectory), described);

        await AssertTheHostRefusesToStartAsync(covenantFeature);

        CliTestResult finished = await RunAsync(covenantFeature, GlobalArguments(apply: true));

        described = $"clean resume: exit {finished.ExitCode}\nstdout: {finished.Output}\nstderr: {finished.Error}\nrecord: {await DescribeTheRecordAsync()}";

        Assert.True(finished.ExitCode == (int)CliExitCode.Success, described);

        using JsonDocument document = JsonDocument.Parse(finished.Output);

        Assert.Equal("Completed", document.RootElement.GetProperty("phase").GetString());

        Assert.False(Directory.Exists(ArcanumPaths.GrimoireDirectory), described);

        Assert.False(Directory.Exists(ArcanumPaths.SecretStoreDirectory), described);
    }

    private async Task<InstallationResetActivePayloadV3> ReadTheKeptRecordAsync(string described)
    {
        InstallationResetActiveStore store = new(ArcanumPaths.GrimoireDirectory, _credentials);

        Result<InstallationResetActiveRecoveryState> inspected = await store.InspectAsync(CancellationToken.None);

        Assert.True(inspected.IsSuccess, described);

        return Assert.IsType<InstallationResetActivePublication>(inspected.Value.Publication).Payload;
    }

    /// <summary>The key material of a stopped-host reset, damaged for the length of one run and put back.</summary>
    /// <remarks>
    /// The original file is moved aside rather than rewritten, so what comes back is the very file with
    /// its owner-only permissions and not a copy of its bytes.
    /// </remarks>
    private sealed class DamagedKeyMaterial : IDisposable
    {
        private const string Aside = ".aside";

        private readonly List<string> _moved = [];

        public void Damage(KeyDamage damage)
        {
            string sidecar = GrimoireKdfSidecarFile.GetSidecarPath(ArcanumPaths.GrimoireDatabaseFile);

            switch (damage)
            {
                case KeyDamage.SidecarMissing:

                    MoveAside(sidecar);

                    break;

                case KeyDamage.SidecarEmpty:

                    MoveAside(sidecar);

                    File.WriteAllBytes(sidecar, []);

                    break;

                default:

                    MoveAside(ArcanumPaths.GrimoireKeyStoreFile);

                    break;
            }
        }

        public void Restore()
        {
            foreach (string path in _moved)
            {
                File.Delete(path);

                File.Move(path + Aside, path);
            }

            _moved.Clear();
        }

        public void Dispose() => Restore();

        private void MoveAside(string path)
        {
            File.Move(path, path + Aside);

            _moved.Add(path);
        }
    }
}
