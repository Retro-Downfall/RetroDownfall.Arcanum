using System.Collections.Immutable;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The terminal half of the full-installation reset Campaign cleanup: every prepared child reaching a
/// terminal phase from authenticated evidence, and the receipt whose counts have to add up.
/// </summary>
/// <remarks>
/// The same real directories, real markers, and real installed schema the preparation suite uses. A
/// reconciliation that deletes is only interesting against a marker that is actually there, and the
/// two blocked arms are only interesting if it can be proven that nothing on disk moved.
/// </remarks>
public sealed partial class CampaignPathFullInstallationResetCleanupTests
{

    [Fact]
    public async Task Reconciliation_deletes_every_opened_marker_and_completes_its_child()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot first = await harness.AddMarkedRootAsync("alpha");

        RegisteredRoot second = await harness.AddMarkedRootAsync("beta");

        PreparedOperation prepared = await harness.PrepareAsync();

        CampaignPathFullInstallationResetCleanupReceipt preparedReceipt =
            Value(await prepared.RunAsync());

        await prepared.CommitAsync();

        PreparedOperation resumed = await harness.ResumeAsync(prepared, preparedReceipt);

        Result<CampaignPathFullInstallationResetCleanupReceipt> terminal =
            await resumed.ReconcileAsync(preparedReceipt);

        Assert.True(terminal.IsSuccess, Describe(terminal));

        Assert.Equal(2ul, terminal.Value.MarkerIntentCount);

        Assert.Equal(2ul, terminal.Value.DeletedCount);

        Assert.Equal(0ul, terminal.Value.OrphanCount);

        Assert.Equal(preparedReceipt.OwnerOperationId, terminal.Value.OwnerOperationId);

        Assert.Equal(preparedReceipt.OwnerEffectDigest, terminal.Value.OwnerEffectDigest);

        // The vector is the prepared one, in the prepared order, in storage of its own. A terminal
        // receipt that reordered its own identifiers would hash to a different vector describing the
        // same children, and one that handed back the caller's array would be editable afterwards.
        Assert.Equal(
            [.. preparedReceipt.OrderedMarkerIntentIds],
            terminal.Value.OrderedMarkerIntentIds.ToArray());

        Assert.Equal(
            preparedReceipt.MarkerIntentVectorDigest,
            terminal.Value.MarkerIntentVectorDigest);

        Assert.False(File.Exists(CleanupHarness.MarkerPathOf(first)));

        Assert.False(File.Exists(CleanupHarness.MarkerPathOf(second)));

        foreach (CampaignPathFullResetCleanupChildRow child in
            await harness.ReadCommittedChildrenAsync(prepared.OwnerOperationId))
        {

            Assert.Equal(CampaignPathMarkerPhase.Completed, child.Intent.Phase);

            // Exactly one advance. Kind four has one legal transition, and a ladder that walked it
            // through the two-phase intermediate phases would be writing phases whose filesystem
            // effects this kind never performs.
            Assert.Equal(2, child.Intent.PhaseRevision);

            Assert.Null(child.Intent.PendingDisposition);

        }

    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("mismatch")]
    public async Task Reconciliation_terminalizes_a_blocked_child_without_touching_the_workspace(
        string arm)
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation prepared = await harness.PrepareAsync();

        if (arm == "unavailable")
        {

            await harness.DeleteRegistryRowAsync(root.CampaignId);

        }
        else
        {

            await harness.UpdateRevisionAsync(root.CampaignId, root.Revision + 1);

        }

        CampaignPathFullInstallationResetCleanupReceipt preparedReceipt =
            Value(await prepared.RunAsync());

        await prepared.CommitAsync();

        CampaignPathFullResetCleanupObservationCode expected = arm == "unavailable"
            ? CampaignPathFullResetCleanupObservationCode.Unavailable
            : CampaignPathFullResetCleanupObservationCode.Mismatch;

        Assert.Equal(
            expected,
            Assert.Single(await harness.ReadCommittedChildrenAsync(prepared.OwnerOperationId))
                .Evidence.ObservationCode);

        byte[] before = await File.ReadAllBytesAsync(CleanupHarness.MarkerPathOf(root), Token);

        PreparedOperation resumed = await harness.ResumeAsync(prepared, preparedReceipt);

        // A lifecycle that opened nothing and retains nothing. A blocked arm that reached for a root
        // would have to ask this opener for one, and this opener refuses.
        Result<CampaignPathFullInstallationResetCleanupReceipt> terminal =
            await resumed.ReconcileAsync(
                preparedReceipt,
                harness.CreateFreshProcessLifecycle(
                    new RecordingRecoveryKeyProvider(available: false)));

        Assert.True(terminal.IsSuccess, Describe(terminal));

        Assert.Equal(0ul, terminal.Value.DeletedCount);

        Assert.Equal(1ul, terminal.Value.OrphanCount);

        Assert.Equal(
            CampaignPathMarkerPhase.ManualBlocker,
            Assert.Single(await harness.ReadCommittedChildrenAsync(prepared.OwnerOperationId))
                .Intent.Phase);

        // The whole point of a typed orphan: the marker is still exactly where it was, and still
        // exactly what it was, for an operator to deal with by hand.
        Assert.Equal(before, await File.ReadAllBytesAsync(CleanupHarness.MarkerPathOf(root), Token));

    }

    [Fact]
    public async Task Reconciliation_blocks_an_opened_child_whose_retained_root_was_lost()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation prepared = await harness.PrepareAsync();

        CampaignPathFullInstallationResetCleanupReceipt preparedReceipt =
            Value(await prepared.RunAsync());

        await prepared.CommitAsync();

        byte[] before = await File.ReadAllBytesAsync(CleanupHarness.MarkerPathOf(root), Token);

        PreparedOperation resumed = await harness.ResumeAsync(prepared, preparedReceipt);

        // A fresh process retained nothing. Reconciliation deletes only through a root this process
        // opened and held; reopening one here would be resolving a display path a second time, which
        // is the one thing the marker protocol exists to avoid.
        Result<CampaignPathFullInstallationResetCleanupReceipt> terminal =
            await resumed.ReconcileAsync(
                preparedReceipt,
                harness.CreateFreshProcessLifecycle());

        Assert.True(terminal.IsSuccess, Describe(terminal));

        Assert.Equal(0ul, terminal.Value.DeletedCount);

        Assert.Equal(1ul, terminal.Value.OrphanCount);

        Assert.Equal(
            CampaignPathMarkerPhase.ManualBlocker,
            Assert.Single(await harness.ReadCommittedChildrenAsync(prepared.OwnerOperationId))
                .Intent.Phase);

        Assert.Equal(before, await File.ReadAllBytesAsync(CleanupHarness.MarkerPathOf(root), Token));

    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("ownership")]
    public async Task Reconciliation_blocks_an_opened_child_whose_marker_changed(string change)
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation prepared = await harness.PrepareAsync();

        CampaignPathFullInstallationResetCleanupReceipt preparedReceipt =
            Value(await prepared.RunAsync());

        await prepared.CommitAsync();

        if (change == "bytes")
        {

            // Still a well-formed marker for the same Campaign, so only the committed digest can
            // tell it apart from the one preparation observed.
            await harness.ReplaceMarkerAsync(
                root,
                root.CampaignId,
                root.Revision,
                root.RootVolumeId,
                root.RootFileId);

        }
        else
        {

            // A marker that no longer binds the root it sits in. The bytes parse, the Campaign
            // agrees, and the same-handle ownership digest is the only thing that disagrees.
            await harness.ReplaceMarkerAsync(
                root,
                root.CampaignId,
                root.Revision,
                root.RootVolumeId + 1,
                root.RootFileId + 1);

        }

        PreparedOperation resumed = await harness.ResumeAsync(prepared, preparedReceipt);

        Result<CampaignPathFullInstallationResetCleanupReceipt> terminal =
            await resumed.ReconcileAsync(preparedReceipt);

        Assert.True(terminal.IsSuccess, Describe(terminal));

        Assert.Equal(0ul, terminal.Value.DeletedCount);

        Assert.Equal(1ul, terminal.Value.OrphanCount);

        Assert.Equal(
            CampaignPathMarkerPhase.ManualBlocker,
            Assert.Single(await harness.ReadCommittedChildrenAsync(prepared.OwnerOperationId))
                .Intent.Phase);

        Assert.True(File.Exists(CleanupHarness.MarkerPathOf(root)));

    }

    [Fact]
    public async Task Reconciliation_completes_an_opened_child_whose_marker_is_already_absent()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation prepared = await harness.PrepareAsync();

        CampaignPathFullInstallationResetCleanupReceipt preparedReceipt =
            Value(await prepared.RunAsync());

        await prepared.CommitAsync();

        // A crash between the compare-delete and the phase advance. Nothing but this operation holds
        // authority over that directory, so the absence is this operation's own completed delete.
        File.Delete(CleanupHarness.MarkerPathOf(root));

        PreparedOperation resumed = await harness.ResumeAsync(prepared, preparedReceipt);

        Result<CampaignPathFullInstallationResetCleanupReceipt> terminal =
            await resumed.ReconcileAsync(preparedReceipt);

        Assert.True(terminal.IsSuccess, Describe(terminal));

        Assert.Equal(1ul, terminal.Value.DeletedCount);

        Assert.Equal(0ul, terminal.Value.OrphanCount);

        Assert.Equal(
            CampaignPathMarkerPhase.Completed,
            Assert.Single(await harness.ReadCommittedChildrenAsync(prepared.OwnerOperationId))
                .Intent.Phase);

    }

    [Fact]
    public async Task Reconciliation_counts_deleted_and_orphaned_children_to_the_intent_count()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot deleted = await harness.AddMarkedRootAsync("alpha");

        RegisteredRoot orphaned = await harness.AddMarkedRootAsync("beta");

        RegisteredRoot alsoOrphaned = await harness.AddMarkedRootAsync("gamma");

        PreparedOperation prepared = await harness.PrepareAsync();

        await harness.DeleteRegistryRowAsync(orphaned.CampaignId);

        await harness.UpdateRevisionAsync(alsoOrphaned.CampaignId, alsoOrphaned.Revision + 1);

        CampaignPathFullInstallationResetCleanupReceipt preparedReceipt =
            Value(await prepared.RunAsync());

        await prepared.CommitAsync();

        PreparedOperation resumed = await harness.ResumeAsync(prepared, preparedReceipt);

        Result<CampaignPathFullInstallationResetCleanupReceipt> terminal =
            await resumed.ReconcileAsync(preparedReceipt);

        Assert.True(terminal.IsSuccess, Describe(terminal));

        Assert.Equal(3ul, terminal.Value.MarkerIntentCount);

        Assert.Equal(1ul, terminal.Value.DeletedCount);

        Assert.Equal(2ul, terminal.Value.OrphanCount);

        Assert.Equal(
            terminal.Value.MarkerIntentCount,
            terminal.Value.DeletedCount + terminal.Value.OrphanCount);

        Assert.False(File.Exists(CleanupHarness.MarkerPathOf(deleted)));

        Assert.True(File.Exists(CleanupHarness.MarkerPathOf(orphaned)));

        Assert.True(File.Exists(CleanupHarness.MarkerPathOf(alsoOrphaned)));

    }

    [Fact]
    public async Task Reconciliation_of_a_proven_empty_vector_returns_the_same_frozen_receipt()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        PreparedOperation prepared = await harness.PrepareAsync();

        CampaignPathFullInstallationResetCleanupReceipt preparedReceipt =
            Value(await prepared.RunAsync());

        await prepared.CommitAsync();

        PreparedOperation resumed = await harness.ResumeAsync(prepared, preparedReceipt);

        Result<CampaignPathFullInstallationResetCleanupReceipt> terminal =
            await resumed.ReconcileAsync(
                preparedReceipt,
                harness.CreateFreshProcessLifecycle(
                    new RecordingRecoveryKeyProvider(available: false)));

        Assert.True(terminal.IsSuccess, Describe(terminal));

        Assert.Equal(0ul, terminal.Value.MarkerIntentCount);

        Assert.Equal(0ul, terminal.Value.DeletedCount);

        Assert.Equal(0ul, terminal.Value.OrphanCount);

        // Identical to the prepared receipt, which is what lets the coordinator publish nothing.
        Assert.True(CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
            preparedReceipt,
            terminal.Value));

    }

    [Fact]
    public async Task Reconciliation_replays_a_terminal_vector_without_advancing_anything()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot deleted = await harness.AddMarkedRootAsync("alpha");

        RegisteredRoot orphaned = await harness.AddMarkedRootAsync("beta");

        PreparedOperation prepared = await harness.PrepareAsync();

        await harness.DeleteRegistryRowAsync(orphaned.CampaignId);

        CampaignPathFullInstallationResetCleanupReceipt preparedReceipt =
            Value(await prepared.RunAsync());

        await prepared.CommitAsync();

        PreparedOperation resumed = await harness.ResumeAsync(prepared, preparedReceipt);

        CampaignPathFullInstallationResetCleanupReceipt terminal =
            Value(await resumed.ReconcileAsync(preparedReceipt));

        IReadOnlyList<CampaignPathFullResetCleanupChildRow> afterFirst =
            await harness.ReadCommittedChildrenAsync(prepared.OwnerOperationId);

        // The retry authenticates against the terminal receipt the first pass produced, exactly as a
        // restarted coordinator would after publishing it.
        PreparedOperation retried = await harness.ResumeAsync(prepared, terminal);

        Result<CampaignPathFullInstallationResetCleanupReceipt> replayed =
            await retried.ReconcileAsync(
                terminal,
                harness.CreateFreshProcessLifecycle(
                    new RecordingRecoveryKeyProvider(available: false)));

        Assert.True(replayed.IsSuccess, Describe(replayed));

        Assert.True(CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
            terminal,
            replayed.Value));

        IReadOnlyList<CampaignPathFullResetCleanupChildRow> afterRetry =
            await harness.ReadCommittedChildrenAsync(prepared.OwnerOperationId);

        Assert.Equal(
            [.. afterFirst.Select(static child => child.Intent.PhaseRevision)],
            afterRetry.Select(static child => child.Intent.PhaseRevision));

        Assert.False(File.Exists(CleanupHarness.MarkerPathOf(deleted)));

        Assert.True(File.Exists(CleanupHarness.MarkerPathOf(orphaned)));

    }

    [Fact]
    public async Task Reconciliation_refuses_the_authority_the_prepared_receipt_superseded()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation prepared = await harness.PrepareAsync();

        CampaignPathFullInstallationResetCleanupReceipt preparedReceipt =
            Value(await prepared.RunAsync());

        await prepared.CommitAsync();

        // The authority that committed the children was minted against the publication that had no
        // receipt at all. Publishing the receipt superseded it, and the stale one may not drive an
        // effect from a journal revision that has since moved.
        Result<CampaignPathFullInstallationResetCleanupReceipt> stale =
            await harness.Lifecycle.ReconcileFullInstallationResetCleanupAsync(
                preparedReceipt,
                prepared.Authority,
                harness.Connection,
                Token);

        Assert.True(stale.IsFailure);

        Assert.Equal(
            CampaignPathMarkerPhase.Prepared,
            Assert.Single(await harness.ReadCommittedChildrenAsync(prepared.OwnerOperationId))
                .Intent.Phase);

        Assert.True(File.Exists(CleanupHarness.MarkerPathOf(root)));

        PreparedOperation resumed = await harness.ResumeAsync(prepared, preparedReceipt);

        Result<CampaignPathFullInstallationResetCleanupReceipt> fresh =
            await resumed.ReconcileAsync(preparedReceipt);

        Assert.True(fresh.IsSuccess, Describe(fresh));

        Assert.Equal(1ul, fresh.Value.DeletedCount);

    }

    [Fact]
    public async Task Reconciliation_refuses_a_receipt_the_journal_does_not_hold()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation prepared = await harness.PrepareAsync();

        CampaignPathFullInstallationResetCleanupReceipt preparedReceipt =
            Value(await prepared.RunAsync());

        await prepared.CommitAsync();

        ImmutableArray<Guid> fabricated = [Guid.NewGuid()];

        CampaignPathFullInstallationResetCleanupReceipt substituted = Value(
            CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
                preparedReceipt.OwnerOperationId,
                preparedReceipt.OwnerEffectDigest,
                fabricated,
                Value(FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(
                    fabricated))));

        // Authenticated, well-formed, and naming a child nothing ever journaled. The publication is
        // the only reason it authenticates, and the journal is what refuses it.
        PreparedOperation resumed = await harness.ResumeAsync(prepared, substituted);

        Result<CampaignPathFullInstallationResetCleanupReceipt> terminal =
            await resumed.ReconcileAsync(substituted);

        Assert.True(terminal.IsFailure);

        Assert.Equal(
            CampaignPathMarkerPhase.Prepared,
            Assert.Single(await harness.ReadCommittedChildrenAsync(prepared.OwnerOperationId))
                .Intent.Phase);

        Assert.True(File.Exists(CleanupHarness.MarkerPathOf(root)));

    }

    [Fact]
    public async Task Reconciliation_returns_a_bounded_immutable_deep_copied_terminal_vector()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        _ = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation prepared = await harness.PrepareAsync();

        CampaignPathFullInstallationResetCleanupReceipt preparedReceipt =
            Value(await prepared.RunAsync());

        await prepared.CommitAsync();

        PreparedOperation resumed = await harness.ResumeAsync(prepared, preparedReceipt);

        CampaignPathFullInstallationResetCleanupReceipt terminal =
            Value(await resumed.ReconcileAsync(preparedReceipt));

        Assert.False(terminal.OrderedMarkerIntentIds.IsDefault);

        Assert.All(
            terminal.OrderedMarkerIntentIds,
            static intentId => Assert.NotEqual(Guid.Empty, intentId));

        Assert.True(terminal.OwnerEffectDigest.IsValid);

        Assert.True(terminal.MarkerIntentVectorDigest.IsValid);

        Assert.True(
            terminal.OrderedMarkerIntentIds.Length
                <= HostToolsMarkerPairResetCheckpointBounds.MaximumVectorCount);

        // Not the caller's array and not the caller's digest buffer. A receipt that handed back the
        // storage it was built from would let whoever holds it edit an authenticated vector.
        Assert.NotSame(
            preparedReceipt.OwnerEffectDigest.Bytes,
            terminal.OwnerEffectDigest.Bytes);

        Assert.Equal(
            Value(FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(
                terminal.OrderedMarkerIntentIds)),
            terminal.MarkerIntentVectorDigest);

    }

    [Fact]
    public async Task Reconciliation_borrows_its_caller_connection_and_leaves_it_open()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        _ = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation prepared = await harness.PrepareAsync();

        CampaignPathFullInstallationResetCleanupReceipt preparedReceipt =
            Value(await prepared.RunAsync());

        await prepared.CommitAsync();

        PreparedOperation resumed = await harness.ResumeAsync(prepared, preparedReceipt);

        _ = Value(await resumed.ReconcileAsync(preparedReceipt));

        // The coordinator holds this one non-pooled connection for the whole operation, and still
        // has effects of its own to run on it afterwards.
        Assert.Equal(System.Data.ConnectionState.Open, harness.Connection.State);

        await using SqliteCommand command = harness.Connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM campaign_path_marker_intents;";

        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync(Token), provider: null));

    }

}
