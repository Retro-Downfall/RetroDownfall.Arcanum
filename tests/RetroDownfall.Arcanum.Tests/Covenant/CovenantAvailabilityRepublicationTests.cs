using System.Net;
using System.Net.Http.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Backup;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Every writer that changes the persisted Covenant search tuple inside a running host republishes the
/// in-process availability snapshot after its transaction commits, so <c>memory status</c> follows the
/// database without a restart.
/// </summary>
/// <remarks>
/// <para>Each host leaves its background maintenance pass out, and each test runs the pass, or the one
/// coordinator it needs, itself. A pass on its own timer could otherwise apply a projection between a
/// write and the assertion that the write left the projection pending.</para>
///
/// <para>The oracle is a restart. <see cref="AssertRepublishedAsync"/> publishes the database as it now
/// stands into a fresh snapshot, exactly as bootstrap does, and requires the live snapshot to carry the
/// same tuple and to name the writer that published it.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class CovenantAvailabilityRepublicationTests
{
    private const string Key = "republication.answer-style";

    private const string OtherKey = "republication.answer-length";

    private const string RebuildOwner = "covenant-republication-test";

    private const string VaultKey = "republication.vault";

    private const string HarborKey = "republication.harbor";

    private static CancellationToken Token => CancellationToken.None;

    /// <summary>
    /// A fresh installation's first maintenance pass carries search to Synchronized in the running host.
    /// It used to report Synchronizing until the host restarted.
    /// </summary>
    [SkippableFact]
    public async Task A_fresh_installation_reports_synchronized_search_after_one_maintenance_pass()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

        Assert.Equal(CovenantSearchHealthState.Synchronizing, (await SearchAsync(client)).State);

        await PassAsync(host);

        CovenantSearchHealthDto search = await SearchAsync(client);

        Assert.Equal(CovenantSearchHealthState.Healthy, search.State);

        Assert.Equal(CovenantSearchExecutionMode.Fts, search.ExecutionMode);

        Assert.Equal(CovenantFtsSynchronizationState.Synchronized, Live(host).FtsSynchronization);

        _ = await AssertRepublishedAsync(host, CovenantHealthTransition.AcceleratorSynchronization);
    }

    /// <summary>
    /// An outbox batch whose transaction fails after it has moved the applied tuple, and so rolls back,
    /// publishes nothing. The next batch that commits publishes what that one could not.
    /// </summary>
    [SkippableFact]
    public async Task An_outbox_batch_that_rolls_back_publishes_nothing_and_the_next_committed_batch_corrects_status()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

        CovenantAvailabilitySnapshot before = Live(host);

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await OpenAsync(scope);

            CovenantSearchOutboxCoordinator failing = new(
                scope.ServiceProvider.GetRequiredService<ICovenantOperationGate>(),
                scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>(),
                scope.ServiceProvider.GetRequiredService<CovenantSearchOutboxWorker>(),
                scope.ServiceProvider.GetRequiredService<CovenantAvailabilityRepublisher>())
            {
                BeforeCommitForTesting = static async (transaction, ct) =>
                {
                    await using SqliteCommand applied = transaction.Connection!.CreateCommand();

                    applied.Transaction = transaction;

                    applied.CommandText = "SELECT AppliedSearchSequence FROM covenant_state WHERE StateKey = 1;";

                    // The update has run inside this transaction, so the failure lands after it.
                    Assert.Equal(1L, await applied.ExecuteScalarAsync(ct));

                    throw new InvalidOperationException("Injected failure between the applied-tuple update and the commit.");
                },
            };

            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                () => failing.SynchronizeAsync(CovenantSearchOutboxWorker.DefaultBatchRows, Token).AsTask());
        }

        Assert.Same(before, Live(host));

        Assert.Equal(1, await ScalarAsync(host, "SELECT AppliedSearchSequence IS NULL FROM covenant_state WHERE StateKey = 1;"));

        await PassAsync(host);

        Assert.Equal(CovenantSearchHealthState.Healthy, (await SearchAsync(client)).State);

        _ = await AssertRepublishedAsync(host, CovenantHealthTransition.AcceleratorSynchronization);
    }

    /// <summary>
    /// After an installation-level Covenant reset, which publishes its new dataset as owing a projection,
    /// the next maintenance pass carries search back to Synchronized without a restart.
    /// </summary>
    [SkippableFact]
    public async Task A_Covenant_reset_reports_synchronized_search_again_after_one_maintenance_pass()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

        await PassAsync(host);

        Guid? erased = Live(host).DatasetGeneration;

        await ResetCovenantAsync(client);

        Assert.NotEqual(erased, Live(host).DatasetGeneration);

        Assert.Equal(CovenantSearchHealthState.Synchronizing, (await SearchAsync(client)).State);

        await PassAsync(host);

        CovenantSearchHealthDto search = await SearchAsync(client);

        Assert.Equal(CovenantSearchHealthState.Healthy, search.State);

        Assert.Equal(CovenantSearchExecutionMode.Fts, search.ExecutionMode);

        // The reset records a full rebuild as owed, but the projection it left empty was adopted and is
        // answering, so there is nothing for the operator to rebuild.
        Assert.Equal(CovenantSearchRebuildGuidance.None, search.Guidance);

        _ = await AssertRepublishedAsync(host, CovenantHealthTransition.AcceleratorSynchronization);
    }

    /// <summary>
    /// A host that booted Synchronized reports the projection a later operator write left pending, and
    /// reports Synchronized again once a pass has applied it. It used to report Healthy throughout.
    /// </summary>
    [SkippableFact]
    public async Task A_host_that_booted_synchronized_reports_a_pending_projection_until_a_pass_applies_it()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using RestartableArcanumProfileFixture profile = new();

        await using (ArcanumWebApplicationFactory first = Host(credentials, profile))
        {
            using HttpClient client = first.CreateAuthenticatedClient();

            _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

            await PassAsync(first);
        }

        await using ArcanumWebApplicationFactory second = Host(credentials, profile);

        using HttpClient restarted = second.CreateAuthenticatedClient();

        Assert.Equal(CovenantSearchHealthState.Healthy, (await SearchAsync(restarted)).State);

        _ = await new MemoryErasureRouteDriver(restarted).SetCovenantAsync(CovenantScope.Global, null, OtherKey, "Keep answers short.");

        CovenantSearchHealthDto pending = await SearchAsync(restarted);

        Assert.Equal(CovenantSearchHealthState.Synchronizing, pending.State);

        Assert.Equal(CovenantSearchExecutionMode.CanonicalFallback, pending.ExecutionMode);

        _ = await AssertRepublishedAsync(second, CovenantHealthTransition.CanonicalMutation);

        await PassAsync(second);

        CovenantSearchHealthDto applied = await SearchAsync(restarted);

        Assert.Equal(CovenantSearchHealthState.Healthy, applied.State);

        Assert.Equal(CovenantSearchExecutionMode.Fts, applied.ExecutionMode);

        _ = await AssertRepublishedAsync(second, CovenantHealthTransition.AcceleratorSynchronization);
    }

    /// <summary>
    /// The owner-cleanup batch that removes a deleted Campaign's head advances the canonical search
    /// sequence, and republishes that position as owing a projection.
    /// </summary>
    [SkippableFact]
    public async Task Owner_cleanup_republishes_the_canonical_position_its_batch_committed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        Guid campaign = await RegisterCampaignAsync(host, client, "cleanup");

        _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Campaign, campaign, Key, "Campaign answer style.");

        await PassAsync(host);

        long canonical = await ScalarAsync(host, "SELECT CanonicalSearchSequence FROM covenant_state WHERE StateKey = 1;");

        await DeleteCampaignAsync(client, campaign);

        CovenantCleanupOutcome cleaned = await CleanUpOwnersAsync(host);

        Assert.Equal(1, cleaned.HeadsRemoved);

        CovenantAvailabilitySnapshot live = await AssertRepublishedAsync(host, CovenantHealthTransition.OwnerCleanup);

        Assert.Equal(canonical + 1, live.CanonicalSequence);

        Assert.Equal(CovenantFtsSynchronizationState.Dirty, live.FtsSynchronization);
    }

    /// <summary>
    /// An owner-cleanup batch that removed no head still republishes. The Campaign delete itself
    /// publishes nothing, because it holds no Covenant lease, so this republication is what carries the
    /// core Campaign-deletion sequence into the snapshot.
    /// </summary>
    [SkippableFact]
    public async Task Owner_cleanup_that_removed_no_head_still_republishes_the_core_campaign_deletion_sequence()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

        await PassAsync(host);

        Guid campaign = await RegisterCampaignAsync(host, client, "headless-cleanup");

        await DeleteCampaignAsync(client, campaign);

        long core = await CoreCampaignDeletionSequenceAsync(host);

        Assert.True(Live(host).CoreCampaignDeletionSequence < core, "The Campaign delete published its own deletion sequence.");

        CovenantCleanupOutcome cleaned = await CleanUpOwnersAsync(host);

        Assert.Equal(0, cleaned.HeadsRemoved);

        CovenantAvailabilitySnapshot live = await AssertRepublishedAsync(host, CovenantHealthTransition.OwnerCleanup);

        Assert.Equal(core, live.CoreCampaignDeletionSequence);
    }

    /// <summary>
    /// Deleting a Campaign that holds no Covenant entries moves only the core Campaign-deletion sequence.
    /// The next maintenance pass carries the applied watermark up to it, so search returns to
    /// Synchronized without waiting for an unrelated Covenant write.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_campaign_with_no_covenant_entries_returns_search_to_synchronized_after_one_pass()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

        await PassAsync(host);

        Assert.Equal(CovenantSearchHealthState.Healthy, (await SearchAsync(client)).State);

        long canonical = await ScalarAsync(host, "SELECT CanonicalSearchSequence FROM covenant_state WHERE StateKey = 1;");

        Guid campaign = await RegisterCampaignAsync(host, client, "headless");

        await DeleteCampaignAsync(client, campaign);

        long core = await CoreCampaignDeletionSequenceAsync(host);

        await PassAsync(host);

        CovenantSearchHealthDto search = await SearchAsync(client);

        Assert.Equal(CovenantSearchHealthState.Healthy, search.State);

        Assert.Equal(CovenantSearchExecutionMode.Fts, search.ExecutionMode);

        CovenantAvailabilitySnapshot live = await AssertRepublishedAsync(host, CovenantHealthTransition.AcceleratorSynchronization);

        Assert.Equal(core, live.AppliedCampaignDeletionSequence);

        // Only the watermark moved: no head was removed, so no search sequence was spent.
        Assert.Equal(canonical, live.CanonicalSequence);

        Assert.Equal(canonical, live.AppliedSequence);
    }

    /// <summary>
    /// A fresh installation records a full rebuild as owed, and nothing in a host clears that record.
    /// Once one pass has synchronized search, the accelerator answers every query, so status asks the
    /// operator for no rebuild.
    /// </summary>
    [SkippableFact]
    public async Task A_fresh_installation_reports_no_rebuild_guidance_once_a_pass_has_synchronized_search()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

        await PassAsync(host);

        CovenantSearchHealthDto search = await SearchAsync(client);

        Assert.Equal(CovenantSearchHealthState.Healthy, search.State);

        Assert.Equal(CovenantSearchExecutionMode.Fts, search.ExecutionMode);

        Assert.Equal(CovenantSearchRebuildGuidance.None, search.Guidance);

        // The persisted record is untouched; only what status asks of the operator changed.
        Assert.Equal(
            (long)CovenantFtsRebuildState.FullRebuildRequired,
            await ScalarAsync(host, "SELECT RebuildStateCode FROM covenant_state WHERE StateKey = 1;"));
    }

    /// <summary>
    /// A rebuild abandoned after its base scan wrote documents leaves a projection the outbox cannot
    /// adopt. Search is not synchronized, and status still asks for the rebuild that alone can recover it.
    /// </summary>
    [SkippableFact]
    public async Task A_projection_the_outbox_cannot_adopt_still_reports_rebuild_required()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

        await PassAsync(host);

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await OpenAsync(scope);

            CovenantIndexRebuildCoordinator rebuild = scope.ServiceProvider.GetRequiredService<CovenantIndexRebuildCoordinator>();

            ILongRunningOperationStore operations = scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();

            Result<LongRunningOperation> started = await rebuild.StartAsync(RebuildOwner, Token);

            Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

            Result<CovenantIndexRebuildProgress> cleared = await rebuild.AdvanceAsync(started.Value, RebuildOwner, Token);

            Assert.True(cleared.IsSuccess, cleared.IsFailure ? cleared.Error.Message : null);

            LongRunningOperation? operation = await operations.GetAsync(started.Value.Id, Token);

            Assert.NotNull(operation);

            Result<CovenantIndexRebuildProgress> scanned = await rebuild.AdvanceAsync(operation, RebuildOwner, Token);

            Assert.True(scanned.IsSuccess, scanned.IsFailure ? scanned.Error.Message : null);

            Assert.Equal(1, scanned.Value.BaseHeadsProcessed);
        }

        await PassAsync(host);

        CovenantSearchHealthDto search = await SearchAsync(client);

        Assert.Equal(CovenantSearchHealthState.Synchronizing, search.State);

        Assert.Equal(CovenantSearchExecutionMode.CanonicalFallback, search.ExecutionMode);

        Assert.Equal(CovenantSearchRebuildGuidance.RebuildRequired, search.Guidance);

        CovenantAvailabilitySnapshot live = await AssertRepublishedAsync(host, CovenantHealthTransition.AcceleratorSynchronization);

        Assert.Null(live.AppliedSequence);

        Assert.True(live.RebuildRequired);
    }

    /// <summary>A review apply that retires a head republishes the canonical position it committed.</summary>
    [SkippableFact]
    public async Task A_review_apply_republishes_the_canonical_position_it_committed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

        await PassAsync(host);

        await RetireByReviewAsync(client, Key);

        CovenantAvailabilitySnapshot live = await AssertRepublishedAsync(host, CovenantHealthTransition.CanonicalMutation);

        Assert.Equal(CovenantFtsSynchronizationState.Dirty, live.FtsSynchronization);
    }

    /// <summary>
    /// A Covenant entry erasure appends its absent deltas and advances the canonical search sequence, and
    /// republishes that position as owing a projection.
    /// </summary>
    [SkippableFact]
    public async Task A_Covenant_entry_erasure_republishes_the_canonical_position_it_committed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        MemoryErasureRouteDriver driver = new(client);

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

        await PassAsync(host);

        long canonical = await ScalarAsync(host, "SELECT CanonicalSearchSequence FROM covenant_state WHERE StateKey = 1;");

        _ = await driver.EraseCovenantAsync(CovenantScope.Global, null, Key);

        CovenantAvailabilitySnapshot live = await AssertRepublishedAsync(host, CovenantHealthTransition.CanonicalMutation);

        Assert.Equal(canonical + 1, live.CanonicalSequence);

        Assert.Equal(CovenantFtsSynchronizationState.Dirty, live.FtsSynchronization);
    }

    /// <summary>
    /// The index rebuild republishes the owed rebuild its first batch records, and the synchronized
    /// projection its last batch completes, after which status no longer asks for a rebuild.
    /// </summary>
    [SkippableFact]
    public async Task An_index_rebuild_republishes_the_rebuild_it_owes_and_then_the_projection_it_completed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

        await PassAsync(host);

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        await OpenAsync(scope);

        CovenantIndexRebuildCoordinator rebuild = scope.ServiceProvider.GetRequiredService<CovenantIndexRebuildCoordinator>();

        ILongRunningOperationStore operations = scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();

        Result<LongRunningOperation> started = await rebuild.StartAsync(RebuildOwner, Token);

        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

        Result<CovenantIndexRebuildProgress> first = await rebuild.AdvanceAsync(started.Value, RebuildOwner, Token);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        Assert.Equal(CovenantIndexRebuildPhase.BaseScan, first.Value.Phase);

        CovenantAvailabilitySnapshot owed = await AssertRepublishedAsync(host, CovenantHealthTransition.AcceleratorRebuild);

        Assert.True(owed.RebuildRequired);

        Assert.Equal(CovenantFtsSynchronizationState.Dirty, owed.FtsSynchronization);

        CovenantIndexRebuildProgress progress = first.Value;

        for (int batch = 0; batch < 16 && progress.Phase is not CovenantIndexRebuildPhase.Completed; batch++)
        {
            LongRunningOperation? operation = await operations.GetAsync(started.Value.Id, Token);

            Assert.NotNull(operation);

            Result<CovenantIndexRebuildProgress> advanced = await rebuild.AdvanceAsync(operation, RebuildOwner, Token);

            Assert.True(advanced.IsSuccess, advanced.IsFailure ? advanced.Error.Message : null);

            progress = advanced.Value;
        }

        Assert.Equal(CovenantIndexRebuildPhase.Completed, progress.Phase);

        CovenantAvailabilitySnapshot rebuilt = await AssertRepublishedAsync(host, CovenantHealthTransition.AcceleratorRebuild);

        Assert.False(rebuilt.RebuildRequired);

        Assert.Equal(CovenantFtsSynchronizationState.Synchronized, rebuilt.FtsSynchronization);

        CovenantSearchHealthDto search = await SearchAsync(client);

        Assert.Equal(CovenantSearchHealthState.Healthy, search.State);

        Assert.Equal(CovenantSearchRebuildGuidance.None, search.Guidance);
    }

    /// <summary>
    /// A degraded accelerator is republished as unavailable, however current its persisted tuple is,
    /// because only the canonical fallback is answering queries.
    /// </summary>
    [SkippableFact]
    public async Task A_degraded_accelerator_is_republished_as_unavailable_however_current_its_tuple()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

        DegradeAccelerator(host);

        await PassAsync(host);

        CovenantAvailabilitySnapshot live = await AssertRepublishedAsync(host, CovenantHealthTransition.AcceleratorSynchronization);

        // The tuple caught up, so the accelerator's health is the only thing keeping this from Synchronized.
        Assert.Equal(live.CanonicalSequence, live.AppliedSequence);

        Assert.Equal(CovenantFtsSynchronizationState.Unavailable, live.FtsSynchronization);
    }

    /// <summary>
    /// A restore whose archive carried an empty projection keeps the archived heads but resets the canonical
    /// search sequence to 0 and drains the outbox, so no delta can ever project those heads. The outbox must
    /// not adopt that empty projection: search stays on the canonical fallback, which still finds every
    /// restored entry, and status asks for the rebuild that alone can project them.
    /// </summary>
    [SkippableFact]
    public async Task A_restore_whose_archive_carried_an_empty_projection_keeps_search_on_the_canonical_fallback()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await RestoreUnprojectedArchiveAsync(harness);

        await using ArcanumWebApplicationFactory host = Host(harness.Credentials, harness.Profile);

        using HttpClient client = host.CreateAuthenticatedClient();

        Assert.Equal(CovenantSearchRebuildGuidance.RebuildRequired, (await SearchAsync(client)).Guidance);

        await PassAsync(host);

        AssertOwesRebuild(await SearchAsync(client));

        CovenantPageDto found = await QueryAsync(client, "vault");

        Assert.Equal(CovenantSearchExecutionMode.CanonicalFallback, found.Search.ExecutionMode);

        Assert.Single(found.Items);

        CovenantAvailabilitySnapshot live = await AssertRepublishedAsync(host, CovenantHealthTransition.AcceleratorSynchronization);

        Assert.Null(live.AppliedSequence);
    }

    /// <summary>
    /// The same restore followed by a write to one restored key before the first pass. The outbox now
    /// starts at sequence 1, but that delta projects one head and leaves the other restored heads out, so
    /// the empty projection is still not adopted.
    /// </summary>
    [SkippableFact]
    public async Task A_restore_followed_by_a_write_to_one_restored_key_still_keeps_search_on_the_canonical_fallback()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await RestoreUnprojectedArchiveAsync(harness);

        await using ArcanumWebApplicationFactory host = Host(harness.Credentials, harness.Profile);

        using HttpClient client = host.CreateAuthenticatedClient();

        _ = await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, VaultKey, "Keep the vault key in the safe.");

        Assert.Equal(1L, await ScalarAsync(host, "SELECT MIN(SearchSequence) FROM covenant_search_outbox;"));

        await PassAsync(host);

        AssertOwesRebuild(await SearchAsync(client));

        CovenantPageDto found = await QueryAsync(client, "harbor");

        Assert.Equal(CovenantSearchExecutionMode.CanonicalFallback, found.Search.ExecutionMode);

        Assert.Single(found.Items);
    }

    /// <summary>
    /// An outbox that no longer starts at the dataset's first sequence is not replayed onto an empty
    /// projection, even when what remains would touch every head. Here an earlier pass consumed the first
    /// delta and a rebuild that was then abandoned cleared the projection and the applied tuple.
    /// </summary>
    [SkippableFact]
    public async Task An_outbox_that_no_longer_starts_at_the_first_sequence_is_not_adopted_onto_an_empty_projection()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = Host(new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        MemoryErasureRouteDriver driver = new(client);

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in British English.");

        await PassAsync(host);

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "Answer in Scottish English.");

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await OpenAsync(scope);

            CovenantIndexRebuildCoordinator rebuild = scope.ServiceProvider.GetRequiredService<CovenantIndexRebuildCoordinator>();

            Result<LongRunningOperation> started = await rebuild.StartAsync(RebuildOwner, Token);

            Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

            Result<CovenantIndexRebuildProgress> cleared = await rebuild.AdvanceAsync(started.Value, RebuildOwner, Token);

            Assert.True(cleared.IsSuccess, cleared.IsFailure ? cleared.Error.Message : null);
        }

        Assert.Equal(0L, await ScalarAsync(host, "SELECT COUNT(*) FROM covenant_search_documents;"));

        Assert.Equal(2L, await ScalarAsync(host, "SELECT MIN(SearchSequence) FROM covenant_search_outbox;"));

        await PassAsync(host);

        AssertOwesRebuild(await SearchAsync(client));

        CovenantPageDto found = await QueryAsync(client, "Scottish");

        Assert.Equal(CovenantSearchExecutionMode.CanonicalFallback, found.Search.ExecutionMode);

        Assert.Single(found.Items);
    }

    /// <summary>
    /// A host with the Covenant on and its background maintenance pass left out, so every pass a test needs
    /// is one it runs.
    /// </summary>
    internal static ArcanumWebApplicationFactory Host(
        InMemoryOsCredentialStore credentials,
        RestartableArcanumProfileFixture? profile = null)
    {
        ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(credentials, profile, covenant: true);

        Action<IServiceCollection>? composed = host.ServiceOverrides;

        host.ServiceOverrides = services =>
        {
            composed?.Invoke(services);

            _ = services.Remove(Assert.Single(services, IsBackgroundMaintenancePass));
        };

        return host;
    }

    /// <summary>
    /// Requires the host's live snapshot to carry exactly what a restart would publish from the database as
    /// it now stands, and to name the writer that published it.
    /// </summary>
    internal static async Task<CovenantAvailabilitySnapshot> AssertRepublishedAsync(
        ArcanumWebApplicationFactory host,
        CovenantHealthTransition transition)
    {
        CovenantAvailabilitySnapshot live = Live(host);

        CovenantAvailability restarted = new();

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            SqliteConnection connection = await scope.ServiceProvider
                .GetRequiredService<ICovenantConnectionSource>()
                .GetOpenConnectionAsync(Token);

            Assert.True(await CovenantPersistedAvailabilityPublisher.PublishAsync(
                restarted,
                connection,
                live.Accelerator is CovenantCapabilityState.Healthy,
                CovenantHealthTransition.Bootstrap,
                Token));
        }

        Assert.Equal(PublishedTuple(restarted.Current), PublishedTuple(live));

        Assert.Equal(transition, live.LastHealthTransition);

        return live;
    }

    private static (Guid? Dataset, long Canonical, long CoreCampaignDeletions, Guid? AppliedDataset, long? Applied,
        long? AppliedCampaignDeletions, ulong AcceleratorEpoch, CovenantFtsSynchronizationState Synchronization,
        bool RebuildRequired) PublishedTuple(CovenantAvailabilitySnapshot snapshot) =>
        (snapshot.DatasetGeneration,
            snapshot.CanonicalSequence,
            snapshot.CoreCampaignDeletionSequence,
            snapshot.AppliedDatasetGeneration,
            snapshot.AppliedSequence,
            snapshot.AppliedCampaignDeletionSequence,
            snapshot.AcceleratorEpoch,
            snapshot.FtsSynchronization,
            snapshot.RebuildRequired);

    private static bool IsBackgroundMaintenancePass(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IHostedService)
        && descriptor.ImplementationFactory?.GetType().GenericTypeArguments is [_, Type implementation]
        && implementation == typeof(InstallationResetRecoveryAwareHostedService<CovenantMaintenanceHostedService>);

    private static CovenantAvailabilitySnapshot Live(ArcanumWebApplicationFactory host) =>
        host.Services.GetRequiredService<ICovenantAvailability>().Current;

    /// <summary>One whole maintenance pass, through the entry point the hosted loop calls on its interval.</summary>
    private static async Task PassAsync(ArcanumWebApplicationFactory host) =>
        Assert.True(await host.Services.GetRequiredService<CovenantMaintenanceHostedService>().RunOnceAsync(Token));

    /// <summary>One owner-cleanup batch, on a scope whose connection EF opened, as the pass runs it.</summary>
    private static async Task<CovenantCleanupOutcome> CleanUpOwnersAsync(ArcanumWebApplicationFactory host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        await OpenAsync(scope);

        Result<CovenantCleanupOutcome> cleaned = await scope.ServiceProvider
            .GetRequiredService<CovenantOwnerCleanupCoordinator>()
            .RunBatchAsync(CovenantCleanupWorker.DefaultBatchSize, Token);

        Assert.True(cleaned.IsSuccess, cleaned.IsFailure ? cleaned.Error.Message : null);

        Assert.Equal(1, cleaned.Value.CampaignsCleaned);

        return cleaned.Value;
    }

    private static void AssertOwesRebuild(CovenantSearchHealthDto search)
    {
        Assert.Equal(CovenantSearchHealthState.Synchronizing, search.State);

        Assert.Equal(CovenantSearchExecutionMode.CanonicalFallback, search.ExecutionMode);

        Assert.Equal(CovenantSearchRebuildGuidance.RebuildRequired, search.Guidance);
    }

    /// <summary>
    /// Authors two Global entries on a host whose maintenance pass never runs, so the archived projection
    /// is empty while its heads exist, then archives it and restores it in place with its protected state.
    /// </summary>
    /// <param name="beforeArchive">Runs on the stopped source installation just before it is archived.</param>
    internal static async Task RestoreUnprojectedArchiveAsync(
        MemoryErasureRestoreHarness harness,
        Func<Task>? beforeArchive = null)
    {
        await using (ArcanumWebApplicationFactory source = Host(harness.Credentials, harness.Profile))
        {
            using HttpClient client = source.CreateAuthenticatedClient();

            MemoryErasureRouteDriver driver = new(client);

            _ = await driver.SetCovenantAsync(CovenantScope.Global, null, VaultKey, "Keep the vault key offline.");

            _ = await driver.SetCovenantAsync(CovenantScope.Global, null, HarborKey, "Moor at the east harbor.");
        }

        SqliteConnection.ClearAllPools();

        if (beforeArchive is not null)
        {
            await beforeArchive();

            SqliteConnection.ClearAllPools();
        }

        string archive = await harness.CreateArchiveAsync("covenant-unprojected.arcbackup");

        BackupRestoreResult restored = await harness.CreateRestoreService(covenantStaging: true).RestoreAsync(
            new BackupRestoreRequest(
                archive,
                BackupRestoreConflictMode.ReplaceInstallation,
                Confirmed: true,
                CreateSafetyBackup: false,
                ProtectedStateMode: BackupProtectedStateMode.RestoreProtectedState,
                ProtectedStateConfirmed: true),
            MemoryErasureRestoreHarness.Passphrase.AsMemory(),
            Token);

        Assert.Equal(BackupRestoreStatus.Completed, restored.Status);

        // The state the reconciler leaves: the heads, an empty projection, a reset sequence and no deltas.
        await using (SqliteConnection live = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            Assert.Equal(2L, await CountAsync(live, "SELECT COUNT(*) FROM covenant_heads;"));

            Assert.Equal(0L, await CountAsync(live, "SELECT COUNT(*) FROM covenant_search_documents;"));

            Assert.Equal(0L, await CountAsync(live, "SELECT CanonicalSearchSequence FROM covenant_state WHERE StateKey = 1;"));

            Assert.Equal(0L, await CountAsync(live, "SELECT COUNT(*) FROM covenant_search_outbox;"));
        }

        SqliteConnection.ClearAllPools();
    }

    private static async Task<CovenantPageDto> QueryAsync(HttpClient client, string text)
    {
        using HttpResponseMessage response = await new MemoryErasureRouteDriver(client).PostAsync(
            "/api/memory/covenant/query",
            new CovenantQueryRequest(CovenantCursorScopeSelection.AllScopes, null, text, null, CovenantLifecycle.Any, null, 50, null),
            ArcanumJsonContext.Default.CovenantQueryRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(response, ArcanumJsonContext.Default.ApiResponseCovenantPageDto);
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task DeleteCampaignAsync(HttpClient client, Guid campaign)
    {
        using HttpResponseMessage deleted = await client.DeleteAsync($"/api/campaigns/{campaign:D}", Token);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    /// <summary>The core Campaign-deletion sequence, as the publisher reads it.</summary>
    private static Task<long> CoreCampaignDeletionSequenceAsync(ArcanumWebApplicationFactory host) =>
        ScalarAsync(host, "SELECT COALESCE(MAX(Sequence), 0) FROM owner_deletion_events WHERE OwnerKindCode = 1;");

    private static async Task OpenAsync(AsyncServiceScope scope)
    {
        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(Token);
        }
    }

    private static async Task<CovenantSearchHealthDto> SearchAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/memory/status", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        MemoryStatusDto status = await MemoryErasureRouteDriver.ReadDataAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemoryStatusDto);

        return Assert.IsType<CovenantStatusDto>(status.Covenant).Search;
    }

    /// <summary>The Covenant memory reset: the plan route, then the reset route at the plan it previewed.</summary>
    private static async Task ResetCovenantAsync(HttpClient client)
    {
        MemoryResetRequest request = new(MemoryResetScope.Covenant);

        DataRetentionPlan plan;

        using (HttpResponseMessage planned = await client.PostAsync(
            "/api/data/memory/reset/plan",
            JsonContent.Create(request, ArcanumJsonContext.Default.MemoryResetRequest),
            Token))
        {
            Assert.Equal(HttpStatusCode.OK, planned.StatusCode);

            plan = await MemoryErasureRouteDriver.ReadDataAsync(planned, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);
        }

        Assert.Empty(plan.Blockers);

        using HttpResponseMessage reset = await client.PostAsync(
            "/api/data/memory/reset",
            JsonContent.Create(request with { ExpectedPlanId = plan.PlanId }, ArcanumJsonContext.Default.MemoryResetRequest),
            Token);

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        DataRetentionApplyResult result = await MemoryErasureRouteDriver.ReadDataAsync(
            reset,
            ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

        Assert.True(result.Reconciled);
    }

    /// <summary>Retires the Global Confirmed head of one key through the review list, prepare and apply routes.</summary>
    private static async Task RetireByReviewAsync(HttpClient client, string key)
    {
        MemoryErasureRouteDriver driver = new(client);

        CovenantReviewPageDto page;

        using (HttpResponseMessage listed = await driver.PostAsync(
            "/api/memory/covenant/review/list",
            new CovenantReviewListRequest(CovenantScope.Global, null, CovenantLane.Confirmed, 50, null),
            ArcanumJsonContext.Default.CovenantReviewListRequest))
        {
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

            page = await MemoryErasureRouteDriver.ReadDataAsync(listed, ArcanumJsonContext.Default.ApiResponseCovenantReviewPageDto);
        }

        CovenantReviewItemDto item = Assert.Single(page.Items, candidate => candidate.IsCurrent && candidate.Key == key);

        CovenantReviewBulkPrepareRequest prepare = new(
            Guid.CreateVersion7(),
            CovenantScope.Global,
            null,
            CovenantLane.Confirmed,
            MemoryReviewAction.Retire,
            [new CovenantReviewDecision(item.ObservationToken, null)]);

        MemoryReviewBulkPlanDto plan;

        using (HttpResponseMessage prepared = await driver.PostAsync(
            "/api/memory/covenant/review/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantReviewBulkPrepareRequest))
        {
            Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);

            plan = await MemoryErasureRouteDriver.ReadDataAsync(prepared, ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto);
        }

        using HttpResponseMessage applied = await driver.PostAsync(
            "/api/memory/covenant/review/apply",
            new CovenantReviewBulkApplyRequest(prepare, plan.PreparedPlanToken),
            ArcanumJsonContext.Default.CovenantReviewBulkApplyRequest);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
    }

    private static async Task<Guid> RegisterCampaignAsync(ArcanumWebApplicationFactory host, HttpClient client, string name)
    {
        string path = Path.Combine(host.TempHome, $"covenant-republication-{name}");

        Directory.CreateDirectory(path);

        using HttpResponseMessage registered = await client.PostAsync(
            "/api/campaigns",
            JsonContent.Create(
                new RegisterCampaignRequest($"Covenant republication {name}", path, WorkspaceType.Campaign, null),
                ArcanumJsonContext.Default.RegisterCampaignRequest),
            Token);

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        return (await MemoryErasureRouteDriver.ReadDataAsync(registered, ArcanumJsonContext.Default.ApiResponseCampaignDto)).Id;
    }

    /// <summary>
    /// Publishes the installed tiers with the accelerator failed, as a schema repair that found it broken
    /// would, leaving the canonical tier and every persisted position as they were.
    /// </summary>
    private static void DegradeAccelerator(ArcanumWebApplicationFactory host)
    {
        const string Source = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

        CovenantAvailability availability = host.Services.GetRequiredService<CovenantAvailability>();

        CovenantAvailabilitySnapshot current = availability.Current;

        _ = availability.PublishSchema(
            new GrimoireSchemaInstallResult(
                new GrimoireSchemaTierInstallResult(GrimoireSchemaTransactionTier.Core, 1, GrimoireSchemaTierHealth.Healthy, Source, null, null),
                new GrimoireSchemaTierInstallResult(
                    GrimoireSchemaTransactionTier.CovenantCanonical,
                    current.CanonicalSchemaVersion!.Value,
                    GrimoireSchemaTierHealth.Healthy,
                    Source,
                    current.CanonicalInstalledFingerprint,
                    null),
                new GrimoireSchemaTierInstallResult(
                    GrimoireSchemaTransactionTier.CovenantAccelerator,
                    current.AcceleratorSchemaVersion!.Value,
                    GrimoireSchemaTierHealth.Unavailable,
                    Source,
                    current.AcceleratorInstalledFingerprint,
                    "covenant.accelerator_degraded_for_test")),
            CovenantHealthTransition.SchemaRepair);

        Assert.Equal(CovenantCapabilityState.Degraded, availability.Current.Accelerator);
    }

    private static async Task<long> ScalarAsync(ArcanumWebApplicationFactory host, string sql)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        SqliteConnection connection = await scope.ServiceProvider
            .GetRequiredService<ICovenantConnectionSource>()
            .GetOpenConnectionAsync(Token);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }
}
