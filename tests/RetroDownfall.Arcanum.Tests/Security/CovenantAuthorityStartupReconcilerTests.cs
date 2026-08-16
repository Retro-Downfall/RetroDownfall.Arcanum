using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
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

        CovenantAuthoritySnapshotProvider provider = new();

        await CovenantAuthorityStartupReconciler.ReconcileAsync(
            database.Connection,
            provider,
            new CovenantEnvelopeMasterKeyProvider(),
            Unavailable(),
            Permitted(),
            "master-key",
            CancellationToken.None);

        Assert.NotNull(provider.Current);

        Assert.Equal(Installation, provider.Current!.InstallationIdentity);

    }

    [Fact]
    public async Task A_tainted_process_publishes_no_authority_at_all()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        CovenantAuthoritySnapshotProvider provider = new();

        await CovenantAuthorityStartupReconciler.ReconcileAsync(
            database.Connection,
            provider,
            new CovenantEnvelopeMasterKeyProvider(),
            Unavailable(),
            Tainted(),
            "master-key",
            CancellationToken.None);

        Assert.Null(provider.Current);

    }

    [Fact]
    public async Task An_unclassified_process_is_treated_exactly_like_a_tainted_one()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        CovenantAuthoritySnapshotProvider provider = new();

        await CovenantAuthorityStartupReconciler.ReconcileAsync(
            database.Connection,
            provider,
            new CovenantEnvelopeMasterKeyProvider(),
            Unavailable(),
            new HostProcessToolsRuntimePolicy(),
            "master-key",
            CancellationToken.None);

        Assert.Null(provider.Current);

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

}
