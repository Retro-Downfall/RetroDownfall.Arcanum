using System.Buffers.Text;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;

using FullInstallationResetMarkerCleanupAuthority =
    RetroDownfall.Arcanum.Infrastructure.InstallationReset.HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The kind-four children a full installation reset journals for every authenticated Campaign, and
/// the replay that proves an interrupted attempt already journaled exactly those.
/// </summary>
/// <remarks>
/// These run against real directories, real markers, and the real installed schema, because the
/// behaviours under test are the ones a fake cannot have: a marker that no longer proves its root, a
/// registration that vanished, a guard trigger that refuses a companion whose parent is the wrong
/// kind. A suite built on doubles here would be asserting that its own doubles agree with each other.
/// </remarks>
[Trait("Category", "Integration")]
public sealed partial class CampaignPathFullInstallationResetCleanupTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Preparation_inserts_one_distinct_kind_four_child_per_authenticated_campaign()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot first = await harness.AddMarkedRootAsync("alpha");

        RegisteredRoot second = await harness.AddMarkedRootAsync("beta");

        PreparedOperation operation = await harness.PrepareAsync();

        Result<CampaignPathFullInstallationResetCleanupReceipt> receipt =
            await operation.RunAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(2ul, receipt.Value.MarkerIntentCount);

        Assert.Equal(operation.OwnerOperationId, receipt.Value.OwnerOperationId);

        Assert.Equal(operation.OwnerEffectDigest, receipt.Value.OwnerEffectDigest);

        Assert.Equal(0ul, receipt.Value.DeletedCount);

        Assert.Equal(0ul, receipt.Value.OrphanCount);

        Assert.Equal(
            FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(
                receipt.Value.OrderedMarkerIntentIds).Value,
            receipt.Value.MarkerIntentVectorDigest);

        IReadOnlyList<CampaignPathFullResetCleanupChildRow> children =
            await operation.ReadChildrenAsync();

        Assert.Equal(2, children.Count);

        Assert.Equal(2, receipt.Value.OrderedMarkerIntentIds.Distinct().Count());

        // Authenticated Campaign order, not random-identifier order. The vector digest is taken over
        // this sequence, so a receipt sorted by the identifiers it just minted would be a different
        // vector describing the same children.
        Assert.Equal(
            [.. operation.Inventory.Entries.Select(static entry => entry.CampaignId)],
            receipt.Value.OrderedMarkerIntentIds.Select(
                id => children.Single(child => child.Intent.IntentId == id).Intent.CampaignId));

        foreach (CampaignPathFullResetCleanupChildRow child in children)
        {

            Assert.Equal(operation.OwnerOperationId, child.Intent.OwnerOperationId);

            Assert.Equal(operation.OwnerEffectDigest, child.Intent.OwnerEffectDigest);

            Assert.Equal(
                CampaignPathMarkerIntentKind.FullInstallationResetCleanup,
                child.Intent.Kind);

            Assert.Null(child.Intent.ExclusiveOwnerOperation);

            Assert.Null(child.Intent.PendingDisposition);

            Assert.Equal(CampaignPathMarkerPhase.Prepared, child.Intent.Phase);

            Assert.Equal(1, child.Intent.PhaseRevision);

            Assert.Equal(
                CampaignPathFullResetCleanupObservationCode.Opened,
                child.Evidence.ObservationCode);

        }

        // Keyed by Campaign rather than by any sort: the vector's order is the authenticated
        // inventory's, which orders by RFC 4122 bytes and not by anything a test can assume.
        foreach (RegisteredRoot root in new[] { first, second })
        {

            Assert.Equal(
                root.DisplayPath,
                children.Single(child => child.Intent.CampaignId == root.CampaignId)
                    .Intent.TargetDisplayPath);

        }

    }

    /// <summary>
    /// The three closed arms, in one vector, from three real states of the workspace.
    /// </summary>
    [Fact]
    public async Task Preparation_records_the_opened_unavailable_and_mismatch_observation_shapes()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot intact = await harness.AddMarkedRootAsync("intact");

        RegisteredRoot vanished = await harness.AddMarkedRootAsync("vanished");

        RegisteredRoot moved = await harness.AddMarkedRootAsync("moved");

        PreparedOperation operation = await harness.PrepareAsync();

        // Both changes land after the authenticated inventory was frozen, which is exactly the
        // window this evidence exists to describe.
        await harness.DeleteRegistryRowAsync(vanished.CampaignId);

        await harness.UpdateRevisionAsync(moved.CampaignId, moved.Revision + 1);

        Result<CampaignPathFullInstallationResetCleanupReceipt> receipt =
            await operation.RunAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Dictionary<Guid, CampaignPathFullResetCleanupChildRow> children =
            (await operation.ReadChildrenAsync()).ToDictionary(
                static child => child.Intent.CampaignId);

        Assert.Equal(3, children.Count);

        AssertOpened(children[intact.CampaignId], intact);

        AssertBlocked(
            children[vanished.CampaignId],
            CampaignPathFullResetCleanupObservationCode.Unavailable);

        AssertBlocked(
            children[moved.CampaignId],
            CampaignPathFullResetCleanupObservationCode.Mismatch);

    }

    /// <summary>
    /// A marker that no longer proves the root it sits in is a mismatch, not an opened child.
    /// </summary>
    /// <remarks>
    /// The registry still names the same directory at the same revision with the same identity, so
    /// everything short of the same-handle ownership comparison agrees. Only re-observing the marker
    /// through the handle separates this from an intact Campaign.
    /// </remarks>
    [Fact]
    public async Task A_marker_that_no_longer_proves_its_root_is_a_mismatch_child_with_no_path()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync("swapped");

        PreparedOperation operation = await harness.PrepareAsync();

        await harness.ReplaceMarkerAsync(
            root,
            root.CampaignId,
            root.Revision,
            root.RootVolumeId,
            root.RootFileId + 1);

        // A restarted process retained nothing, so the marker is re-read rather than remembered.
        Result<CampaignPathFullInstallationResetCleanupReceipt> receipt =
            await operation.RunAsync(harness.CreateFreshProcessLifecycle());

        Assert.True(receipt.IsSuccess, Describe(receipt));

        AssertBlocked(
            Assert.Single(await operation.ReadChildrenAsync()),
            CampaignPathFullResetCleanupObservationCode.Mismatch);

    }

    [Fact]
    public async Task Preparation_borrows_and_never_begins_commits_rolls_back_or_disposes_the_callers_transaction()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        _ = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation operation = await harness.PrepareAsync();

        Result<CampaignPathFullInstallationResetCleanupReceipt> receipt =
            await operation.RunAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        // Still the caller's to use: a seam that had committed or rolled back would leave this
        // transaction unusable, and one that had disposed it would throw here.
        Assert.Equal(1L, await operation.CountChildrenInTransactionAsync());

        await operation.RollbackAsync();

        Assert.Equal(0L, await harness.CountCommittedChildrenAsync());

    }

    [Fact]
    public async Task Parent_and_companion_insert_atomically_in_the_same_caller_transaction()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        _ = await harness.AddMarkedRootAsync("alpha");

        _ = await harness.AddMarkedRootAsync("beta");

        PreparedOperation rolledBack = await harness.PrepareAsync();

        Assert.True((await rolledBack.RunAsync()).IsSuccess);

        await rolledBack.RollbackAsync();

        Assert.Equal(0L, await harness.CountCommittedChildrenAsync());

        Assert.Equal(0L, await harness.CountCommittedEvidenceAsync());

        PreparedOperation committed = await harness.PrepareAsync();

        Assert.True((await committed.RunAsync()).IsSuccess);

        await committed.CommitAsync();

        Assert.Equal(2L, await harness.CountCommittedChildrenAsync());

        Assert.Equal(2L, await harness.CountCommittedEvidenceAsync());

    }

    [Fact]
    public async Task Replay_returns_the_same_children_when_parent_and_every_companion_field_match()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        _ = await harness.AddMarkedRootAsync("alpha");

        _ = await harness.AddMarkedRootAsync("beta");

        PreparedOperation first = await harness.PrepareAsync();

        Result<CampaignPathFullInstallationResetCleanupReceipt> prepared = await first.RunAsync();

        Assert.True(prepared.IsSuccess, Describe(prepared));

        await first.CommitAsync();

        // Second attempt, same owner, with the receipt the checkpoint published.
        PreparedOperation replay = await harness.ResumeAsync(first, prepared.Value);

        Result<CampaignPathFullInstallationResetCleanupReceipt> replayed = await replay.RunAsync();

        Assert.True(replayed.IsSuccess, Describe(replayed));

        Assert.True(CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
            prepared.Value,
            replayed.Value));

        await replay.RollbackAsync();

        // Nothing new was written and nothing was substituted.
        Assert.Equal(2L, await harness.CountCommittedChildrenAsync());

        Assert.Equal(2L, await harness.CountCommittedEvidenceAsync());

    }

    [Fact]
    public async Task Replay_rejects_an_expected_receipt_whose_ordered_identifiers_are_reordered()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        _ = await harness.AddMarkedRootAsync("alpha");

        _ = await harness.AddMarkedRootAsync("beta");

        PreparedOperation first = await harness.PrepareAsync();

        Result<CampaignPathFullInstallationResetCleanupReceipt> prepared = await first.RunAsync();

        Assert.True(prepared.IsSuccess, Describe(prepared));

        await first.CommitAsync();

        CampaignPathFullInstallationResetCleanupReceipt reordered = Value(
            CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
                prepared.Value.OwnerOperationId,
                prepared.Value.OwnerEffectDigest,
                [.. prepared.Value.OrderedMarkerIntentIds.Reverse()],
                Value(FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(
                    [.. prepared.Value.OrderedMarkerIntentIds.Reverse()]))));

        PreparedOperation replay = await harness.ResumeAsync(first, reordered);

        AssertRefused(await replay.RunAsync());

        await replay.RollbackAsync();

    }

    /// <summary>
    /// The same number of children, for a different set of Campaigns, is not a replay.
    /// </summary>
    [Fact]
    public async Task Replay_rejects_a_same_count_campaign_replacement()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot replaced = await harness.AddMarkedRootAsync("alpha");

        _ = await harness.AddMarkedRootAsync("beta");

        PreparedOperation first = await harness.PrepareAsync();

        Assert.True((await first.RunAsync()).IsSuccess);

        await first.CommitAsync();

        // One Campaign leaves the registry and another arrives, leaving the count intact. A replay
        // that counted rather than compared would accept this as the vector it already journaled.
        await harness.DeleteRegistryRowAsync(replaced.CampaignId);

        _ = await harness.AddMarkedRootAsync("gamma");

        // A resumed process: it retained nothing, so it re-observes the registry as it now stands.
        CampaignPathMarkerLifecycle resumed = harness.CreateFreshProcessLifecycle();

        PreparedOperation replay = await harness.PrepareAsync(
            first.OwnerOperationId,
            resumed);

        AssertRefused(await replay.RunAsync(resumed));

        await replay.RollbackAsync();

    }

    /// <summary>
    /// A journal whose children describe a different observation than this preparation would make is
    /// refused rather than adopted.
    /// </summary>
    /// <remarks>
    /// The evidence itself is immutable and the schema refuses every rewrite, so the substitution has
    /// to arrive the only way it can: as a second authenticated inventory that disagrees with the
    /// committed children. That is also the realistic shape — a resumed attempt reading a workspace
    /// that moved underneath the one it journaled.
    /// </remarks>
    [Fact]
    public async Task Replay_rejects_children_whose_evidence_disagrees_with_the_authenticated_inventory()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation first = await harness.PrepareAsync();

        Assert.True((await first.RunAsync()).IsSuccess);

        await first.CommitAsync();

        // The same directory under a new name: same inode, same marker, same revision, and a
        // different canonical display path. Only the committed display-path digest separates the
        // authenticated inventory a second attempt builds from the evidence the first committed.
        await harness.MoveRootAsync(root, "alpha-moved");

        PreparedOperation replay = await harness.PrepareAsync(
            first.OwnerOperationId,
            harness.CreateFreshProcessLifecycle());

        AssertRefused(await replay.RunAsync());

        await replay.RollbackAsync();

    }

    [Fact]
    public async Task Zero_campaign_preparation_returns_the_frozen_empty_receipt_without_any_dml()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        PreparedOperation operation = await harness.PrepareAsync();

        Result<CampaignPathFullInstallationResetCleanupReceipt> receipt =
            await operation.RunAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(0ul, receipt.Value.MarkerIntentCount);

        Assert.Empty(receipt.Value.OrderedMarkerIntentIds);

        Assert.Equal(
            Value(FullInstallationResetMarkerPairResetDigests.FullResetIntentVector([])),
            receipt.Value.MarkerIntentVectorDigest);

        Assert.Equal(0L, await operation.CountChildrenInTransactionAsync());

        await operation.RollbackAsync();

        // Idempotent by construction: a resumed attempt whose checkpoint already carries the empty
        // receipt reaches the same frozen vector, and still runs inside a caller-owned transaction.
        PreparedOperation replay = await harness.ResumeAsync(operation, receipt.Value);

        Result<CampaignPathFullInstallationResetCleanupReceipt> again = await replay.RunAsync();

        Assert.True(again.IsSuccess, Describe(again));

        Assert.True(CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
            receipt.Value,
            again.Value));

        await replay.RollbackAsync();

    }

    /// <summary>
    /// A receipt the checkpoint never published is not the caller's to assert.
    /// </summary>
    /// <remarks>
    /// The authority compares the caller's expected receipt with the authenticated one before
    /// anything is read, so a caller holding a receipt from somewhere else is refused rather than
    /// having its vector compared against it.
    /// </remarks>
    [Fact]
    public async Task An_expected_receipt_the_checkpoint_never_published_is_refused()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        PreparedOperation operation = await harness.PrepareAsync();

        Result<CampaignPathFullInstallationResetCleanupReceipt> receipt =
            await operation.RunAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        AssertRefused(await operation.RunAsync(expectedReceipt: receipt.Value));

        await operation.RollbackAsync();

    }

    /// <summary>
    /// The authority is revalidated before anything is read or written.
    /// </summary>
    [Fact]
    public async Task Preparation_requires_current_cleanup_authority_before_read_or_write()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        _ = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation operation = await harness.PrepareAsync();

        // The authenticated record moves on underneath the authority this preparation holds.
        harness.AdvancePublishedPhase(HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted);

        AssertRefused(await operation.RunAsync());

        Assert.Equal(0L, await operation.CountChildrenInTransactionAsync());

        await operation.RollbackAsync();

    }

    /// <summary>
    /// A caller whose preparation does not match the authenticated checkpoint writes nothing.
    /// </summary>
    [Fact]
    public async Task Preparation_refuses_an_owner_effect_the_checkpoint_does_not_name()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        _ = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation operation = await harness.PrepareAsync();

        CampaignPathFullInstallationResetCleanupPreparation substituted = Value(
            CampaignPathFullInstallationResetCleanupPreparation.Create(
                operation.OwnerOperationId,
                new CovenantDigest([.. Enumerable.Repeat((byte)0x5C, 32)]),
                operation.Inventory));

        AssertRefused(await operation.RunAsync(preparation: substituted));

        Assert.Equal(0L, await operation.CountChildrenInTransactionAsync());

        await operation.RollbackAsync();

    }

    /// <summary>
    /// A process that retained no root must prove the identity key before it touches the workspace.
    /// </summary>
    /// <remarks>
    /// The absence of any child is what makes this an ordering assertion rather than a result
    /// assertion. Had the reopen been attempted and failed, this Campaign would have become a
    /// blocked child and the vector would have been journaled anyway; a refusal with an empty
    /// journal can only mean nothing was opened at all.
    /// </remarks>
    [Fact]
    public async Task Fresh_process_preparation_without_the_root_identity_key_opens_nothing_and_journals_nothing()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        _ = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation operation = await harness.PrepareAsync();

        RecordingRecoveryKeyProvider keys = new(available: false);

        AssertRefused(await operation.RunAsync(harness.CreateFreshProcessLifecycle(keys)));

        Assert.Equal(1, keys.Calls);

        Assert.Equal(0L, await operation.CountChildrenInTransactionAsync());

        await operation.RollbackAsync();

    }

    /// <summary>
    /// Replaying still-prepared opened children on a fresh process needs the same key first.
    /// </summary>
    [Fact]
    public async Task Fresh_process_replay_of_prepared_opened_children_without_the_key_is_refused()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        _ = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation first = await harness.PrepareAsync();

        Result<CampaignPathFullInstallationResetCleanupReceipt> prepared = await first.RunAsync();

        Assert.True(prepared.IsSuccess, Describe(prepared));

        await first.CommitAsync();

        RecordingRecoveryKeyProvider keys = new(available: false);

        PreparedOperation replay = await harness.ResumeAsync(first, prepared.Value);

        AssertRefused(await replay.RunAsync(harness.CreateFreshProcessLifecycle(keys)));

        Assert.Equal(1, keys.Calls);

        await replay.RollbackAsync();

    }

    /// <summary>
    /// A vector of blocked children asks nothing of the credential store or the filesystem.
    /// </summary>
    /// <remarks>
    /// A blocked child has no root to reopen — that is what "blocked" records — so a replay that
    /// still demanded the identity key would be asking for authority it has no use for, and would
    /// turn an unavailable keychain into a reason a finished observation cannot be read back.
    /// </remarks>
    [Fact]
    public async Task Replay_of_blocked_children_needs_no_root_identity_key_at_all()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot vanishing = await harness.AddMarkedRootAsync("vanishing");

        PreparedOperation first = await harness.PrepareAsync();

        await harness.DeleteRegistryRowAsync(vanishing.CampaignId);

        Result<CampaignPathFullInstallationResetCleanupReceipt> prepared = await first.RunAsync();

        Assert.True(prepared.IsSuccess, Describe(prepared));

        AssertBlocked(
            Assert.Single(await first.ReadChildrenAsync()),
            CampaignPathFullResetCleanupObservationCode.Unavailable);

        await first.CommitAsync();

        RecordingRecoveryKeyProvider keys = new(available: false);

        PreparedOperation replay = await harness.ResumeAsync(first, prepared.Value);

        Result<CampaignPathFullInstallationResetCleanupReceipt> replayed =
            await replay.RunAsync(harness.CreateFreshProcessLifecycle(keys));

        Assert.True(replayed.IsSuccess, Describe(replayed));

        Assert.Equal(0, keys.Calls);

        await replay.RollbackAsync();

    }

    /// <summary>
    /// A published receipt with no children behind it is a contradiction, not a first attempt.
    /// </summary>
    [Fact]
    public async Task A_published_receipt_with_an_empty_journal_is_refused_rather_than_rewritten()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        _ = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation first = await harness.PrepareAsync();

        Result<CampaignPathFullInstallationResetCleanupReceipt> prepared = await first.RunAsync();

        Assert.True(prepared.IsSuccess, Describe(prepared));

        // The receipt is published, but the transaction that held the children never committed.
        await first.RollbackAsync();

        PreparedOperation replay = await harness.ResumeAsync(first, prepared.Value);

        AssertRefused(await replay.RunAsync());

        Assert.Equal(0L, await replay.CountChildrenInTransactionAsync());

        await replay.RollbackAsync();

    }

    /// <summary>
    /// The joined read is positional over nineteen columns, and a transposition between two
    /// same-width digest columns would be invisible to any assertion that did not vary them.
    /// </summary>
    [Fact]
    public async Task The_joined_read_projects_every_distinct_digest_column_to_its_own_field()
    {

        await using CleanupHarness harness = await CleanupHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync("alpha");

        PreparedOperation operation = await harness.PrepareAsync();

        Assert.True((await operation.RunAsync()).IsSuccess);

        CampaignPathFullResetCleanupChildRow child =
            Assert.Single(await operation.ReadChildrenAsync());

        CampaignMarkerInventoryEntryV1 entry = Assert.Single(operation.Inventory.Entries);

        CovenantDigest[] distinct =
        [
            child.Intent.OwnerEffectDigest,
            child.Intent.MarkerDigest,
            child.Evidence.CampaignInventoryEntryDigest,
            child.Evidence.IndexedPhysicalIdentityDigest,
            child.Evidence.CanonicalDisplayPathDigest,
            child.Evidence.ObservationDigest,
        ];

        Assert.Equal(distinct.Length, distinct.Distinct().Count());

        Assert.Equal(operation.OwnerEffectDigest, child.Intent.OwnerEffectDigest);

        Assert.Equal(entry.MarkerDigest, child.Intent.MarkerDigest);

        Assert.Equal(root.IdentityDigest, child.Evidence.IndexedPhysicalIdentityDigest);

        Assert.Equal(
            Value(FullInstallationResetMarkerPairResetDigests.CampaignInventoryEntry(entry)),
            child.Evidence.CampaignInventoryEntryDigest);

        Assert.Equal(
            Value(FullInstallationResetMarkerPairResetDigests.CampaignDisplayPath(
                root.DisplayPath)),
            child.Evidence.CanonicalDisplayPathDigest);

        Assert.Equal(
            entry.SameHandleOwnershipEvidenceDigest,
            child.Evidence.SameHandleOwnershipEvidenceDigest);

        Assert.Equal(
            child.Evidence.SameHandleOwnershipEvidenceDigest,
            child.Evidence.OpenedSameHandleOwnershipEvidenceDigest);

        Assert.Equal(
            Value(FullInstallationResetMarkerPairResetDigests.CampaignObservation(
                CampaignPathFullResetCleanupObservationCode.Opened,
                child.Evidence.CampaignInventoryEntryDigest,
                child.Evidence.SameHandleOwnershipEvidenceDigest)),
            child.Evidence.ObservationDigest);

        await operation.RollbackAsync();

    }

    private static void AssertOpened(
        CampaignPathFullResetCleanupChildRow child,
        RegisteredRoot root)
    {

        Assert.Equal(
            CampaignPathFullResetCleanupObservationCode.Opened,
            child.Evidence.ObservationCode);

        Assert.Equal(root.DisplayPath, child.Intent.TargetDisplayPath);

        Assert.Equal(
            child.Evidence.SameHandleOwnershipEvidenceDigest,
            child.Evidence.OpenedSameHandleOwnershipEvidenceDigest);

    }

    private static void AssertBlocked(
        CampaignPathFullResetCleanupChildRow child,
        CampaignPathFullResetCleanupObservationCode expected)
    {

        Assert.Equal(expected, child.Evidence.ObservationCode);

        Assert.Null(child.Intent.TargetDisplayPath);

        Assert.Null(child.Evidence.OpenedSameHandleOwnershipEvidenceDigest);

        Assert.Equal(CampaignPathMarkerPhase.Prepared, child.Intent.Phase);

    }

    private static void AssertRefused<T>(Result<T> result)
    {

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

        Assert.Equal(
            "The full-installation reset Campaign cleanup is not available.",
            result.Error.Message);

    }

    private static string Describe<T>(Result<T> result) =>
        result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : string.Empty;

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, Describe(result));

        return result.Value;

    }

    private static CovenantDigest Digest(byte fill) =>
        new([.. Enumerable.Repeat(fill, 32)]);

    private sealed record RegisteredRoot(
        Guid CampaignId,
        long Revision,
        string DisplayPath,
        CovenantDigest IdentityDigest,
        CovenantDigest MarkerDigest,
        ulong RootVolumeId,
        ulong RootFileId);

    /// <summary>
    /// One authenticated operation, with the authority and the caller-owned transaction the seam
    /// expects to be handed.
    /// </summary>
    private sealed class PreparedOperation
    {

        private readonly CleanupHarness _harness;

        internal PreparedOperation(
            CleanupHarness harness,
            Guid ownerOperationId,
            CovenantDigest ownerEffectDigest,
            CampaignPathFullInstallationResetInventory inventory,
            InstallationResetActivePublication publication,
            SqliteTransaction transaction,
            CampaignPathFullInstallationResetCleanupReceipt? publishedReceipt,
            FullInstallationResetMarkerCleanupAuthority authority)
        {

            _harness = harness;

            Authority = authority;

            OwnerOperationId = ownerOperationId;

            OwnerEffectDigest = ownerEffectDigest;

            Inventory = inventory;

            Publication = publication;

            Transaction = transaction;

            PublishedReceipt = publishedReceipt;

        }

        internal Guid OwnerOperationId { get; }

        internal CovenantDigest OwnerEffectDigest { get; }

        internal CampaignPathFullInstallationResetInventory Inventory { get; }

        internal InstallationResetActivePublication Publication { get; }

        internal SqliteTransaction Transaction { get; }

        internal CampaignPathFullInstallationResetCleanupReceipt? PublishedReceipt { get; }

        /// <summary>
        /// Minted while the publication was current, the way the coordinator mints it.
        /// </summary>
        internal FullInstallationResetMarkerCleanupAuthority Authority { get; }

        internal async Task<Result<CampaignPathFullInstallationResetCleanupReceipt>> RunAsync(
            CampaignPathMarkerLifecycle? lifecycle = null,
            CampaignPathFullInstallationResetCleanupPreparation? preparation = null,
            CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt = null)
        {

            CampaignPathMarkerLifecycle subject = lifecycle ?? _harness.Lifecycle;

            return await subject.PrepareFullInstallationResetCleanupAsync(
                preparation ?? Value(
                    CampaignPathFullInstallationResetCleanupPreparation.Create(
                        OwnerOperationId,
                        OwnerEffectDigest,
                        Inventory)),
                expectedReceipt ?? PublishedReceipt,
                Authority,
                _harness.Connection,
                Transaction,
                Token);

        }

        internal async Task<IReadOnlyList<CampaignPathFullResetCleanupChildRow>>
            ReadChildrenAsync()
        {

            CampaignPathFullResetCleanupEvidenceStore store = new(
                CovenantSqliteConnectionInitializer.Instance,
                _harness.Connection,
                Transaction);

            return Value(await store.ReadOwnerChildrenAsync(OwnerOperationId, Token));

        }

        internal async Task<long> CountChildrenInTransactionAsync()
        {

            await using SqliteCommand command = _harness.Connection.CreateCommand();

            command.Transaction = Transaction;

            command.CommandText =
                "SELECT COUNT(*) FROM campaign_path_marker_intents WHERE IntentKindCode = 4;";

            return Convert.ToInt64(await command.ExecuteScalarAsync(Token), provider: null);

        }

        /// <summary>
        /// Reconciles through the freshly minted authority, releasing this attempt's read transaction
        /// first because the seam opens short transactions of its own on the same connection.
        /// </summary>
        internal async Task<Result<CampaignPathFullInstallationResetCleanupReceipt>> ReconcileAsync(
            CampaignPathFullInstallationResetCleanupReceipt prepared,
            CampaignPathMarkerLifecycle? lifecycle = null)
        {

            await RollbackAsync();

            return await (lifecycle ?? _harness.Lifecycle)
                .ReconcileFullInstallationResetCleanupAsync(
                    prepared,
                    Authority,
                    _harness.Connection,
                    Token);

        }

        internal async Task CommitAsync()
        {

            await Transaction.CommitAsync(Token);

            await Transaction.DisposeAsync();

        }

        internal async Task RollbackAsync()
        {

            await Transaction.RollbackAsync(Token);

            await Transaction.DisposeAsync();

        }

    }

    private sealed class CleanupHarness : IAsyncDisposable
    {

        private static readonly byte[] RootIdentityKey = Convert.FromHexString(
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

        private static readonly byte[] MarkerKey = Convert.FromHexString(
            "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F");

        private readonly CovenantSchemaScratchDatabase _database;

        private readonly string _scratch;

        private readonly string _guardedRoot;

        private readonly ArcanumMaintenanceLock _heldLock;

        private readonly RecordingActiveStore _store;

        private readonly HostToolsMarkerPairResetCoordinator _coordinator;

        private CleanupHarness(
            CovenantSchemaScratchDatabase database,
            string scratch,
            string guardedRoot,
            ArcanumMaintenanceLock heldLock,
            PhysicalCampaignRootOpener opener,
            CampaignPathMarkerCodec codec,
            CampaignPathMarkerLifecycle lifecycle,
            RecordingActiveStore store,
            HostToolsMarkerPairResetCoordinator coordinator)
        {

            _database = database;

            _scratch = scratch;

            _guardedRoot = guardedRoot;

            _heldLock = heldLock;

            _store = store;

            _coordinator = coordinator;

            Opener = opener;

            Codec = codec;

            Lifecycle = lifecycle;

        }

        internal SqliteConnection Connection => _database.Connection;

        internal PhysicalCampaignRootOpener Opener { get; }

        internal CampaignPathMarkerCodec Codec { get; }

        internal CampaignPathMarkerLifecycle Lifecycle { get; }

        internal static async Task<CleanupHarness> CreateAsync()
        {

            CovenantSchemaScratchDatabase database =
                await CovenantSchemaScratchDatabase.CreateAsync(Token);

            string scratch = Directory.CreateTempSubdirectory(
                "arcanum-full-reset-cleanup-").FullName;

            string guardedRoot = Directory.CreateDirectory(
                Path.Combine(scratch, "guarded")).FullName;

            try
            {

                _ = await GrimoireSchemaTestInstaller.InstallAsync(
                    database.Connection,
                    1536,
                    Token);

                ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                    ArcanumMaintenanceLock.TryAcquire(guardedRoot));

                PhysicalCampaignRootOpener opener = new(new StubKeySource(RootIdentityKey));

                CampaignPathMarkerCodec codec = new(new StubKeySource(MarkerKey));

                CampaignPathMarkerLifecycle lifecycle = new(
                    codec,
                    opener,
                    new ForbiddenConnectionSource(),
                    CovenantSqliteConnectionInitializer.Instance,
                    TimeProvider.System,
                    new RecordingRecoveryKeyProvider(available: true));

                RecordingActiveStore store = new(guardedRoot);

                HostToolsMarkerPairResetCoordinator coordinator = new(
                    store,
                    new InertDatabase(),
                    new InertReadiness(),
                    new HostProcessToolsMarkerPairJoiner(),
                    new AcceptingVerifier(() => store.Current),
                    new InertLifecycle(),
                    new InertOsPort());

                return new CleanupHarness(
                    database,
                    scratch,
                    guardedRoot,
                    heldLock,
                    opener,
                    codec,
                    lifecycle,
                    store,
                    coordinator);

            }
            catch
            {

                await database.DisposeAsync();

                Directory.Delete(scratch, recursive: true);

                throw;

            }

        }

        /// <summary>
        /// A second lifecycle over the same database and the same directories, retaining nothing.
        /// </summary>
        internal CampaignPathMarkerLifecycle CreateFreshProcessLifecycle(
            RecordingRecoveryKeyProvider? keys = null) =>
            new(
                Codec,
                Opener,
                new ForbiddenConnectionSource(),
                CovenantSqliteConnectionInitializer.Instance,
                TimeProvider.System,
                keys ?? new RecordingRecoveryKeyProvider(available: true));

        /// <summary>
        /// Runs the authenticated pre-effect inventory and publishes the checkpoint that names it.
        /// </summary>
        internal async Task<PreparedOperation> PrepareAsync(
            Guid? ownerOperationId = null,
            CampaignPathMarkerLifecycle? inventoryLifecycle = null)
        {

            Guid owner = ownerOperationId ?? Guid.NewGuid();

            Result<CampaignPathFullInstallationResetInventory> inventory =
                await (inventoryLifecycle ?? Lifecycle)
                    .InventoryFullInstallationResetCleanupAsync(owner, Connection, Token);

            Assert.True(inventory.IsSuccess, Describe(inventory));

            InstallationResetActivePublication publication = Publish(owner, inventory.Value, null);

            return new PreparedOperation(
                this,
                owner,
                CheckpointOf(publication).OwnerEffectDigest,
                inventory.Value,
                publication,
                await BeginImmediateAsync(),
                publishedReceipt: null,
                await MintAuthorityAsync(publication));

        }

        /// <summary>
        /// Republishes the same owner's checkpoint carrying a receipt, as a resumed attempt sees it.
        /// </summary>
        internal async Task<PreparedOperation> ResumeAsync(
            PreparedOperation previous,
            CampaignPathFullInstallationResetCleanupReceipt receipt)
        {

            InstallationResetActivePublication publication = Publish(
                previous.OwnerOperationId,
                previous.Inventory,
                receipt);

            return new PreparedOperation(
                this,
                previous.OwnerOperationId,
                CheckpointOf(publication).OwnerEffectDigest,
                previous.Inventory,
                publication,
                await BeginImmediateAsync(),
                receipt,
                await MintAuthorityAsync(publication));

        }

        internal void AdvancePublishedPhase(HostToolsMarkerPairResetPhase phase) =>
            _store.Current = _store.Current with
            {
                Payload = InstallationResetActivePayloadV2.FromRecord(
                    _store.Current.Payload.ToRecord() with
                    {
                        HostToolsMarkerPairReset = CheckpointOf(_store.Current) with
                        {
                            Phase = phase,
                        },
                    }),
            };

        internal async Task<FullInstallationResetMarkerCleanupAuthority> MintAuthorityAsync(
            InstallationResetActivePublication publication)
        {

            MethodInfo? reflected = typeof(HostToolsMarkerPairResetCoordinator).GetMethod(
                "MintCleanupAuthorityAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(reflected);

            MethodInfo method = reflected;

            Task<Result<FullInstallationResetMarkerCleanupAuthority>> task =
                Assert.IsType<Task<Result<FullInstallationResetMarkerCleanupAuthority>>>(
                    method.Invoke(_coordinator, [_heldLock, publication, Token]));

            return Value(await task);

        }

        internal async Task<SqliteTransaction> BeginImmediateAsync()
        {

            await using (SqliteCommand command = Connection.CreateCommand())
            {

                command.CommandText = "PRAGMA foreign_keys;";

                _ = await command.ExecuteScalarAsync(Token);

            }

            return (SqliteTransaction)await Connection.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                Token);

        }

        internal static string MarkerPathOf(RegisteredRoot root) =>
            Path.Combine(root.DisplayPath, ".arcanum", "campaign-root.marker");

        /// <summary>
        /// Reads the committed vector outside any caller transaction, as a later attempt sees it.
        /// </summary>
        internal async Task<IReadOnlyList<CampaignPathFullResetCleanupChildRow>>
            ReadCommittedChildrenAsync(Guid ownerOperationId)
        {

            await using SqliteTransaction transaction = await BeginImmediateAsync();

            CampaignPathFullResetCleanupEvidenceStore store = new(
                CovenantSqliteConnectionInitializer.Instance,
                Connection,
                transaction);

            IReadOnlyList<CampaignPathFullResetCleanupChildRow> children =
                Value(await store.ReadOwnerChildrenAsync(ownerOperationId, Token));

            await transaction.RollbackAsync(Token);

            return children;

        }

        internal async Task<long> CountCommittedChildrenAsync() =>
            await ScalarAsync(
                "SELECT COUNT(*) FROM campaign_path_marker_intents WHERE IntentKindCode = 4;");

        internal async Task<long> CountCommittedEvidenceAsync() =>
            await ScalarAsync(
                "SELECT COUNT(*) FROM campaign_path_full_reset_cleanup_evidence;");

        internal async Task DeleteRegistryRowAsync(Guid campaignId) =>
            await ExecuteAsync(
                "DELETE FROM campaign_path_identities WHERE CampaignId = $campaign;",
                ("$campaign", Canonical(campaignId)));

        internal async Task UpdateRevisionAsync(Guid campaignId, long revision) =>
            await ExecuteAsync(
                """
                UPDATE campaign_path_identities SET Revision = $revision
                WHERE CampaignId = $campaign;
                """,
                ("$revision", revision),
                ("$campaign", Canonical(campaignId)));

        /// <summary>
        /// Renames a registered root's directory and follows it in the registry.
        /// </summary>
        /// <remarks>
        /// The inode, and therefore the identity digest and everything the marker binds itself to,
        /// is unchanged. Only the canonical display path moves, which is the one inventory field
        /// that can differ while the Campaign is still fully openable.
        /// </remarks>
        internal async Task<string> MoveRootAsync(RegisteredRoot root, string leaf)
        {

            string moved = Path.Combine(_scratch, leaf);

            Directory.Move(root.DisplayPath, moved);

            await ExecuteAsync(
                """
                UPDATE campaign_path_identities SET DisplayPath = $path
                WHERE CampaignId = $campaign;
                """,
                ("$path", moved),
                ("$campaign", Canonical(root.CampaignId)));

            return moved;

        }

        internal async Task ReplaceMarkerAsync(
            RegisteredRoot root,
            Guid markerCampaignId,
            long markerRevision,
            ulong rootVolumeId,
            ulong rootFileId)
        {

            Result<byte[]> encoded = Codec.Encode(
                new CampaignPathMarkerContent(
                    CampaignPathMarkerPolicy.Version,
                    markerCampaignId,
                    markerRevision,
                    rootVolumeId,
                    rootFileId,
                    [.. Enumerable.Repeat(
                        (byte)0x73,
                        CampaignPathMarkerPolicy.MarkerSecretByteCount)]));

            Assert.True(encoded.IsSuccess, Describe(encoded));

            await File.WriteAllBytesAsync(
                Path.Combine(root.DisplayPath, ".arcanum", "campaign-root.marker"),
                encoded.Value,
                Token);

        }

        internal async Task<RegisteredRoot> AddMarkedRootAsync(string leaf)
        {

            Guid campaignId = Guid.NewGuid();

            long revision = 3;

            string directory = Directory.CreateDirectory(
                Path.Combine(_scratch, leaf)).FullName;

            CovenantDigest identity = Assert.IsType<CovenantDigest>(
                Opener.IdentifyExact(directory));

            Result<CampaignPathMarkerRootAuthority> opened =
                await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
                    Opener,
                    campaignId,
                    revision,
                    identity,
                    directory,
                    Token);

            Assert.True(opened.IsSuccess, Describe(opened));

            await using CampaignPathMarkerRootAuthority authority = opened.Value;

            Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                directory,
                out FileHandleMetadata metadata));

            Result<byte[]> encoded = Codec.Encode(
                new CampaignPathMarkerContent(
                    CampaignPathMarkerPolicy.Version,
                    campaignId,
                    revision,
                    metadata.Identity.VolumeId,
                    metadata.Identity.FileId,
                    [.. Enumerable.Repeat(
                        (byte)0x42,
                        CampaignPathMarkerPolicy.MarkerSecretByteCount)]));

            Assert.True(encoded.IsSuccess, Describe(encoded));

            PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary = Value(
                await authority.CreateTemporaryExclusiveNoFollowAsync(
                    $"marker-{Guid.NewGuid():N}.tmp",
                    Token));

            Assert.True((await temporary.WriteAllAsync(encoded.Value, Token)).IsSuccess);

            Assert.True((await temporary.FlushToDiskAsync(Token)).IsSuccess);

            Assert.True((await authority.RenameTemporaryToMarkerNoReplaceAsync(
                temporary,
                temporary.PhysicalIdentityDigest,
                encoded.Value,
                Token)).IsSuccess);

            await ExecuteAsync(
                """
                INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings",
                    "SanctumConfigJson", "CreatedAt", "UpdatedAt")
                VALUES ($campaign, $leaf, $leaf, $path, 1, '{}', '{}',
                    '2026-08-22T00:00:00Z', '2026-08-22T00:00:00Z');
                """,
                ("$campaign", Canonical(campaignId)),
                ("$leaf", leaf),
                ("$path", directory));

            await ExecuteAsync(
                """
                INSERT INTO campaign_path_identities (
                    CampaignId, PolicyVersion, Revision, DisplayPath, Depth,
                    PhysicalIdentityDigest, UpdatedAtUtc)
                VALUES ($campaign, $policy, $revision, $path, 1, $identity,
                    '2026-08-22T00:00:00.0000000+00:00');
                """,
                ("$campaign", Canonical(campaignId)),
                ("$policy", (long)CampaignPathIdentityPolicy.Version),
                ("$revision", revision),
                ("$path", directory),
                ("$identity", identity.Bytes.ToArray()));

            return new RegisteredRoot(
                campaignId,
                revision,
                directory,
                identity,
                new CovenantDigest(SHA256.HashData(encoded.Value)),
                metadata.Identity.VolumeId,
                metadata.Identity.FileId);

        }

        public async ValueTask DisposeAsync()
        {

            _heldLock.Dispose();

            await _database.DisposeAsync();

            try
            {
                Directory.Delete(_scratch, recursive: true);
            }
            catch (IOException)
            {
                // A leftover scratch directory is not worth failing a suite over.
            }

        }

        /// <summary>
        /// The spelling both of these columns hold in production: uppercase, dashed, 36 characters.
        /// </summary>
        /// <remarks>
        /// <c>"Campaigns"."Id"</c> is written by the object-relational writer, which the SQLite value
        /// binder uppercases unconditionally, and <c>campaign_path_identities.CampaignId</c> is declared
        /// <c>REFERENCES "Campaigns"("Id")</c> and bound as a <c>Guid</c> by
        /// <c>CampaignPathIdentityReader</c> for exactly that reason. A <c>ToString("D")</c> here rendered
        /// both lowercase, which is a pairing no installation holds and the version-5 identity guards now
        /// refuse.
        /// </remarks>
        private static string Canonical(Guid identity) => identity.ToString("D").ToUpperInvariant();

        private async Task<long> ScalarAsync(string sql)
        {

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = sql;

            return Convert.ToInt64(await command.ExecuteScalarAsync(Token), provider: null);

        }

        private async Task ExecuteAsync(
            string sql,
            params (string Name, object Value)[] parameters)
        {

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = sql;

            foreach ((string name, object value) in parameters)
            {

                _ = command.Parameters.AddWithValue(name, value);

            }

            _ = await command.ExecuteNonQueryAsync(Token);

        }

        private static HostToolsMarkerPairResetCheckpointV1 CheckpointOf(
            InstallationResetActivePublication publication) =>
            Assert.IsType<HostToolsMarkerPairResetCheckpointV1>(
                publication.Payload.HostToolsMarkerPairReset);

        /// <summary>
        /// Builds and publishes the exact authenticated checkpoint the authority revalidates against.
        /// </summary>
        private InstallationResetActivePublication Publish(
            Guid operation,
            CampaignPathFullInstallationResetInventory inventory,
            CampaignPathFullInstallationResetCleanupReceipt? receipt)
        {

            Guid installation = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

            DateTimeOffset acceptedAtUtc = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

            FullInstallationResetExternalRemediationAttestation attestation =
                Attestation(operation);

            CovenantDigest signedDigest = Value(
                FullInstallationResetRemediationAttestationDigest.Calculate(attestation));

            FullInstallationResetRemediationClaimV1 claim = new(
                1,
                operation,
                installation,
                signedDigest,
                Digest(0x45),
                Digest(0x46),
                acceptedAtUtc);

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            CovenantDigest pairDigest = Value(
                FullInstallationResetMarkerPairResetDigests.PairEvidence(pair));

            CovenantDigest ownerEffect = Value(
                FullInstallationResetMarkerPairResetDigests.FullResetEffect(
                    operation,
                    installation,
                    pair.Database.TransitionId!.Value,
                    pair.Database.TaintMasterKeyVersion!.Value,
                    pair.Database.TaintFingerprint!.Value,
                    pair.Database.DatabaseMarkerDigest,
                    pair.OsMarker.MarkerBytesDigest,
                    attestation.RemediationActionDigest,
                    inventory.InventoryDigest));

            HostToolsMarkerPairResetCheckpointV1 checkpoint = new(
                1,
                HostToolsMarkerPairResetPhase.PairAbsenceVerified,
                new FullInstallationResetRestartProofV1(
                    1,
                    FullInstallationResetSignedAttestationProjectionV1.FromAttestation(
                        attestation),
                    acceptedAtUtc,
                    signedDigest,
                    pair.Database,
                    pair.OsMarker,
                    pairDigest),
                inventory.Entries,
                inventory.InventoryDigest,
                ownerEffect,
                receipt?.MarkerIntentCount,
                receipt?.OrderedMarkerIntentIds,
                receipt?.MarkerIntentVectorDigest,
                receipt?.DeletedCount,
                receipt?.OrphanCount);

            InstallationResetActiveRecord record = new(
                InstallationResetActiveStore.CurrentVersion,
                operation,
                "full-reset-plan",
                InstallationResetScope.All,
                new DataRetentionWorkspaceBinding(Guid.NewGuid(), "/workspace"),
                new InstallationResetAcceptedBinding("binding", [], [], [], [], []),
                InstallationResetPhase.Prepared,
                PointOfNoReturn: false,
                RowsDeleted: 0,
                FilesDeleted: 0,
                EstimatedBytesDeleted: 0,
                CredentialResults: [],
                LastErrorCode: ErrorCodes.Data.RecoveryRequired,
                FullInstallationResetRemediationClaim: claim,
                HostToolsMarkerPairReset: checkpoint);

            InstallationResetActiveLocation location = new(
                "/active",
                Digest(0x10),
                Digest(0x11),
                "reset.active",
                Digest(0x12));

            InstallationResetActiveEnvelopeV2 envelope = new(
                2,
                location.ProfileNamespaceDigest,
                installation,
                operation,
                2,
                Digest(0x13),
                location.Digest,
                InstallationResetScope.All,
                record.PlanId,
                "nonce",
                "ciphertext",
                "tag");

            CovenantDigest envelopeDigest = Digest(0x14);

            InstallationResetActivePublication publication = new(
                location,
                envelope,
                envelopeDigest,
                InstallationResetActivePayloadV2.FromRecord(record),
                new InstallationResetActiveAnchorV1(
                    1,
                    InstallationResetActiveAnchorState.Active,
                    location.ProfileNamespaceDigest,
                    installation,
                    operation,
                    2,
                    envelopeDigest,
                    location.Digest));

            _store.Current = publication;

            return publication;

        }

        private static FullInstallationResetExternalRemediationAttestation Attestation(
            Guid operationId) =>
            new(
                1,
                operationId,
                Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
                Guid.Parse("11111111-2222-4333-8444-555555555555"),
                7,
                Digest(0x5A),
                TaintedDatabaseEvidence().DatabaseMarkerDigest,
                Digest(0x23),
                new CovenantDigest(Convert.FromHexString(
                    "761e8536128080d5936070524da90a6558b8901ea46d93194646b413bb27a1d9")),
                Base64Url.EncodeToString([.. Enumerable.Repeat((byte)0x33, 16)]),
                "RetroDownfall.Remediation.v1",
                new DateTimeOffset(2026, 8, 22, 11, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero),
                Base64Url.EncodeToString([.. Enumerable.Repeat((byte)0x44, 64)]));

        private static HostProcessToolsDatabaseMarkerEvidence TaintedDatabaseEvidence() =>
            new(
                "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
                RetroDownfall.Arcanum.Core.Security.CovenantHostToolsState.HostToolsTainted,
                Guid.Parse("11111111-2222-4333-8444-555555555555"),
                7,
                Digest(0x5A));

        private static HostProcessToolsOsMarkerEvidence MatchedOsEvidence() =>
            new(
                "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
                Guid.Parse("11111111-2222-4333-8444-555555555555"),
                7,
                Digest(0x5A),
                Digest(0x23),
                Digest(0x25));

    }

    private sealed class RecordingRecoveryKeyProvider(bool available)
        : ICampaignRootIdentityRecoveryKeyProvider
    {

        private static readonly byte[] Key = Convert.FromHexString(
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

        internal int Calls { get; private set; }

        public bool TryCopyExistingRootIdentityKey(Span<byte> destination)
        {

            Calls++;

            if (!available || destination.Length != Key.Length)
            {

                return false;

            }

            Key.CopyTo(destination);

            return true;

        }

    }

    private sealed class StubKeySource(byte[] key) : ICampaignRootIdentityKeyProvider
    {

        public bool TryCopyRootIdentityKey(Span<byte> destination)
        {

            if (destination.Length < key.Length)
            {

                return false;

            }

            key.CopyTo(destination);

            return true;

        }

    }

    private sealed class ForbiddenConnectionSource : ICovenantConnectionSource
    {

        public ValueTask<SqliteConnection> GetOpenConnectionAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The full-reset cleanup must borrow its caller's Core connection.");

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The full-reset cleanup must borrow its caller's Core connection.");

    }

    private sealed class RecordingActiveStore(string guardedRoot)
        : IInstallationResetActiveStore
    {

        public string GuardedRoot { get; } = guardedRoot;

        internal InstallationResetActivePublication Current { get; set; } = null!;

        public Task<Result<InstallationResetActiveRecoveryState>> RecoverAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<InstallationResetActiveRecoveryState>.Success(
                new InstallationResetActiveRecoveryState(
                    InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
                    Current,
                    LegacyRecord: null)));

        public Task<Result<InstallationResetActivePublication>> BeginAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetActivePublication>> AdvanceAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication current,
            InstallationResetActiveRecord next,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetActiveRecoveryState>> InspectAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetActivePublication>> MigrateLegacyV1Async(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord expectedRecord,
            FileHandleIdentity expectedIdentity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> RetireAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> CompleteStartupCleanupAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class AcceptingVerifier(
        Func<InstallationResetActivePublication> current)
        : IFullInstallationResetRemediationAttestationVerifier
    {

        public bool MatchesAuthenticatedClaim(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair,
            Guid acceptedOperationId,
            Guid acceptedInstallationId,
            CovenantDigest acceptedAttestationDigest,
            CovenantDigest acceptedNonceDigest,
            CovenantDigest acceptedIssuerDigest) =>
            throw new NotSupportedException();

        public Result<FullInstallationResetRemediationAuthorization> Verify(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair) =>
            throw new NotSupportedException();

        public Result<FullInstallationResetRemediationAuthorization> VerifyAtAcceptedTime(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid authenticatedInstallationId,
            HostProcessToolsMatchedPair persistedPair,
            DateTimeOffset acceptedAtUtc)
        {

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current().Payload.FullInstallationResetRemediationClaim);

            return Result<FullInstallationResetRemediationAuthorization>.Success(
                new FullInstallationResetRemediationAuthorization(
                    claim.OperationId,
                    claim.InstallationId,
                    claim.AttestationDigest,
                    claim.NonceDigest,
                    claim.IssuerDigest,
                    claim.AcceptedAtUtc));

        }

    }

    private sealed class InertDatabase : IHostToolsMarkerPairResetDatabase
    {

        public Task<Result<HostToolsMarkerPairResetDatabaseSession>> OpenAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class InertReadiness : IFullInstallationResetCampaignSchemaReadiness
    {

        public Task<Result> RequireExactAsync(
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class InertOsPort : IHostToolsMarkerPairResetOsPort
    {

        public HostToolsMarkerPairResetOsOpenResult OpenExact() =>
            throw new NotSupportedException();

        public HostToolsMarkerPairResetOsOpenResult ReopenExact(
            HostProcessToolsOsMarkerEvidence expectedEvidence) =>
            throw new NotSupportedException();

        public Task<HostToolsMarkerPairResetOsDeleteStatus> CompareDeleteExactAsync(
            IHostToolsMarkerPairResetOsCapability capability,
            HostProcessToolsOsMarkerEvidence expectedEvidence,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<HostToolsMarkerPairResetOsAbsenceStatus> ProveExactAbsenceAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class InertLifecycle : ICampaignPathMarkerLifecycle
    {

        public Task<Result<CampaignPathFullInstallationResetInventory>>
            InventoryFullInstallationResetCleanupAsync(
                Guid ownerOperationId,
                SqliteConnection liveCoreConnection,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result> RevalidateFullInstallationResetInventoryAsync(
            CampaignPathFullInstallationResetInventory inventory,
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
            PrepareFullInstallationResetCleanupAsync(
                CampaignPathFullInstallationResetCleanupPreparation preparation,
                CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt,
                FullInstallationResetMarkerCleanupAuthority authority,
                SqliteConnection liveCoreConnection,
                SqliteTransaction liveCoreTransaction,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
            ReconcileFullInstallationResetCleanupAsync(
                CampaignPathFullInstallationResetCleanupReceipt prepared,
                FullInstallationResetMarkerCleanupAuthority authority,
                SqliteConnection liveCoreConnection,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<CampaignPathRestoreCleanupInventory>> InventoryRestoreCleanupAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<CampaignPathRestoreCleanupPreparationReceipt>>
            PrepareRestoreCleanupInStagedDatabaseAsync(
                CampaignPathRestoreCleanupPreparation preparation,
                SqliteConnection stagedConnection,
                SqliteTransaction stagedTransaction,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<CampaignPathMarkerGateCompletion>> ReconcileGateOwnedAsync(
            CampaignPathMarkerGateReconcileRequest request,
            ICovenantExclusiveOperationLease exclusiveLease,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask ReleaseRetainedRootsAsync(Guid ownerOperationId) =>
            throw new NotSupportedException();

    }

}
