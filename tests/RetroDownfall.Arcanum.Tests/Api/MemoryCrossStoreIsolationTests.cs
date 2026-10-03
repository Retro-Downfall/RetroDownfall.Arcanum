using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Every per-item memory verb, driven through its mapped route, leaves the other two stores and the
/// shared ledger byte-identical.
/// </summary>
/// <remarks>
/// <para>Each row starts a host holding all three stores, written by their production writers: two
/// Global Saga memories through the store's insert, a Global and a Campaign Lexicon entry through the
/// host's Lexicon service, and two Global Covenant keys through the set routes. The row then writes its
/// own precondition the same way (a retire before a reinstate, a pin before an unpin, a mask before an
/// unmask, an erase before a release), and only then is the first snapshot taken.</para>
///
/// <para>Saga extraction stays off, so no background pass can write a Saga row while a row runs. The
/// Covenant search projection is drained to a settled state before each snapshot, so a Covenant
/// write's projection lands inside its own row rather than in whichever row the maintenance pass
/// happens to meet.</para>
///
/// <para>These rows characterise production code that is expected to be isolated. A row that fails is
/// a cross-store write to report, not one to work around here.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class MemoryCrossStoreIsolationTests
{
    private const string SagaOneContent = "The operator keeps the lighthouse log in blue ink.";

    private const string SagaTwoContent = "The operator rows to the island every second Tuesday.";

    private const string GlobalEntry = "Iso Warden";

    private const string CampaignEntry = "Iso Keeper";

    private const string KeyA = "iso.a";

    private const string KeyB = "iso.b";

    private const string Fingerprints = "memory_erasure_fingerprints";

    private static readonly MemoryStoreFamily[] Families =
    [
        MemoryStoreFamily.Saga,
        MemoryStoreFamily.Lexicon,
        MemoryStoreFamily.Covenant,
        MemoryStoreFamily.Shared,
    ];

    private static readonly MemoryMutationCase[] Verbs =
    [
        new("saga-correct", MemoryStoreFamily.Saga, NoPrecondition, static world => CorrectSagaAsync(world)),
        new("saga-retire", MemoryStoreFamily.Saga, NoPrecondition, static world => SagaHashVerbAsync(world, "retire")),
        new("saga-reinstate", MemoryStoreFamily.Saga, static world => SagaHashVerbAsync(world, "retire"), static world => SagaHashVerbAsync(world, "reinstate")),
        new("saga-pin", MemoryStoreFamily.Saga, NoPrecondition, static world => SagaPinVerbAsync(world, "pin")),
        new("saga-unpin", MemoryStoreFamily.Saga, static world => SagaPinVerbAsync(world, "pin"), static world => SagaPinVerbAsync(world, "unpin")),
        new("lexicon-correct", MemoryStoreFamily.Lexicon, NoPrecondition, static world => CorrectLexiconAsync(world)),
        new("lexicon-retire", MemoryStoreFamily.Lexicon, NoPrecondition, static world => LexiconVerbAsync(world, "retire")),
        new("lexicon-reinstate", MemoryStoreFamily.Lexicon, static world => LexiconVerbAsync(world, "retire"), static world => LexiconVerbAsync(world, "reinstate")),
        new("lexicon-pin", MemoryStoreFamily.Lexicon, NoPrecondition, static world => LexiconVerbAsync(world, "pin")),
        new("lexicon-unpin", MemoryStoreFamily.Lexicon, static world => LexiconVerbAsync(world, "pin"), static world => LexiconVerbAsync(world, "unpin")),
        new("covenant-set", MemoryStoreFamily.Covenant, NoPrecondition, static world => SetCovenantAsync(world)),
        new("covenant-retire", MemoryStoreFamily.Covenant, NoPrecondition, static world => RetireCovenantAsync(world)),
        new("covenant-correct", MemoryStoreFamily.Covenant, NoPrecondition, static world => CorrectCovenantAsync(world)),
        new("covenant-pin", MemoryStoreFamily.Covenant, NoPrecondition, static world => CurateAsync(world, CovenantCurationKind.Pin, CovenantScope.Global, null)),
        new("covenant-unpin", MemoryStoreFamily.Covenant, static world => CurateAsync(world, CovenantCurationKind.Pin, CovenantScope.Global, null), static world => CurateAsync(world, CovenantCurationKind.Unpin, CovenantScope.Global, null)),
        new("covenant-mask", MemoryStoreFamily.Covenant, NoPrecondition, static world => CurateAsync(world, CovenantCurationKind.Mask, CovenantScope.Campaign, world.Campaign)),
        new("covenant-unmask", MemoryStoreFamily.Covenant, static world => CurateAsync(world, CovenantCurationKind.Mask, CovenantScope.Campaign, world.Campaign), static world => CurateAsync(world, CovenantCurationKind.Unmask, CovenantScope.Campaign, world.Campaign)),
        new("saga-review-apply", MemoryStoreFamily.Saga, NoPrecondition, static world => ReviewSagaAsync(world)),
        new("lexicon-review-apply", MemoryStoreFamily.Lexicon, NoPrecondition, static world => ReviewLexiconAsync(world)),
        new("covenant-review-apply", MemoryStoreFamily.Covenant, NoPrecondition, static world => ReviewCovenantAsync(world)),
        new("saga-erase", MemoryStoreFamily.Saga, NoPrecondition, static world => EraseAsync(world, MemoryReviewStore.Saga)),
        new("lexicon-erase", MemoryStoreFamily.Lexicon, NoPrecondition, static world => EraseAsync(world, MemoryReviewStore.Lexicon)),
        new("covenant-erase", MemoryStoreFamily.Covenant, NoPrecondition, static world => EraseAsync(world, MemoryReviewStore.Covenant)),
        new("saga-release", MemoryStoreFamily.Saga, static world => EraseAsync(world, MemoryReviewStore.Saga), static world => ReleaseAsync(world, MemoryReviewStore.Saga)),
        new("lexicon-release", MemoryStoreFamily.Lexicon, static world => EraseAsync(world, MemoryReviewStore.Lexicon), static world => ReleaseAsync(world, MemoryReviewStore.Lexicon)),
        new("covenant-release", MemoryStoreFamily.Covenant, static world => EraseAsync(world, MemoryReviewStore.Covenant), static world => ReleaseAsync(world, MemoryReviewStore.Covenant)),
        new("legacy-saga-delete", MemoryStoreFamily.Saga, NoPrecondition, static world => DeleteAsync(world, $"/api/saga/{world.SagaOneId}")),
        new("legacy-lexicon-delete", MemoryStoreFamily.Lexicon, NoPrecondition, static world => DeleteAsync(world, $"/api/memory/lexicon/{Uri.EscapeDataString(GlobalEntry)}")),
    ];

    /// <summary>How long one row may run before every request and wait it makes is cancelled.</summary>
    private static readonly TimeSpan RowDeadline = TimeSpan.FromMinutes(3);

    public static TheoryData<string> Cases
    {
        get
        {
            TheoryData<string> cases = [];

            foreach (MemoryMutationCase verb in Verbs)
            {
                cases.Add(verb.Name);
            }

            return cases;
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public async Task A_per_item_verb_changes_only_its_own_store(string row)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        MemoryMutationCase verb = Assert.Single(Verbs, candidate => candidate.Name == row);

        using CancellationTokenSource deadline = new(RowDeadline);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        IsolationWorld world = await SeedAsync(host, deadline.Token);

        await verb.Arrange(world);

        await MemoryStoreSnapshot.QuiesceAsync(host.Services, world.Token);

        MemoryStoreSnapshot before = await CaptureAsync(world);

        await verb.Act(world);

        await MemoryStoreSnapshot.QuiesceAsync(host.Services, world.Token);

        MemoryStoreSnapshot.AssertOnlyChanged(before, await CaptureAsync(world), verb.Target);
    }

    /// <summary>
    /// An erase records exactly one fingerprint, in its own store's partition, and a release lifts
    /// exactly that one, while the fingerprints the other stores already hold stay row for row.
    /// </summary>
    [SkippableTheory]
    [InlineData(MemoryReviewStore.Saga)]
    [InlineData(MemoryReviewStore.Lexicon)]
    [InlineData(MemoryReviewStore.Covenant)]
    public async Task Erase_and_release_touch_only_their_own_fingerprint_partition(MemoryReviewStore store)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using CancellationTokenSource deadline = new(RowDeadline);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        IsolationWorld world = await SeedAsync(host, deadline.Token);

        MemoryStoreFamily target = FamilyOf(store);

        // Every other store already holds a fingerprint of its own, of an item this test never names
        // again, so "left alone" is a statement about rows that exist.
        foreach (MemoryReviewStore other in (MemoryReviewStore[])[MemoryReviewStore.Saga, MemoryReviewStore.Lexicon, MemoryReviewStore.Covenant])
        {
            if (other != store)
            {
                await EraseBystanderAsync(world, other);
            }
        }

        await MemoryStoreSnapshot.QuiesceAsync(host.Services, world.Token);

        MemoryStoreSnapshot before = await CaptureAsync(world);

        foreach (MemoryStoreFamily family in Families.Where(family => family != target && family != MemoryStoreFamily.Shared))
        {
            Assert.Equal(1, before.CountRows(family, Fingerprints));
        }

        await EraseAsync(world, store);

        await MemoryStoreSnapshot.QuiesceAsync(host.Services, world.Token);

        MemoryStoreSnapshot erased = await CaptureAsync(world);

        MemoryStoreSnapshot.AssertOnlyChanged(before, erased, target);

        Assert.Equal(before.CountRows(target, Fingerprints) + 1, erased.CountRows(target, Fingerprints));

        MemoryErasureReleaseResultDto released = await ReleaseAsync(world, store);

        Assert.Equal(store, released.Store);

        Assert.Equal(MemoryErasureReleaseOutcome.Released, released.Outcome);

        Assert.Equal(1, released.ReleasedCount);

        await MemoryStoreSnapshot.QuiesceAsync(host.Services, world.Token);

        MemoryStoreSnapshot after = await CaptureAsync(world);

        MemoryStoreSnapshot.AssertOnlyChanged(erased, after, target);

        Assert.Equal(erased.CountRows(target, Fingerprints) - 1, after.CountRows(target, Fingerprints));

        foreach (MemoryStoreFamily family in Families.Where(family => family != target))
        {
            Assert.Equal(FingerprintRows(before, family), FingerprintRows(erased, family));

            Assert.Equal(FingerprintRows(before, family), FingerprintRows(after, family));
        }
    }

    private static Task NoPrecondition(IsolationWorld world) => Task.CompletedTask;

    /// <summary>
    /// Writes every store's baseline through its production writer, and proves no extraction pass is
    /// configured to write a Saga row behind the test's back.
    /// </summary>
    private static async Task<IsolationWorld> SeedAsync(ArcanumWebApplicationFactory host, CancellationToken ct)
    {
        Assert.False(host.Services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue.Features.SagaExtraction);

        MemoryErasureRouteDriver driver = new(host.CreateClient());

        HttpClient client = host.CreateAuthenticatedClient();

        Guid campaign = await RegisterCampaignAsync(host, client, ct);

        string one = await MemoryErasureRouteDriver.InsertSagaAsync(host, SagaOneContent, ct: ct);

        string two = await MemoryErasureRouteDriver.InsertSagaAsync(host, SagaTwoContent, ct: ct);

        await ScribeAsync(host, GlobalEntry, null, ["keeps the harbour light", "counts the gulls"], ct);

        await ScribeAsync(host, CampaignEntry, campaign, ["holds the island key"], ct);

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, KeyA, "Answer in British English.", ct);

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, KeyB, "Prefer short paragraphs.", ct);

        return new IsolationWorld(host, driver, client, campaign, one, two, ct);
    }

    /// <summary>Snapshots every memory table on a fresh read-only connection of the host's own.</summary>
    private static async Task<MemoryStoreSnapshot> CaptureAsync(IsolationWorld world)
    {
        Result<IGrimoireOrdinaryConnectionLease> opened = await world.Host.Services
            .GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, world.Token);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

        await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

        return await MemoryStoreSnapshot.CaptureAsync(lease.Connection, world.Token);
    }

    private static MemoryStoreFamily FamilyOf(MemoryReviewStore store) =>
        store switch
        {
            MemoryReviewStore.Saga => MemoryStoreFamily.Saga,
            MemoryReviewStore.Lexicon => MemoryStoreFamily.Lexicon,
            MemoryReviewStore.Covenant => MemoryStoreFamily.Covenant,
            _ => throw new ArgumentOutOfRangeException(nameof(store), store, "A memory store is required."),
        };

    private static string[] FingerprintRows(MemoryStoreSnapshot snapshot, MemoryStoreFamily family) =>
        [.. snapshot.Rows[family].Where(static row => row.StartsWith(Fingerprints + ":", StringComparison.Ordinal))];

    private static async Task CorrectSagaAsync(IsolationWorld world)
    {
        SagaMemoryDetail detail = await ShowSagaAsync(world, world.SagaOneId);

        SagaCurationResult result = await PostOkAsync(
            world,
            $"/api/memory/saga/{world.SagaOneId}/correct",
            new SagaCorrectRequest(detail.ContentHash, "The operator keeps the lighthouse log in green ink."),
            ArcanumJsonContext.Default.SagaCorrectRequest,
            ArcanumJsonContext.Default.ApiResponseSagaCurationResult);

        Assert.Equal(SagaCurationOutcomeKind.Applied, result.Outcome);
    }

    private static async Task SagaHashVerbAsync(IsolationWorld world, string verb)
    {
        SagaMemoryDetail detail = await ShowSagaAsync(world, world.SagaOneId);

        string path = $"/api/memory/saga/{world.SagaOneId}/{verb}";

        SagaCurationResult result = verb == "retire"
            ? await PostOkAsync(
                world,
                path,
                new SagaRetireRequest(detail.ContentHash),
                ArcanumJsonContext.Default.SagaRetireRequest,
                ArcanumJsonContext.Default.ApiResponseSagaCurationResult)
            : await PostOkAsync(
                world,
                path,
                new SagaReinstateRequest(detail.ContentHash),
                ArcanumJsonContext.Default.SagaReinstateRequest,
                ArcanumJsonContext.Default.ApiResponseSagaCurationResult);

        Assert.Equal(SagaCurationOutcomeKind.Applied, result.Outcome);
    }

    private static async Task SagaPinVerbAsync(IsolationWorld world, string verb)
    {
        using HttpResponseMessage response = await world.Client.PostAsync($"/api/memory/saga/{world.SagaOneId}/{verb}", content: null, world.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SagaCurationResult result = await MemoryErasureRouteDriver.ReadDataAsync(response, ArcanumJsonContext.Default.ApiResponseSagaCurationResult);

        Assert.Equal(SagaCurationOutcomeKind.Applied, result.Outcome);
    }

    private static async Task<SagaMemoryDetail> ShowSagaAsync(IsolationWorld world, string id)
    {
        using HttpResponseMessage shown = await world.Client.GetAsync($"/api/memory/saga/{id}", world.Token);

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(shown, ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail);
    }

    private static async Task CorrectLexiconAsync(IsolationWorld world)
    {
        LexiconCurationTarget target = (await ShowLexiconAsync(world, GlobalEntry, null)).Target;

        LexiconCurationResult result = await PostOkAsync(
            world,
            "/api/memory/lexicon/correct",
            new LexiconCorrectRequest(target, new LexiconReplacementContent("Place", ["keeps the harbour light", "rings the fog bell"])),
            ArcanumJsonContext.Default.LexiconCorrectRequest,
            ArcanumJsonContext.Default.ApiResponseLexiconCurationResult);

        Assert.Equal(LexiconCurationOutcomeKind.Applied, result.Outcome);
    }

    private static async Task LexiconVerbAsync(IsolationWorld world, string verb)
    {
        LexiconCurationTarget target = (await ShowLexiconAsync(world, GlobalEntry, null)).Target;

        string path = $"/api/memory/lexicon/{verb}";

        JsonTypeInfo<ApiResponse<LexiconCurationResult>> result = ArcanumJsonContext.Default.ApiResponseLexiconCurationResult;

        LexiconCurationResult curated = verb switch
        {
            "retire" => await PostOkAsync(world, path, new LexiconRetireRequest(target), ArcanumJsonContext.Default.LexiconRetireRequest, result),
            "reinstate" => await PostOkAsync(world, path, new LexiconReinstateRequest(target), ArcanumJsonContext.Default.LexiconReinstateRequest, result),
            "pin" => await PostOkAsync(world, path, new LexiconPinRequest(target), ArcanumJsonContext.Default.LexiconPinRequest, result),
            _ => await PostOkAsync(world, path, new LexiconUnpinRequest(target), ArcanumJsonContext.Default.LexiconUnpinRequest, result),
        };

        Assert.Equal(LexiconCurationOutcomeKind.Applied, curated.Outcome);
    }

    private static Task<LexiconEntryDetail> ShowLexiconAsync(IsolationWorld world, string name, Guid? campaignId) =>
        PostOkAsync(
            world,
            "/api/memory/lexicon/show",
            new LexiconShowRequest(name, ScopeOf(campaignId)),
            ArcanumJsonContext.Default.LexiconShowRequest,
            ArcanumJsonContext.Default.ApiResponseLexiconEntryDetail);

    private static async Task SetCovenantAsync(IsolationWorld world)
    {
        CovenantMutationResultDto result = await world.Driver.SetCovenantAsync(CovenantScope.Global, null, KeyA, "Answer in plain English.", world.Token);

        Assert.Equal(CovenantMutationOutcome.Applied, result.Outcome);
    }

    private static async Task RetireCovenantAsync(IsolationWorld world)
    {
        CovenantHeadDto head = Assert.IsType<CovenantHeadDto>((await DetailAsync(world, CovenantScope.Global, null, KeyB)).Confirmed);

        CovenantRetirePrepareRequest prepare = new(CovenantScope.Global, null, KeyB, CovenantLane.Confirmed, head.LaneRevision, Guid.CreateVersion7());

        CovenantMutationPreflightDto preflight = await PostOkAsync(
            world,
            "/api/memory/covenant/retire/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantRetirePrepareRequest,
            ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto);

        CovenantMutationResultDto result = await PostOkAsync(
            world,
            "/api/memory/covenant/retire",
            new CovenantRetireRequest(prepare.Scope, prepare.CampaignId, prepare.Key, prepare.Lane, prepare.ExpectedRevision, prepare.MutationId, preflight.PreflightToken),
            ArcanumJsonContext.Default.CovenantRetireRequest,
            ArcanumJsonContext.Default.ApiResponseCovenantMutationResultDto);

        Assert.Equal(CovenantMutationOutcome.Applied, result.Outcome);
    }

    private static async Task CorrectCovenantAsync(IsolationWorld world)
    {
        CovenantHeadDto head = Assert.IsType<CovenantHeadDto>((await DetailAsync(world, CovenantScope.Global, null, KeyA)).Confirmed);

        CovenantCorrectPrepareRequest prepare = new(
            CovenantScope.Global,
            null,
            KeyA,
            "Answer in Scottish English.",
            head.VersionId,
            CovenantLane.Confirmed,
            head.LaneRevision,
            Assert.IsType<string>(head.RenderedHash),
            Guid.CreateVersion7());

        CovenantMutationPreflightDto preflight = await PostOkAsync(
            world,
            "/api/memory/covenant/correct/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantCorrectPrepareRequest,
            ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto);

        CovenantMutationResultDto result = await PostOkAsync(
            world,
            "/api/memory/covenant/correct",
            new CovenantCorrectRequest(
                prepare.Scope,
                prepare.CampaignId,
                prepare.Key,
                prepare.Content,
                prepare.TargetVersionId,
                prepare.TargetLane,
                prepare.ExpectedRevision,
                prepare.TargetRenderedHash,
                prepare.MutationId,
                preflight.PreflightToken),
            ArcanumJsonContext.Default.CovenantCorrectRequest,
            ArcanumJsonContext.Default.ApiResponseCovenantMutationResultDto);

        Assert.Equal(CovenantMutationOutcome.Applied, result.Outcome);
    }

    /// <summary>
    /// Prepares and commits one curation change of <c>iso.a</c>'s Confirmed lane at whatever curation
    /// revision the lane holds now, the way the CLI does.
    /// </summary>
    private static async Task CurateAsync(IsolationWorld world, CovenantCurationKind kind, CovenantScope scope, Guid? campaignId)
    {
        CovenantCurationPrepareRequest prepare = new(kind, scope, campaignId, KeyA, CovenantLane.Confirmed, 0, Guid.CreateVersion7());

        CovenantCurationPreflightDto preflight = await PrepareCurationAsync(world, prepare);

        if (preflight.CurrentRevision != preflight.ExpectedRevision)
        {
            prepare = prepare with { ExpectedRevision = preflight.CurrentRevision };

            preflight = await PrepareCurationAsync(world, prepare);
        }

        CovenantCurationResultDto result = await PostOkAsync(
            world,
            "/api/memory/covenant/curate",
            new CovenantCurationRequest(
                prepare.Kind,
                prepare.Scope,
                prepare.CampaignId,
                prepare.Key,
                prepare.Lane,
                prepare.ExpectedRevision,
                prepare.MutationId,
                preflight.PreflightToken),
            ArcanumJsonContext.Default.CovenantCurationRequest,
            ArcanumJsonContext.Default.ApiResponseCovenantCurationResultDto);

        Assert.Equal(CovenantMutationOutcome.Applied, result.Outcome);
    }

    private static Task<CovenantCurationPreflightDto> PrepareCurationAsync(IsolationWorld world, CovenantCurationPrepareRequest prepare) =>
        PostOkAsync(
            world,
            "/api/memory/covenant/curate/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantCurationPrepareRequest,
            ArcanumJsonContext.Default.ApiResponseCovenantCurationPreflightDto);

    private static Task<CovenantDetailDto> DetailAsync(IsolationWorld world, CovenantScope scope, Guid? campaignId, string key) =>
        PostOkAsync(
            world,
            "/api/memory/covenant/detail",
            new CovenantDetailRequest(scope, campaignId, key),
            ArcanumJsonContext.Default.CovenantDetailRequest,
            ArcanumJsonContext.Default.ApiResponseCovenantDetailDto);

    /// <summary>A bulk review of the Global Saga queue: one memory retired, the other pinned.</summary>
    private static async Task ReviewSagaAsync(IsolationWorld world)
    {
        await ReviewSagaOnceAsync(world, MemoryReviewAction.Retire, world.SagaOneId);

        await ReviewSagaOnceAsync(world, MemoryReviewAction.Pin, world.SagaTwoId);
    }

    private static async Task ReviewSagaOnceAsync(IsolationWorld world, MemoryReviewAction action, string memoryId)
    {
        SagaReviewPageDto page = await PostOkAsync(
            world,
            "/api/memory/saga/review/list",
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 50, null),
            ArcanumJsonContext.Default.SagaReviewListRequest,
            ArcanumJsonContext.Default.ApiResponseSagaReviewPageDto);

        SagaReviewItemDto item = Assert.Single(
            page.Items,
            candidate => candidate.IsCurrent && string.Equals(candidate.SubjectId, memoryId, StringComparison.OrdinalIgnoreCase));

        SagaReviewBulkPrepareRequest prepare = new(
            Guid.CreateVersion7(),
            SagaMemoryScopeKind.Global,
            null,
            action,
            [new SagaReviewDecision(item.ObservationToken, null)]);

        MemoryReviewBulkPlanDto plan = await PostOkAsync(
            world,
            "/api/memory/saga/review/prepare",
            prepare,
            ArcanumJsonContext.Default.SagaReviewBulkPrepareRequest,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto);

        MemoryReviewBulkResultDto result = await PostOkAsync(
            world,
            "/api/memory/saga/review/apply",
            new SagaReviewBulkApplyRequest(prepare, plan.PreparedPlanToken),
            ArcanumJsonContext.Default.SagaReviewBulkApplyRequest,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);

        AssertReviewApplied(result, MemoryReviewStore.Saga, action);
    }

    /// <summary>A bulk review of both Lexicon queues: the Global entry retired, the Campaign entry pinned.</summary>
    private static async Task ReviewLexiconAsync(IsolationWorld world)
    {
        await ReviewLexiconOnceAsync(world, MemoryReviewAction.Retire, GlobalEntry, null);

        await ReviewLexiconOnceAsync(world, MemoryReviewAction.Pin, CampaignEntry, world.Campaign);
    }

    private static async Task ReviewLexiconOnceAsync(IsolationWorld world, MemoryReviewAction action, string name, Guid? campaignId)
    {
        Guid entryId = (await ShowLexiconAsync(world, name, campaignId)).Target.EntryId;

        LexiconReviewPageDto page = await PostOkAsync(
            world,
            "/api/memory/lexicon/review/list",
            new LexiconReviewListRequest(ScopeOf(campaignId), 50, null),
            ArcanumJsonContext.Default.LexiconReviewListRequest,
            ArcanumJsonContext.Default.ApiResponseLexiconReviewPageDto);

        LexiconReviewItemDto item = Assert.Single(page.Items, candidate => candidate.IsCurrent && candidate.EntryId == entryId);

        LexiconReviewBulkPrepareRequest prepare = new(
            Guid.CreateVersion7(),
            ScopeOf(campaignId),
            action,
            [new LexiconReviewDecision(item.ObservationToken, null)]);

        MemoryReviewBulkPlanDto plan = await PostOkAsync(
            world,
            "/api/memory/lexicon/review/prepare",
            prepare,
            ArcanumJsonContext.Default.LexiconReviewBulkPrepareRequest,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto);

        MemoryReviewBulkResultDto result = await PostOkAsync(
            world,
            "/api/memory/lexicon/review/apply",
            new LexiconReviewBulkApplyRequest(prepare, plan.PreparedPlanToken),
            ArcanumJsonContext.Default.LexiconReviewBulkApplyRequest,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);

        AssertReviewApplied(result, MemoryReviewStore.Lexicon, action);
    }

    /// <summary>A bulk review of the Global Confirmed Covenant queue: one key retired, the other pinned.</summary>
    private static async Task ReviewCovenantAsync(IsolationWorld world)
    {
        await ReviewCovenantOnceAsync(world, MemoryReviewAction.Retire, KeyB);

        await ReviewCovenantOnceAsync(world, MemoryReviewAction.Pin, KeyA);
    }

    private static async Task ReviewCovenantOnceAsync(IsolationWorld world, MemoryReviewAction action, string key)
    {
        CovenantReviewPageDto page = await PostOkAsync(
            world,
            "/api/memory/covenant/review/list",
            new CovenantReviewListRequest(CovenantScope.Global, null, CovenantLane.Confirmed, 50, null),
            ArcanumJsonContext.Default.CovenantReviewListRequest,
            ArcanumJsonContext.Default.ApiResponseCovenantReviewPageDto);

        CovenantReviewItemDto item = Assert.Single(page.Items, candidate => candidate.IsCurrent && candidate.Key == key);

        CovenantReviewBulkPrepareRequest prepare = new(
            Guid.CreateVersion7(),
            CovenantScope.Global,
            null,
            CovenantLane.Confirmed,
            action,
            [new CovenantReviewDecision(item.ObservationToken, null)]);

        MemoryReviewBulkPlanDto plan = await PostOkAsync(
            world,
            "/api/memory/covenant/review/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantReviewBulkPrepareRequest,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto);

        MemoryReviewBulkResultDto result = await PostOkAsync(
            world,
            "/api/memory/covenant/review/apply",
            new CovenantReviewBulkApplyRequest(prepare, plan.PreparedPlanToken),
            ArcanumJsonContext.Default.CovenantReviewBulkApplyRequest,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);

        AssertReviewApplied(result, MemoryReviewStore.Covenant, action);
    }

    private static void AssertReviewApplied(MemoryReviewBulkResultDto result, MemoryReviewStore store, MemoryReviewAction action)
    {
        Assert.Equal(store, result.Store);

        Assert.Equal(action, result.Action);

        Assert.False(result.Replayed);

        // Each store names its own outcomes, and the Lexicon spells them in lower case.
        Assert.Equal(
            action == MemoryReviewAction.Retire ? "Retired" : "Pinned",
            Assert.Single(result.Items).Outcome,
            ignoreCase: true);
    }

    /// <summary>Erases the store's first seeded item through its show, prepare and apply routes.</summary>
    private static Task EraseAsync(IsolationWorld world, MemoryReviewStore store) =>
        store switch
        {
            MemoryReviewStore.Saga => world.Driver.EraseSagaAsync(world.SagaOneId, ct: world.Token),
            MemoryReviewStore.Lexicon => world.Driver.EraseLexiconAsync(GlobalEntry, null, ct: world.Token),
            _ => world.Driver.EraseCovenantAsync(CovenantScope.Global, null, KeyB, ct: world.Token),
        };

    /// <summary>Erases the store's other seeded item, which the fingerprint theory never names again.</summary>
    private static Task EraseBystanderAsync(IsolationWorld world, MemoryReviewStore store) =>
        store switch
        {
            MemoryReviewStore.Saga => world.Driver.EraseSagaAsync(world.SagaTwoId, ct: world.Token),
            MemoryReviewStore.Lexicon => world.Driver.EraseLexiconAsync(CampaignEntry, world.Campaign, ct: world.Token),
            _ => world.Driver.EraseCovenantAsync(CovenantScope.Global, null, KeyA, ct: world.Token),
        };

    /// <summary>Releases the fingerprint <see cref="EraseAsync"/> recorded, requiring that it released one.</summary>
    private static async Task<MemoryErasureReleaseResultDto> ReleaseAsync(IsolationWorld world, MemoryReviewStore store)
    {
        MemoryErasureReleaseResultDto released = store switch
        {
            MemoryReviewStore.Saga => await world.Driver.ReleaseSagaAsync(new SagaErasureReleaseRequest(SagaMemoryScopeKind.Global, null, SagaOneContent), world.Token),
            MemoryReviewStore.Lexicon => await world.Driver.ReleaseLexiconAsync(new LexiconErasureReleaseRequest(ScopeOf(null), GlobalEntry), world.Token),
            _ => await world.Driver.ReleaseCovenantAsync(new CovenantErasureReleaseRequest(CovenantScope.Global, null, KeyB), world.Token),
        };

        Assert.Equal(MemoryErasureReleaseOutcome.Released, released.Outcome);

        return released;
    }

    private static async Task DeleteAsync(IsolationWorld world, string path)
    {
        using HttpResponseMessage deleted = await world.Client.DeleteAsync(path, world.Token);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    private static async Task<TResult> PostOkAsync<TRequest, TResult>(
        IsolationWorld world,
        string path,
        TRequest request,
        JsonTypeInfo<TRequest> requestInfo,
        JsonTypeInfo<ApiResponse<TResult>> resultInfo)
    {
        using HttpResponseMessage response = await world.Driver.PostAsync(path, request, requestInfo, world.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(response, resultInfo);
    }

    /// <summary>Registers Campaign C through its route, so every store and the Covenant gate know it.</summary>
    private static async Task<Guid> RegisterCampaignAsync(ArcanumWebApplicationFactory host, HttpClient client, CancellationToken ct)
    {
        string path = Path.Combine(host.TempHome, "isolation-campaign");

        Directory.CreateDirectory(path);

        using HttpResponseMessage registered = await client.PostAsync(
            "/api/campaigns",
            JsonContent.Create(
                new RegisterCampaignRequest("Isolation", path, WorkspaceType.Campaign, null),
                ArcanumJsonContext.Default.RegisterCampaignRequest),
            ct);

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        return (await MemoryErasureRouteDriver.ReadDataAsync(registered, ArcanumJsonContext.Default.ApiResponseCampaignDto)).Id;
    }

    /// <summary>Scribes one entry through the host's own Lexicon service, the one every writer holds.</summary>
    private static async Task ScribeAsync(ArcanumWebApplicationFactory host, string name, Guid? campaignId, string[] facts, CancellationToken ct)
    {
        using IServiceScope scope = host.Services.CreateScope();

        Result<LexiconEntryDto> scribed = await scope.ServiceProvider
            .GetRequiredService<ILexiconService>()
            .UpsertAsync(name, "Place", facts, LexiconScope.ForResolvedCampaign(campaignId), ct);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);
    }

    private static LexiconCurationScope ScopeOf(Guid? campaignId) =>
        new(campaignId is null ? LexiconScopeKind.Global : LexiconScopeKind.Campaign, campaignId);

    /// <summary>One verb: the precondition it needs, what it does, and the one store it may change.</summary>
    private sealed record MemoryMutationCase(
        string Name,
        MemoryStoreFamily Target,
        Func<IsolationWorld, Task> Arrange,
        Func<IsolationWorld, Task> Act);

    /// <summary>
    /// The host a row runs in, the identities its baseline wrote, and the deadline every request and
    /// wait in the row is cancelled at.
    /// </summary>
    private sealed record IsolationWorld(
        ArcanumWebApplicationFactory Host,
        MemoryErasureRouteDriver Driver,
        HttpClient Client,
        Guid Campaign,
        string SagaOneId,
        string SagaTwoId,
        CancellationToken Token);
}
