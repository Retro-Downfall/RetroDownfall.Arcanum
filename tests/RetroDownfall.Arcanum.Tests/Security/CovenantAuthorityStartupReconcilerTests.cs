using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The one place Covenant authority becomes available to a running process.
/// </summary>
/// <remarks>
/// Startup publishes authority exactly once, so this is also the last place a tainted installation
/// can be stopped before envelope keys exist. These tests assert the gate's decision is honoured
/// here rather than only at the services that consume the snapshot, because a service added later
/// would otherwise inherit authority nobody re-checked.
/// </remarks>
public sealed class CovenantAuthorityStartupReconcilerTests
{

    private const string Installation = "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90";

    [Fact]
    public async Task A_permitted_process_publishes_the_committed_authority_row()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        using CovenantRuntimeGenerationProvider runtime = new();

        CovenantAuthoritySnapshotProvider provider = new(runtime);

        await database.InstallCanonicalAsync(CancellationToken.None);

        Guid dataset = await ReadDatasetGenerationAsync(database.Connection);

        CovenantAvailabilitySnapshot availability = runtime.PublishAvailability(
            _ => Healthy(dataset));

        await CovenantAuthorityStartupReconciler.ReconcileAsync(
            database.Connection,
            runtime,
            new CovenantEnvelopeMasterKeyProvider(runtime),
            availability,
            Permitted(),
            "master-key",
            CancellationToken.None);

        Assert.NotNull(provider.Current);

        Assert.Equal(Installation, provider.Current!.InstallationIdentity);

        Assert.Equal(dataset, runtime.Current.Keys!.Snapshot.DatasetGeneration);

    }

    [Fact]
    public async Task An_unavailable_canonical_tier_publishes_only_recovery_key_families()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        await AssertRecoveryOnlyAsync(database, Unavailable());

    }

    [Fact]
    public async Task A_degraded_canonical_tier_publishes_only_recovery_key_families()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        await database.InstallCanonicalAsync(CancellationToken.None);

        Guid dataset = await ReadDatasetGenerationAsync(database.Connection);

        await AssertRecoveryOnlyAsync(
            database,
            Healthy(dataset) with
            {

                Canonical = CovenantCapabilityState.Degraded,

                CanonicalSchemaVersion = null,

                CanonicalDiagnosticCode = "covenant.canonical_degraded",

            });

    }

    [Fact]
    public async Task An_absent_canonical_envelope_row_publishes_only_recovery_key_families()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        await database.InstallCanonicalAsync(CancellationToken.None);

        Guid dataset = await ReadDatasetGenerationAsync(database.Connection);

        await database.ExecuteAsync(
            "DELETE FROM covenant_state WHERE StateKey = 1;",
            CancellationToken.None);

        await AssertRecoveryOnlyAsync(database, Healthy(dataset));

    }

    [Fact]
    public async Task A_master_version_mismatched_envelope_publishes_only_recovery_key_families()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        await database.InstallCanonicalAsync(CancellationToken.None);

        Guid dataset = await ReadDatasetGenerationAsync(database.Connection);

        await database.ExecuteAsync(
            "UPDATE covenant_state SET EnvelopeMasterKeyVersion = 2 WHERE StateKey = 1;",
            CancellationToken.None);

        await AssertRecoveryOnlyAsync(database, Healthy(dataset));

    }

    [Fact]
    public async Task Bootstrap_preparation_exposes_no_mixed_authority_and_key_state()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        using CovenantRuntimeGenerationProvider runtime = new();

        using BlockingDerivationCheckpoint checkpoint = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(
            runtime,
            checkpoint,
            CovenantEnvelopeKeyAccessCheckpoint.None);

        CovenantAvailabilitySnapshot availability = runtime.PublishAvailability(
            _ => Unavailable());

        Task reconciliation = Task.Run(
            () => CovenantAuthorityStartupReconciler.ReconcileAsync(
                database.Connection,
                runtime,
                keys,
                availability,
                Permitted(),
                "master-key",
                CancellationToken.None));

        checkpoint.WaitUntilReached();

        Assert.Null(runtime.Current.Keys);

        Assert.Null(runtime.Current.ActiveAuthority);

        checkpoint.Release();

        await reconciliation;

        Assert.NotNull(runtime.Current.Keys);

        Assert.NotNull(runtime.Current.ActiveAuthority);

    }

    [Fact]
    public async Task An_availability_winner_during_bootstrap_makes_initialization_stale()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        using CovenantRuntimeGenerationProvider runtime = new();

        CovenantRuntimeGenerationState expected = runtime.Current;

        CovenantAvailabilitySnapshot? winner = null;

        PublishingDerivationCheckpoint checkpoint = new(() =>
        {

            winner = runtime.PublishAvailability(current => current with
            {

                FeatureEnabled = true,

                Canonical = CovenantCapabilityState.Degraded,

                CanonicalDiagnosticCode = "covenant.bootstrap_winner",

                Accelerator = CovenantCapabilityState.Degraded,

                AcceleratorDiagnosticCode = "covenant.accelerator_winner",

            });

        });

        using CovenantEnvelopeMasterKeyProvider keys = new(
            runtime,
            checkpoint,
            CovenantEnvelopeKeyAccessCheckpoint.None);

        await CovenantAuthorityStartupReconciler.ReconcileAsync(
            database.Connection,
            runtime,
            keys,
            expected.Availability,
            Permitted(),
            "master-key",
            CancellationToken.None);

        Assert.NotNull(winner);

        Assert.Same(winner, runtime.Current.Availability);

        Assert.True(runtime.Current.Availability.FeatureEnabled);

        Assert.Equal(CovenantCapabilityState.Degraded, runtime.Current.Availability.Canonical);

        Assert.Equal("covenant.bootstrap_winner", runtime.Current.Availability.CanonicalDiagnosticCode);

        Assert.Equal(CovenantCapabilityState.Degraded, runtime.Current.Availability.Accelerator);

        Assert.Equal("covenant.accelerator_winner", runtime.Current.Availability.AcceleratorDiagnosticCode);

        Assert.Null(runtime.Current.Keys);

        Assert.Null(runtime.Current.ActiveAuthority);

    }

    [Fact]
    public async Task Bootstrap_derivation_failure_exposes_neither_authority_nor_any_key_family()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        using CovenantRuntimeGenerationProvider runtime = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(
            runtime,
            new FailingDerivationCheckpoint(),
            CovenantEnvelopeKeyAccessCheckpoint.None);

        CovenantAvailabilitySnapshot availability = runtime.PublishAvailability(
            _ => Unavailable());

        await CovenantAuthorityStartupReconciler.ReconcileAsync(
            database.Connection,
            runtime,
            keys,
            availability,
            Permitted(),
            "master-key",
            CancellationToken.None);

        Assert.Null(runtime.Current.ActiveAuthority);

        Assert.Null(runtime.Current.Keys);

        Assert.Null(keys.Current);

    }

    [Fact]
    public async Task A_tainted_process_publishes_no_authority_at_all()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        using CovenantRuntimeGenerationProvider runtime = new();

        CovenantAuthoritySnapshotProvider provider = new(runtime);

        await CovenantAuthorityStartupReconciler.ReconcileAsync(
            database.Connection,
            runtime,
            new CovenantEnvelopeMasterKeyProvider(runtime),
            Unavailable(),
            Tainted(),
            "master-key",
            CancellationToken.None);

        Assert.Null(provider.Current);

        Assert.Null(runtime.Current.Keys);

    }

    [Fact]
    public async Task An_unclassified_process_is_treated_exactly_like_a_tainted_one()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        using CovenantRuntimeGenerationProvider runtime = new();

        CovenantAuthoritySnapshotProvider provider = new(runtime);

        await CovenantAuthorityStartupReconciler.ReconcileAsync(
            database.Connection,
            runtime,
            new CovenantEnvelopeMasterKeyProvider(runtime),
            Unavailable(),
            new HostProcessToolsRuntimePolicy(),
            "master-key",
            CancellationToken.None);

        Assert.Null(provider.Current);

        Assert.Null(runtime.Current.Keys);

    }

    private static IHostProcessToolsRuntimePolicy Permitted()
    {

        HostProcessToolsRuntimePolicy policy = new();

        _ = policy.Publish(new HostProcessToolsStartupDecision(
            HostProcessToolsMarkerPairDisposition.Clean,
            CovenantPermitted: true,
            HostProcessToolsPermitted: false));

        return policy;

    }

    private static IHostProcessToolsRuntimePolicy Tainted()
    {

        HostProcessToolsRuntimePolicy policy = new();

        _ = policy.Publish(new HostProcessToolsStartupDecision(
            HostProcessToolsMarkerPairDisposition.TaintedMatched,
            CovenantPermitted: false,
            HostProcessToolsPermitted: true));

        return policy;

    }

    /// <summary>
    /// A canonical tier that is not healthy, so the reconciler is exercised without a canonical
    /// envelope row: this suite is about whether authority publishes at all.
    /// </summary>
    private static CovenantAvailabilitySnapshot Unavailable() =>
        new(
            Generation: 1,
            FeatureEnabled: false,
            Canonical: CovenantCapabilityState.Unavailable,
            CanonicalSchemaVersion: null,
            CanonicalInstalledFingerprint: null,
            Accelerator: CovenantCapabilityState.Unavailable,
            AcceleratorSchemaVersion: null,
            AcceleratorInstalledFingerprint: null,
            DatasetGeneration: null,
            CanonicalSequence: 0,
            CoreCampaignDeletionSequence: 0,
            AppliedDatasetGeneration: null,
            AppliedSequence: null,
            AppliedCampaignDeletionSequence: null,
            AcceleratorEpoch: 0,
            FtsSynchronization: CovenantFtsSynchronizationState.Unavailable,
            RebuildRequired: true,
            LastHealthTransition: CovenantHealthTransition.Bootstrap,
            CanonicalDiagnosticCode: null,
            AcceleratorDiagnosticCode: null);

    private static CovenantAvailabilitySnapshot Healthy(Guid dataset) =>
        Unavailable() with
        {

            FeatureEnabled = true,

            Canonical = CovenantCapabilityState.Healthy,

            CanonicalSchemaVersion = 1,

            CanonicalInstalledFingerprint = "sha256-canonical",

            DatasetGeneration = dataset,

            CanonicalDiagnosticCode = null,

        };

    private static async Task AssertRecoveryOnlyAsync(
        CovenantSchemaScratchDatabase database,
        CovenantAvailabilitySnapshot availability)
    {

        using CovenantRuntimeGenerationProvider runtime = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        CovenantAuthoritySnapshotProvider authority = new(runtime);

        CovenantEnvelopeCodec codec = new(keys, TimeProvider.System);

        CovenantAvailabilitySnapshot published = runtime.PublishAvailability(
            _ => availability);

        await CovenantAuthorityStartupReconciler.ReconcileAsync(
            database.Connection,
            runtime,
            keys,
            published,
            Permitted(),
            "master-key",
            CancellationToken.None);

        Assert.NotNull(authority.Current);

        Assert.NotNull(keys.Current);

        Assert.Null(keys.Current!.Snapshot.DatasetGeneration);

        foreach (CovenantEnvelopePurpose purpose in Enum.GetValues<CovenantEnvelopePurpose>())
        {

            Result<string> encoded = codec.Encode(purpose, [1], TimeSpan.FromMinutes(1));

            if (CovenantEnvelopeLimits.IsDatasetKeyed(purpose))
            {

                Assert.True(encoded.IsFailure);

                Assert.Equal(ErrorCodes.Covenant.Unavailable, encoded.Error.Code);

            }
            else
            {

                Assert.True(encoded.IsSuccess);

            }

        }

    }

    private static async Task<Guid> ReadDatasetGenerationAsync(SqliteConnection connection)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;";

        object? value = await command.ExecuteScalarAsync(CancellationToken.None);

        byte[] bytes = Assert.IsType<byte[]>(value);

        return new Guid(bytes);

    }

    private static async Task<CovenantSchemaScratchDatabase> CreateAsync()
    {

        CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase
            .CreateAsync(CancellationToken.None);

        try
        {

            await database.InstallCoreObjectsAsync(["covenant_authority_state"], CancellationToken.None);

            await using SqliteCommand seed = database.Connection.CreateCommand();

            seed.CommandText = """
                INSERT INTO covenant_authority_state (
                    StateKey,
                    InstallationIdentity,
                    AuthorityEpoch,
                    CurrentMasterKeyVersion,
                    CurrentMasterKeyFingerprint,
                    RecoveryEnvelopeEpoch,
                    HostToolsStateCode,
                    TaintTimeMasterVersion,
                    TaintFingerprint,
                    TransitionId,
                    UpdatedAtUtc)
                VALUES (1, $identity, 1, 1, $fingerprint, 1, 1, NULL, NULL, NULL, '2026-08-16T00:00:00.0000000+00:00');
                """;

            _ = seed.Parameters.AddWithValue("$identity", Installation);

            _ = seed.Parameters.AddWithValue("$fingerprint", new byte[32]);

            _ = await seed.ExecuteNonQueryAsync(CancellationToken.None);

            return database;

        }
        catch
        {

            await database.DisposeAsync();

            throw;

        }

    }

    private sealed class BlockingDerivationCheckpoint : ICovenantEnvelopeDerivationCheckpoint, IDisposable
    {

        private readonly ManualResetEventSlim _reached = new();

        private readonly ManualResetEventSlim _release = new();

        private int _blocked;

        public void Reached(CovenantEnvelopeDerivationStep step, int purposeKeysDerived)
        {

            if (step != CovenantEnvelopeDerivationStep.PurposeKeyDerived
                || Interlocked.Exchange(ref _blocked, 1) != 0)
            {

                return;

            }

            _reached.Set();

            _release.Wait();

        }

        public void Zeroized(CovenantEnvelopeSensitiveBufferKind kind, bool isZero)
        {
        }

        internal void WaitUntilReached() => Assert.True(_reached.Wait(TimeSpan.FromSeconds(5)));

        internal void Release() => _release.Set();

        public void Dispose()
        {

            _reached.Dispose();

            _release.Dispose();

        }

    }

    private sealed class FailingDerivationCheckpoint : ICovenantEnvelopeDerivationCheckpoint
    {

        public void Reached(CovenantEnvelopeDerivationStep step, int purposeKeysDerived) =>
            throw new InvalidOperationException("Injected bootstrap derivation failure.");

        public void Zeroized(CovenantEnvelopeSensitiveBufferKind kind, bool isZero)
        {
        }

    }

    private sealed class PublishingDerivationCheckpoint(Action publish) : ICovenantEnvelopeDerivationCheckpoint
    {

        private readonly Action _publish = publish ?? throw new ArgumentNullException(nameof(publish));

        private int _published;

        public void Reached(CovenantEnvelopeDerivationStep step, int purposeKeysDerived)
        {

            if (step == CovenantEnvelopeDerivationStep.PurposeKeyDerived
                && Interlocked.Exchange(ref _published, 1) == 0)
            {

                _publish();

            }

        }

        public void Zeroized(CovenantEnvelopeSensitiveBufferKind kind, bool isZero)
        {
        }

    }

}
