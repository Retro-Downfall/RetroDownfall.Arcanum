using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// State, receipt, and conflict behaviour of the transactional mutation kernel.
/// </summary>
public sealed class CovenantMutationKernelTests
{

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void The_kernel_exposes_only_the_batch_entry_point()
    {

        System.Reflection.MethodInfo[] declared = [.. typeof(CovenantMutationKernel)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)];

        System.Reflection.MethodInfo only = Assert.Single(declared);

        Assert.Equal(nameof(CovenantMutationKernel.ApplyBatchAsync), only.Name);

        Assert.Equal(
            [typeof(CovenantMutationBatch), typeof(CovenantMutationTransaction), typeof(CancellationToken)],
            only.GetParameters().Select(static parameter => parameter.ParameterType));

    }

    [Fact]
    public async Task A_create_appends_a_version_and_advances_its_head()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> applied = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.style",
                    "Prefer terse answers.",
                    expectedRevision: 0,
                    expectedKeyEpoch: 0)),
            Token);

        CovenantMutationReceipt receipt = Assert.Single(applied.Value);

        Assert.Equal(CovenantMutationOutcome.Applied, receipt.Outcome);

        Assert.Equal(1L, receipt.ResultingLaneRevision);

        Assert.False(receipt.Replayed);

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_entries;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_versions;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_heads;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT CanonicalSearchSequence FROM covenant_state;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_search_outbox;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_mutation_receipts;"));

    }

    [Fact]
    public async Task An_update_advances_the_lane_and_keeps_the_old_version()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.style",
                    "First.",
                    0,
                    0)),
            Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> updated = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.style",
                    "Second.",
                    expectedRevision: 1,
                    expectedKeyEpoch: 1)),
            Token);

        CovenantMutationReceipt receipt = Assert.Single(updated.Value);

        Assert.Equal(2L, receipt.ResultingLaneRevision);

        Assert.Equal(2, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_versions;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_heads;"));

        Assert.Equal(2, await ScalarAsync(fixture, "SELECT CanonicalSearchSequence FROM covenant_state;"));

    }

    [Fact]
    public async Task An_identical_set_returns_no_change_without_appending()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.style", "Same.", 0, 0)),
            Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> repeated = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.style", "Same.", 1, 1)),
            Token);

        CovenantMutationReceipt receipt = Assert.Single(repeated.Value);

        Assert.Equal(CovenantMutationOutcome.NoChange, receipt.Outcome);

        Assert.Null(receipt.ResultingVersionId);

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_versions;"));

        // A NoChange is still durable evidence: the receipt exists even though nothing was appended.
        Assert.Equal(2, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_mutation_receipts;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT CanonicalSearchSequence FROM covenant_state;"));

    }

    [Fact]
    public async Task A_retirement_tombstones_the_lane_and_repeating_it_is_no_change()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.doomed", "Bye.", 0, 0)),
            Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> retired = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorRetire(
                    CovenantOperationScope.Global,
                    "global.doomed",
                    CovenantLane.Confirmed,
                    expectedRevision: 1,
                    expectedKeyEpoch: 1)),
            Token);

        Assert.Equal(CovenantMutationOutcome.Applied, Assert.Single(retired.Value).Outcome);

        Assert.Equal(2, await ScalarAsync(fixture, "SELECT CurrentOperationCode FROM covenant_heads;"));

        Result<IReadOnlyList<CovenantMutationReceipt>> again = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorRetire(
                    CovenantOperationScope.Global,
                    "global.doomed",
                    CovenantLane.Confirmed,
                    expectedRevision: 2,
                    expectedKeyEpoch: 2)),
            Token);

        Assert.Equal(CovenantMutationOutcome.NoChange, Assert.Single(again.Value).Outcome);

    }

    [Fact]
    public async Task Reactivation_after_a_confirmed_tombstone_requires_the_explicit_flag()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.back", "One.", 0, 0)),
            Token);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorRetire(
                    CovenantOperationScope.Global,
                    "global.back",
                    CovenantLane.Confirmed,
                    1,
                    1)),
            Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.back", "Two.", 2, 2)),
            Token);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, refused.Error.Code);

        Result<IReadOnlyList<CovenantMutationReceipt>> allowed = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.back",
                    "Two.",
                    2,
                    2,
                    reactivate: true)),
            Token);

        Assert.Equal(3L, Assert.Single(allowed.Value).ResultingLaneRevision);

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT CurrentOperationCode FROM covenant_heads;"));

    }

    [Fact]
    public async Task An_agent_cannot_reactivate_a_retired_proposed_lane()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                fixture,
                Token,
                CovenantMutationFixture.AgentPropose(CampaignOne, "campaign.idea", "One.", 0, 0)),
            Token);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                fixture,
                Token,
                CovenantMutationFixture.OperatorRetire(
                    CovenantOperationScope.ForCampaign(CampaignOne),
                    "campaign.idea",
                    CovenantLane.Proposed,
                    1,
                    1)),
            Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                fixture,
                Token,
                CovenantMutationFixture.AgentPropose(CampaignOne, "campaign.idea", "Two.", 2, 2)),
            Token);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, refused.Error.Code);

    }

    [Fact]
    public async Task The_two_lanes_carry_independent_revisions()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        CovenantOperationScope scope = CovenantOperationScope.ForCampaign(CampaignOne);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                fixture,
                Token,
                CovenantMutationFixture.OperatorSet(scope, "shared.key", "Confirmed one.", 0, 0)),
            Token);

        // Two Proposed revisions must not disturb the Confirmed lane's expected revision.
        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                fixture,
                Token,
                CovenantMutationFixture.AgentPropose(CampaignOne, "shared.key", "Proposed one.", 0, 1)),
            Token);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                fixture,
                Token,
                CovenantMutationFixture.AgentPropose(CampaignOne, "shared.key", "Proposed two.", 1, 2)),
            Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> confirmed = await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                fixture,
                Token,
                CovenantMutationFixture.OperatorSet(scope, "shared.key", "Confirmed two.", 1, 3)),
            Token);

        Assert.Equal(2L, Assert.Single(confirmed.Value).ResultingLaneRevision);

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(DISTINCT EntryId) FROM covenant_heads;"));

        Assert.Equal(2, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_heads;"));

    }

    [Fact]
    public async Task A_stale_expected_revision_fails_without_mutating()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.cas", "One.", 0, 0)),
            Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> stale = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.cas", "Two.", 7, 1)),
            Token);

        Assert.Equal(ErrorCodes.Covenant.RevisionConflict, stale.Error.Code);

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_versions;"));

    }

    [Fact]
    public async Task Creating_over_an_existing_head_is_a_revision_conflict()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.dup", "One.", 0, 0)),
            Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> duplicate = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.dup", "Two.", 0, 1)),
            Token);

        Assert.Equal(ErrorCodes.Covenant.RevisionConflict, duplicate.Error.Code);

    }

    [Fact]
    public async Task Retiring_a_key_that_never_existed_is_a_lifecycle_conflict()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorRetire(
                    CovenantOperationScope.Global,
                    "global.absent",
                    CovenantLane.Confirmed,
                    0,
                    0)),
            Token);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, refused.Error.Code);

    }

    [Fact]
    public async Task The_same_mutation_id_and_request_digest_replays_its_committed_receipt()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        Guid mutationId = Guid.NewGuid();

        Result<IReadOnlyList<CovenantMutationReceipt>> first = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.replay",
                    "One.",
                    0,
                    0,
                    mutationId)),
            Token);

        // A later head change must not make the replay produce a different answer: the receipt is
        // resolved before any compare-and-swap is attempted.
        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.replay", "Two.", 1, 1)),
            Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> replay = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.replay",
                    "One.",
                    0,
                    0,
                    mutationId)),
            Token);

        CovenantMutationReceipt replayed = Assert.Single(replay.Value);

        Assert.True(replayed.Replayed);

        Assert.Equal(Assert.Single(first.Value).ResultingVersionId, replayed.ResultingVersionId);

        // An exact retry returns its committed answer, and that includes the entry it named. A
        // zeroed identity on the replay path is a valid-looking receipt pointing at no row at all.
        Assert.NotEqual(Guid.Empty, replayed.EntryId);

        Assert.Equal(Assert.Single(first.Value).EntryId, replayed.EntryId);

        Assert.Equal(CovenantOperationScope.Global, replayed.Scope);

        Assert.Equal(2, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_mutation_receipts;"));

    }

    [Fact]
    public async Task A_replayed_no_change_receipt_still_names_its_entry()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> first = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.idle", "Same.", 0, 0)),
            Token);

        Guid mutationId = Guid.NewGuid();

        // The identical content makes this a deliberate no-op, so its receipt carries no resulting
        // version and the entry can only be recovered through the entry table.
        Result<IReadOnlyList<CovenantMutationReceipt>> noChange = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.idle",
                    "Same.",
                    1,
                    1,
                    mutationId)),
            Token);

        Assert.Equal(CovenantMutationOutcome.NoChange, Assert.Single(noChange.Value).Outcome);

        Result<IReadOnlyList<CovenantMutationReceipt>> replay = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.idle",
                    "Same.",
                    1,
                    1,
                    mutationId)),
            Token);

        CovenantMutationReceipt replayed = Assert.Single(replay.Value);

        Assert.True(replayed.Replayed);

        Assert.Null(replayed.ResultingVersionId);

        Assert.Equal(Assert.Single(first.Value).EntryId, replayed.EntryId);

    }

    [Fact]
    public async Task The_same_mutation_id_with_a_different_request_digest_fails_closed()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        Guid mutationId = Guid.NewGuid();

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.conflict",
                    "One.",
                    0,
                    0,
                    mutationId)),
            Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> conflict = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.conflict",
                    "Two.",
                    1,
                    1,
                    mutationId,
                    authorizationSeed: 99)),
            Token);

        Assert.Equal("Security.IdempotencyConflict", conflict.Error.Code);

    }

    [Fact]
    public async Task A_stale_dataset_generation_is_refused()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                Guid.NewGuid(),
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.stale", "One.", 0, 0)),
            Token);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    [Fact]
    public async Task A_stale_key_epoch_is_refused_as_an_aba_hazard()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.aba", "One.", 0, 0)),
            Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.aba",
                    "Two.",
                    expectedRevision: 1,
                    expectedKeyEpoch: 0)),
            Token);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    [Fact]
    public async Task A_changed_campaign_registry_epoch_is_refused()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "global.registry", "One.", 0, 0)),
            Token);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    [Fact]
    public async Task Provenance_leaves_are_stored_and_summarized_on_their_version()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        ImmutableArray<CovenantMutationProvenanceLeaf> provenance =
        [
            new CovenantMutationProvenanceLeaf(
                0,
                new Guid("aaaaaaaa-1111-4111-8111-111111111111"),
                new Guid("bbbbbbbb-1111-4111-8111-111111111111"),
                "logical/one",
                CovenantOperationGateFixture.Digest(5),
                CovenantMaterializationSourceRange.WholeSource,
                null,
                null,
                null,
                null),

            new CovenantMutationProvenanceLeaf(
                1,
                new Guid("aaaaaaaa-2222-4222-8222-222222222222"),
                new Guid("bbbbbbbb-2222-4222-8222-222222222222"),
                "logical/two",
                CovenantOperationGateFixture.Digest(6),
                CovenantMaterializationSourceRange.Utf16Range,
                4,
                9,
                null,
                null),
        ];

        Result<IReadOnlyList<CovenantMutationReceipt>> applied = await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                fixture,
                Token,
                CovenantMutationFixture.AgentPropose(
                    CampaignOne,
                    "campaign.sourced",
                    "From attachments.",
                    0,
                    0,
                    provenance: provenance)),
            Token);

        Assert.True(applied.IsSuccess);

        Assert.Equal(2, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_version_attachment_provenance;"));

        Assert.Equal(2, await ScalarAsync(fixture, "SELECT AttachmentProvenanceCount FROM covenant_versions;"));

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease lease = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CampaignOne),
            Token)).Value;

        CovenantSourcePage page = (await fixture.Store.ReadSourcePageAsync(
            new CovenantSourceQuery(Assert.Single(applied.Value).ResultingVersionId!.Value),
            lease,
            Token)).Value;

        // The aggregate the kernel stored has to be the one a later detailed read recomputes.
        Assert.True(page.DigestMatches);

    }

    [Fact]
    public async Task One_batch_allocates_exactly_one_search_sequence_and_ordered_outbox_rows()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        CovenantOperationScope scope = CovenantOperationScope.ForCampaign(CampaignOne);

        Result<IReadOnlyList<CovenantMutationReceipt>> applied = await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                fixture,
                Token,
                CovenantMutationFixture.OperatorSet(scope, "batch.one", "One.", 0, 0),
                CovenantMutationFixture.OperatorSet(scope, "batch.two", "Two.", 0, 0, authorizationSeed: 80),
                CovenantMutationFixture.AgentPropose(CampaignOne, "batch.three", "Three.", 0, 0)),
            Token);

        Assert.Equal(3, applied.Value.Count);

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT CanonicalSearchSequence FROM covenant_state;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(DISTINCT SearchSequence) FROM covenant_search_outbox;"));

        Assert.Equal(3, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_search_outbox;"));

        Assert.Equal(
            3,
            await ScalarAsync(fixture, "SELECT COUNT(DISTINCT Ordinal) FROM covenant_search_outbox;"));

    }

    [Fact]
    public async Task A_failed_batch_writes_nothing_at_all()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, "batch.good", "Good.", 0, 0),
                CovenantMutationFixture.OperatorRetire(
                    CovenantOperationScope.Global,
                    "batch.absent",
                    CovenantLane.Confirmed,
                    0,
                    0,
                    authorizationSeed: 90)),
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(0, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_entries;"));

        Assert.Equal(0, await ScalarAsync(fixture, "SELECT CanonicalSearchSequence FROM covenant_state;"));

    }

    [Fact]
    public async Task An_approved_agent_retirement_the_factory_built_persists_its_ward_evidence()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CovenantTask6Fixture.CampaignId, "one", Token);

        _ = await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                fixture,
                Token,
                CovenantMutationFixture.AgentPropose(
                    CovenantTask6Fixture.CampaignId,
                    "campaign.a",
                    "Prefer repo-root builds.",
                    0,
                    0)),
            Token);

        await using CovenantAgentRetirementCapability capability = new(targetLaneRevision: 1, keyEpoch: 1);

        Result<CovenantMutationIntent> intent = CovenantAgentMutationFactory.Retire(
            capability.Context,
            CovenantTask6Fixture.D(90));

        Assert.True(intent.IsSuccess, intent.Error.Message);

        Result<IReadOnlyList<CovenantMutationReceipt>> retired = await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(fixture, Token, intent.Value),
            Token);

        Assert.True(retired.IsSuccess, retired.IsFailure ? retired.Error.Message : string.Empty);

        Assert.Equal(CovenantMutationOutcome.Applied, Assert.Single(retired.Value).Outcome);

        // covenant_versions permits a Ward digest only under OriginCode 3, and demands a Ward mode
        // with it. Any other origin makes the tombstone insert a raw CHECK failure at commit time.
        Assert.Equal(
            (long)CovenantOrigin.AgentApproved,
            await ScalarAsync(fixture, "SELECT OriginCode FROM covenant_versions WHERE OperationCode = 2;"));

        Assert.Equal(
            1,
            await ScalarAsync(
                fixture,
                "SELECT COUNT(*) FROM covenant_versions WHERE OperationCode = 2 AND WardReceiptDigest IS NOT NULL;"));

        Assert.Equal(
            (long)CovenantAuthorizationMode.WardInteractive,
            await ScalarAsync(
                fixture,
                "SELECT AuthorizationModeCode FROM covenant_versions WHERE OperationCode = 2;"));

    }

    private static async Task<long> ScalarAsync(CovenantCanonicalFixture fixture, string sql)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(Token);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

    }

    /// <summary>
    /// A live retirement capability aimed at an already-staged Proposed head, so the persistence path
    /// runs against the intent the factory actually mints rather than a hand-written one.
    /// </summary>
    private sealed class CovenantAgentRetirementCapability : IAsyncDisposable
    {

        private readonly CancellationTokenSource _turn = new();

        public CovenantAgentRetirementCapability(long targetLaneRevision, long keyEpoch)
        {

            CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();

            CovenantMutationCollector collector = new(
                Guid.CreateVersion7(),
                plan.Digest,
                CovenantTask6Fixture.BranchId);

            Context = new CovenantToolInvocationContext(
                collector,
                CovenantCapabilityFixtures.Campaign(),
                CovenantCapabilityFixtures.Admission(plan),
                CovenantCapabilityFixtures.Materialization(sourceCount: 0),
                new CovenantCapabilityFixtures.StubHeadProbe(),
                CovenantToolCapabilityNonce.Create(),
                CovenantToolNames.RetireCovenant,
                "call-1",
                CovenantCapabilityFixtures.RetirementPreflight(
                    targetLaneRevision: targetLaneRevision,
                    keyEpoch: keyEpoch),
                CovenantCapabilityFixtures.WardReceipt(CovenantWardDecision.Approved),
                _turn.Token);

        }

        public CovenantToolInvocationContext Context { get; }

        public async ValueTask DisposeAsync()
        {

            await Context.DisposeAsync();

            _turn.Dispose();

        }

    }

}
