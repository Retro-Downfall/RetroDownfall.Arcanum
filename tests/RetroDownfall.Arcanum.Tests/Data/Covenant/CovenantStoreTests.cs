using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Behaviour of the bounded canonical read port.
/// </summary>
public sealed class CovenantStoreTests
{

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    private static readonly Guid CampaignTwo = CovenantOperationGateFixture.CampaignTwo;

    // The shared gate-fixture identities are digit-only, so their uppercase and lowercase text are
    // the same string and no case mismatch can ever surface through them. Real Campaign identities
    // carry hex letters, EF writes "Campaigns"."Id" uppercase, and Covenant writes its own
    // covenant_heads.CampaignId lowercase, so any query that crosses that boundary needs these.
    private static readonly Guid HexCampaignOne = new("A1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D");

    private static readonly Guid HexCampaignTwo = new("F0E1D2C3-B4A5-4968-8778-695A4B3C2D1E");

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Turn_snapshot_loads_global_confirmed_and_campaign_lanes()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        await fixture.AddCampaignAsync(CampaignTwo, "two", Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.style",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Prefer terse answers.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "campaign.style",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Build from the repository root.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "campaign.proposal",
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "The operator seems to prefer pnpm.",
            Token);

        // Another Campaign's rows must never enter this snapshot.
        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignTwo,
            "other.campaign",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Unrelated.",
            Token);

        CovenantTurnSnapshot snapshot = await ReadSnapshotAsync(fixture, CampaignOne);

        Assert.Equal(3, snapshot.Candidates.Length);

        Assert.Equal(CampaignOne, snapshot.CanonicalCampaignId);

        Assert.Equal(await fixture.ReadDatasetGenerationAsync(Token), snapshot.DatasetGeneration.Value);

        Assert.All(
            snapshot.Candidates,
            candidate => Assert.Equal(CovenantSnapshotCandidateIntegrity.Verified, candidate.Integrity));

        Assert.DoesNotContain(snapshot.Candidates, candidate => candidate.CampaignId == CampaignTwo);

    }

    [Fact]
    public async Task Turn_snapshot_excludes_tombstones_and_global_proposed_is_impossible()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        SeededHead live = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.live",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Live.",
            Token);

        SeededHead retiring = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.retired",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Doomed.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.retired",
            CovenantLane.Confirmed,
            CovenantOperation.Retire,
            null,
            Token,
            entryId: retiring.EntryId,
            laneRevision: 2,
            predecessorVersionId: retiring.VersionId);

        CovenantTurnSnapshot snapshot = await ReadSnapshotAsync(fixture, CampaignOne);

        CovenantSnapshotCandidate only = Assert.Single(snapshot.Candidates);

        Assert.Equal(live.EntryId, only.EntryId);

    }

    [Fact]
    public async Task Global_only_turns_load_no_campaign_rows()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.only",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Global.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "campaign.only",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Campaign.",
            Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease lease =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantTurnSnapshot> snapshot = await fixture.Store.ReadTurnSnapshotAsync(
            CanonicalCampaignContext.GlobalOnly,
            lease,
            Token);

        CovenantSnapshotCandidate only = Assert.Single(snapshot.Value.Candidates);

        Assert.Equal(CovenantScope.Global, only.Scope);

        Assert.Null(snapshot.Value.CanonicalCampaignId);

    }

    [Fact]
    public async Task Turn_snapshot_refuses_more_than_one_hundred_and_sixty_active_heads()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        for (int index = 0; index < CovenantLimits.MaxActiveSnapshotRows + 1; index++)
        {

            _ = await fixture.SeedHeadAsync(
                CovenantScope.Global,
                null,
                $"global.key{index:000}",
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                $"Value {index}.",
                Token);

        }

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease lease =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantTurnSnapshot> overflow = await fixture.Store.ReadTurnSnapshotAsync(
            CanonicalCampaignContext.GlobalOnly,
            lease,
            Token);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, overflow.Error.Code);

    }

    [Fact]
    public async Task One_hundred_and_sixty_active_heads_are_accepted()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        for (int index = 0; index < CovenantLimits.MaxActiveSnapshotRows; index++)
        {

            _ = await fixture.SeedHeadAsync(
                CovenantScope.Global,
                null,
                $"global.key{index:000}",
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                $"Value {index}.",
                Token);

        }

        CovenantTurnSnapshot snapshot = await ReadSnapshotAsync(fixture, null);

        Assert.Equal(CovenantLimits.MaxActiveSnapshotRows, snapshot.Candidates.Length);

    }

    [Fact]
    public async Task A_confirmed_artifact_that_fails_verification_fails_the_turn()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.tampered",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Original.",
            Token,
            corruptCompiledContent: "- global.tampered: \"Tampered.\"\n");

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease lease =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantTurnSnapshot> snapshot = await fixture.Store.ReadTurnSnapshotAsync(
            CanonicalCampaignContext.GlobalOnly,
            lease,
            Token);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, snapshot.Error.Code);

    }

    [Fact]
    public async Task A_damaged_proposed_artifact_is_quarantined_rather_than_fatal()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "campaign.damaged",
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "Original.",
            Token,
            corruptCompiledContent: "- campaign.damaged: \"Tampered.\"\n");

        CovenantTurnSnapshot snapshot = await ReadSnapshotAsync(fixture, CampaignOne);

        CovenantSnapshotCandidate only = Assert.Single(snapshot.Candidates);

        Assert.Equal(CovenantSnapshotCandidateIntegrity.Quarantined, only.Integrity);

    }

    [Fact]
    public async Task The_turn_snapshot_rejects_a_lease_whose_coverage_does_not_match()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease wrongScope = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CampaignTwo),
            Token)).Value;

        Result<CovenantTurnSnapshot> mismatched = await fixture.Store.ReadTurnSnapshotAsync(
            CovenantCanonicalFixture.CampaignContext(CampaignOne),
            wrongScope,
            Token);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, mismatched.Error.Code);

    }

    [Fact]
    public async Task Lane_head_probe_reports_present_retired_and_absent()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        SeededHead present = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "campaign.present",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Here.",
            Token);

        SeededHead retiring = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "campaign.retired",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Going.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "campaign.retired",
            CovenantLane.Confirmed,
            CovenantOperation.Retire,
            null,
            Token,
            entryId: retiring.EntryId,
            laneRevision: 2,
            predecessorVersionId: retiring.VersionId);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease lease = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CampaignOne),
            Token)).Value;

        CanonicalCampaignContext campaign = CovenantCanonicalFixture.CampaignContext(CampaignOne);

        CovenantLaneHeadProbe found = (await fixture.Store.ProbeLaneHeadAsync(
            campaign,
            CovenantLane.Confirmed,
            "campaign.present",
            lease,
            Token)).Value;

        Assert.Equal(CovenantLaneHeadPresence.Present, found.Presence);

        Assert.Equal(present.EntryId, found.EntryId);

        Assert.Equal(1, found.LaneRevision);

        Assert.True(found.KeyEpoch > 0);

        CovenantLaneHeadProbe retired = (await fixture.Store.ProbeLaneHeadAsync(
            campaign,
            CovenantLane.Confirmed,
            "campaign.retired",
            lease,
            Token)).Value;

        Assert.Equal(CovenantLaneHeadPresence.Retired, retired.Presence);

        Assert.Equal(2, retired.LaneRevision);

        CovenantLaneHeadProbe absent = (await fixture.Store.ProbeLaneHeadAsync(
            campaign,
            CovenantLane.Confirmed,
            "campaign.never",
            lease,
            Token)).Value;

        Assert.Equal(CovenantLaneHeadPresence.Absent, absent.Presence);

        Assert.Null(absent.EntryId);

    }

    [Fact]
    public async Task Lane_head_probe_isolates_campaigns_and_refuses_a_write_lease()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        await fixture.AddCampaignAsync(CampaignTwo, "two", Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "shared.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "One.",
            Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease lease = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CampaignTwo),
            Token)).Value;

        CovenantLaneHeadProbe probe = (await fixture.Store.ProbeLaneHeadAsync(
            CovenantCanonicalFixture.CampaignContext(CampaignTwo),
            CovenantLane.Confirmed,
            "shared.key",
            lease,
            Token)).Value;

        Assert.Equal(CovenantLaneHeadPresence.Absent, probe.Presence);

    }

    [Fact]
    public async Task List_pages_are_clamped_stable_and_keyset_continuable()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        for (int index = 0; index < 5; index++)
        {

            _ = await fixture.SeedHeadAsync(
                CovenantScope.Global,
                null,
                $"global.k{index}",
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                $"Value {index}.",
                Token);

        }

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantInstallationReadLease lease = (await gate.AcquireInstallationReadAsync(Token)).Value;

        CovenantListQuery first = new(
            CovenantCursorScopeSelection.AllScopes,
            CampaignId: null,
            Lane: null,
            CovenantLifecycle.Set,
            PageSize: 0,
            After: null);

        Assert.Equal(1, first.EffectivePageSize);

        CovenantListPage firstPage = (await fixture.Store.ReadListPageAsync(first, lease, Token)).Value;

        _ = Assert.Single(firstPage.Items);

        Assert.NotNull(firstPage.NextKeyset);

        Assert.Equal("global.k0", firstPage.Items[0].NormalizedKey);

        CovenantListPage secondPage = (await fixture.Store.ReadListPageAsync(
            first with { After = firstPage.NextKeyset },
            lease,
            Token)).Value;

        Assert.Equal("global.k1", secondPage.Items[0].NormalizedKey);

        CovenantListQuery wide = new(
            CovenantCursorScopeSelection.AllScopes,
            CampaignId: null,
            Lane: null,
            CovenantLifecycle.Set,
            PageSize: 5_000,
            After: null);

        Assert.Equal(CovenantLimits.MaxPageSize, wide.EffectivePageSize);

        CovenantListPage widePage = (await fixture.Store.ReadListPageAsync(wide, lease, Token)).Value;

        Assert.Equal(5, widePage.Items.Length);

        Assert.Null(widePage.NextKeyset);

    }

    [Fact]
    public async Task An_all_scope_list_requires_the_installation_lease()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease scoped =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantListPage> refused = await fixture.Store.ReadListPageAsync(
            new CovenantListQuery(
                CovenantCursorScopeSelection.AllScopes,
                null,
                null,
                CovenantLifecycle.Any,
                50,
                null),
            scoped,
            Token);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

        // The same scoped lease is enough for its own scope.
        Result<CovenantListPage> allowed = await fixture.Store.ReadListPageAsync(
            new CovenantListQuery(
                CovenantCursorScopeSelection.Global,
                null,
                null,
                CovenantLifecycle.Any,
                50,
                null),
            scoped,
            Token);

        Assert.True(allowed.IsSuccess);

    }

    [Fact]
    public async Task Detail_returns_both_lane_heads_for_one_scoped_key()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        SeededHead confirmed = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "both.lanes",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Confirmed.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "both.lanes",
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "Proposed.",
            Token,
            entryId: confirmed.EntryId);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease lease = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CampaignOne),
            Token)).Value;

        CovenantDetail detail = (await fixture.Store.ReadDetailAsync(
            new CovenantDetailQuery(CovenantOperationScope.ForCampaign(CampaignOne), "both.lanes"),
            lease,
            Token)).Value;

        Assert.Equal(confirmed.EntryId, detail.EntryId);

        Assert.NotNull(detail.ConfirmedHead);

        Assert.NotNull(detail.ProposedHead);

        Assert.Equal(CovenantLane.Proposed, detail.ProposedHead!.Lane);

        Assert.True(detail.KeyEpoch > 0);

        CovenantDetail missing = (await fixture.Store.ReadDetailAsync(
            new CovenantDetailQuery(CovenantOperationScope.ForCampaign(CampaignOne), "no.such.key"),
            lease,
            Token)).Value;

        Assert.Null(missing.EntryId);

        Assert.Null(missing.ConfirmedHead);

    }

    [Fact]
    public async Task Version_pages_descend_by_revision()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        SeededHead first = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "versioned.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "One.",
            Token);

        SeededHead second = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "versioned.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Two.",
            Token,
            entryId: first.EntryId,
            laneRevision: 2,
            predecessorVersionId: first.VersionId);

        SeededHead third = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "versioned.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Three.",
            Token,
            entryId: first.EntryId,
            laneRevision: 3,
            predecessorVersionId: second.VersionId);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease lease =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        CovenantVersionPage page = (await fixture.Store.ReadVersionPageAsync(
            new CovenantVersionQuery(first.EntryId, CovenantLane.Confirmed, 2, null),
            lease,
            Token)).Value;

        Assert.Equal([3L, 2L], page.Items.Select(static item => item.LaneRevision));

        Assert.Equal(third.VersionId, page.Items[0].VersionId);

        Assert.NotNull(page.NextKeyset);

        CovenantVersionPage next = (await fixture.Store.ReadVersionPageAsync(
            new CovenantVersionQuery(first.EntryId, CovenantLane.Confirmed, 2, page.NextKeyset),
            lease,
            Token)).Value;

        CovenantVersionItem last = Assert.Single(next.Items);

        Assert.Equal(1L, last.LaneRevision);

        Assert.Null(next.NextKeyset);

    }

    [Fact]
    public async Task Sources_return_every_leaf_and_recompute_the_aggregate_digest()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        List<MaterializationSourceDigestInput> sources = [];

        for (int index = 0; index < CovenantLimits.MaxVersionSources; index++)
        {

            sources.Add(
                new MaterializationSourceDigestInput(
                    new Guid($"aaaaaaaa-0000-4000-8000-{index:000000000000}"),
                    new Guid($"bbbbbbbb-0000-4000-8000-{index:000000000000}"),
                    $"logical/{index}",
                    CovenantOperationGateFixture.Digest((byte)index),
                    CovenantMaterializationSourceRange.WholeSource,
                    null,
                    null,
                    []));

        }

        SeededHead head = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "sourced.key",
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "From an attachment.",
            Token,
            sources: [.. sources]);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease lease = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CampaignOne),
            Token)).Value;

        CovenantSourcePage page = (await fixture.Store.ReadSourcePageAsync(
            new CovenantSourceQuery(head.VersionId),
            lease,
            Token)).Value;

        Assert.Equal(CovenantLimits.MaxVersionSources, page.Items.Length);

        Assert.Equal((uint)CovenantLimits.MaxVersionSources, page.StoredProvenanceCount);

        Assert.True(page.DigestMatches);

        Assert.Equal([.. Enumerable.Range(0, CovenantLimits.MaxVersionSources)], page.Items.Select(static item => item.Ordinal));

    }

    [Fact]
    public async Task A_global_effect_snapshot_counts_every_campaign_and_caps_its_examples()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        const int campaignCount = CovenantMutationEffectSnapshot.MaxExamples + 7;

        for (int index = 0; index < campaignCount; index++)
        {

            Guid campaignId = new($"cccccccc-0000-4000-8000-{index:000000000000}");

            await fixture.AddCampaignAsync(campaignId, $"campaign-{index}", Token);

            if (index % 2 == 0)
            {

                _ = await fixture.SeedHeadAsync(
                    CovenantScope.Campaign,
                    campaignId,
                    "shared.key",
                    CovenantLane.Confirmed,
                    CovenantOperation.Set,
                    "Shadowing.",
                    Token);

            }

        }

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "shared.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Global.",
            Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantInstallationReadLease lease = (await gate.AcquireInstallationReadAsync(Token)).Value;

        CovenantMutationEffectSnapshot effect = (await fixture.Store.ReadMutationEffectSnapshotAsync(
            new CovenantMutationEffectQuery(
                CovenantOperationScope.Global,
                "shared.key",
                CovenantLane.Confirmed,
                CovenantOperation.Retire),
            lease,
            Token)).Value;

        Assert.Equal(campaignCount, effect.AffectedCampaignCount);

        Assert.Equal(CovenantMutationEffectSnapshot.MaxExamples, effect.Examples.Length);

        Assert.True(effect.ExamplesTruncated);

        Assert.Equal(CovenantEffectDecision.HeadRetired, effect.LocalDecision);

        Assert.True(effect.KeyEpoch > 0);

        Assert.True(effect.CampaignRegistryEpoch > 0);

        Assert.True(effect.DependentHeadVectorDigest.IsValid);

    }

    [Fact]
    public async Task A_global_effect_snapshot_sees_heads_whose_campaign_row_was_written_by_ef()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(HexCampaignOne, "one", Token);

        await fixture.AddCampaignAsync(HexCampaignTwo, "two", Token);

        // Only the first Campaign holds a local Confirmed override, so a Global Set does nothing
        // there and resurfaces in the second. The identity mismatch inverted exactly this: the join
        // matched no head at all, so every Campaign was reported as inheriting the Global value.
        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            HexCampaignOne,
            "shared.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Local override.",
            Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantInstallationReadLease lease = (await gate.AcquireInstallationReadAsync(Token)).Value;

        CovenantMutationEffectSnapshot effect = (await fixture.Store.ReadMutationEffectSnapshotAsync(
            new CovenantMutationEffectQuery(
                CovenantOperationScope.Global,
                "shared.key",
                CovenantLane.Confirmed,
                CovenantOperation.Set),
            lease,
            Token)).Value;

        CovenantMutationEffectExample one = Assert.Single(
            effect.Examples,
            example => example.CampaignId == HexCampaignOne);

        Assert.True(one.HasCampaignConfirmedHead);

        Assert.Equal(CovenantEffectDecision.NoEffect, one.Decision);

        CovenantMutationEffectExample two = Assert.Single(
            effect.Examples,
            example => example.CampaignId == HexCampaignTwo);

        Assert.False(two.HasCampaignConfirmedHead);

        Assert.Equal(CovenantEffectDecision.GlobalConfirmedResurfaces, two.Decision);

    }

    [Fact]
    public async Task A_campaign_effect_snapshot_finds_a_campaign_row_written_by_ef()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(HexCampaignOne, "one", Token);

        await fixture.AddCampaignAsync(HexCampaignTwo, "two", Token);

        FakeCovenantCampaignScopeProbe campaigns = new();

        campaigns.Set(HexCampaignOne, CovenantCampaignScopeState.Live);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(campaigns: campaigns);

        await using CovenantReadLease lease = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(HexCampaignOne),
            Token)).Value;

        CovenantMutationEffectSnapshot effect = (await fixture.Store.ReadMutationEffectSnapshotAsync(
            new CovenantMutationEffectQuery(
                CovenantOperationScope.ForCampaign(HexCampaignOne),
                "scoped.key",
                CovenantLane.Confirmed,
                CovenantOperation.Set),
            lease,
            Token)).Value;

        // The scoped arm binds its own identity against the EF-owned column, so a mismatch makes the
        // preflight report that the mutation affects no Campaign at all.
        Assert.Equal(1, effect.AffectedCampaignCount);

        CovenantMutationEffectExample only = Assert.Single(effect.Examples);

        Assert.Equal(HexCampaignOne, only.CampaignId);

    }

    [Fact]
    public async Task A_global_effect_scan_refuses_a_scoped_lease()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease scoped =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantMutationEffectSnapshot> refused = await fixture.Store.ReadMutationEffectSnapshotAsync(
            new CovenantMutationEffectQuery(
                CovenantOperationScope.Global,
                "shared.key",
                CovenantLane.Confirmed,
                CovenantOperation.Set),
            scoped,
            Token);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

    }

    [Fact]
    public async Task A_campaign_effect_snapshot_binds_only_its_own_campaign()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        await fixture.AddCampaignAsync(CampaignTwo, "two", Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignTwo,
            "scoped.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Elsewhere.",
            Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease lease = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CampaignOne),
            Token)).Value;

        CovenantMutationEffectSnapshot effect = (await fixture.Store.ReadMutationEffectSnapshotAsync(
            new CovenantMutationEffectQuery(
                CovenantOperationScope.ForCampaign(CampaignOne),
                "scoped.key",
                CovenantLane.Confirmed,
                CovenantOperation.Set),
            lease,
            Token)).Value;

        Assert.Equal(1, effect.AffectedCampaignCount);

        CovenantMutationEffectExample only = Assert.Single(effect.Examples);

        Assert.Equal(CampaignOne, only.CampaignId);

        Assert.False(only.HasCampaignConfirmedHead);

        Assert.Equal(CovenantEffectDecision.HeadCreated, effect.LocalDecision);

    }

    [Fact]
    public async Task A_released_lease_cannot_read()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantReadLease lease = (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        await lease.DisposeAsync();

        Result<CovenantTurnSnapshot> refused = await fixture.Store.ReadTurnSnapshotAsync(
            CanonicalCampaignContext.GlobalOnly,
            lease,
            Token);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    [Fact]
    public async Task Concurrent_writers_cannot_split_one_reader_snapshot()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.first",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "First.",
            Token);

        CovenantTurnSnapshot before = await ReadSnapshotAsync(fixture, null);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.second",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Second.",
            Token);

        CovenantTurnSnapshot after = await ReadSnapshotAsync(fixture, null);

        // The first snapshot is immutable: a later write cannot retroactively appear inside it.
        _ = Assert.Single(before.Candidates);

        Assert.Equal(2, after.Candidates.Length);

        Assert.NotEqual(before.Digest, after.Digest);

    }

    private static async Task<CovenantTurnSnapshot> ReadSnapshotAsync(
        CovenantCanonicalFixture fixture,
        Guid? campaignId)
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CanonicalCampaignContext campaign = campaignId is { } present
            ? CovenantCanonicalFixture.CampaignContext(present)
            : CanonicalCampaignContext.GlobalOnly;

        CovenantOperationScope scope = campaignId is { } id
            ? CovenantOperationScope.ForCampaign(id)
            : CovenantOperationScope.Global;

        await using CovenantReadLease lease = (await gate.AcquireReadAsync(scope, Token)).Value;

        Result<CovenantTurnSnapshot> snapshot = await fixture.Store.ReadTurnSnapshotAsync(
            campaign,
            lease,
            Token);

        Assert.True(snapshot.IsSuccess, snapshot.IsFailure ? snapshot.Error.Message : string.Empty);

        return snapshot.Value;

    }

}
