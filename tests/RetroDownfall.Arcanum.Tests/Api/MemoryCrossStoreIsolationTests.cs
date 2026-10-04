using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Each per-item memory verb below, driven through its production entry point, leaves the other two
/// stores and the shared ledger byte-identical.
/// </summary>
/// <remarks>
/// <para>The verbs covered are exactly these:</para>
/// <list type="bullet">
/// <item><description>the operator verbs spec section 16 lists, through their mapped routes: Saga, Lexicon and
/// Covenant curation; bulk review apply in each store (a Retire and a Pin); erase and release in
/// each store; and the legacy <c>DELETE /api/saga/{id}</c> and <c>DELETE /api/memory/lexicon/{name}</c>;</description></item>
/// <item><description>the other bulk review actions in each store: Confirm, Unpin, and Correct, where the Saga
/// correction releases an erasure fingerprint implicitly while the other two stores hold
/// fingerprints of their own;</description></item>
/// <item><description>the agent tools <c>scribe_lexicon</c> and <c>delete_lexicon</c>, through the function the
/// host's MCP bridge hands a turn, which outside a turn writes the Global tier.</description></item>
/// </list>
///
/// <para>Not covered: <c>propose_covenant</c> and <c>retire_covenant</c> act only under the Covenant staging
/// capability a model turn grants (outside one they refuse as <c>Covenant.IneligibleTurn</c>), and what
/// they stage is published only when that turn commits, so no tool call outside a turn reaches their
/// write. Saga extraction is out of scope too: it is off in these hosts by design, so that no
/// background pass can write a Saga row while a row runs.</para>
///
/// <para>Each row starts a host holding all three stores, written by their production writers: two
/// Global Saga memories through the store's insert, a Global and a Campaign Lexicon entry through the
/// host's Lexicon service, and two Global Covenant keys through the set routes. The memories are then
/// labelled through the sensitivity ledger: the Saga memories in every row, and the Lexicon entries as
/// well in the theory that runs without the Annals. The row then writes its own precondition the same
/// way (a retire before a reinstate, a pin before an unpin, a mask before an unmask, an erase before a
/// release), and only then is the first snapshot taken.</para>
///
/// <para>The Covenant search projection is drained to a settled state before each snapshot, so a
/// Covenant write's projection lands inside its own row. After a verb outside the Covenant, any search
/// work it left pending fails the row by name before anything drains it.</para>
///
/// <para>These rows characterize production code that is expected to be isolated. A row that fails is
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

    private const string Labels = "artifact_sensitivity";

    private static readonly LexiconReplacementContent CorrectedFacts = new("Place", ["keeps the harbour light", "rings the fog bell"]);

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
        new("saga-review-apply", MemoryStoreFamily.Saga, NoPrecondition, static world => ReviewSagaAsync(world)) { NeedsAnnals = true },
        new("lexicon-review-apply", MemoryStoreFamily.Lexicon, NoPrecondition, static world => ReviewLexiconAsync(world)) { NeedsAnnals = true },
        new("covenant-review-apply", MemoryStoreFamily.Covenant, NoPrecondition, static world => ReviewCovenantAsync(world)) { NeedsAnnals = true },
        new("saga-review-confirm", MemoryStoreFamily.Saga, NoPrecondition, static world => ReviewSagaOnceAsync(world, MemoryReviewAction.Confirm, world.SagaOneId)) { NeedsAnnals = true },
        new("lexicon-review-confirm", MemoryStoreFamily.Lexicon, NoPrecondition, static world => ReviewLexiconOnceAsync(world, MemoryReviewAction.Confirm, GlobalEntry, null)) { NeedsAnnals = true },
        new("covenant-review-confirm", MemoryStoreFamily.Covenant, NoPrecondition, static world => ReviewCovenantOnceAsync(world, MemoryReviewAction.Confirm, KeyA)) { NeedsAnnals = true },
        new("saga-review-correct", MemoryStoreFamily.Saga, static world => EraseForCorrectionAsync(world, MemoryReviewStore.Saga), static world => CorrectSagaByReviewAsync(world)) { NeedsAnnals = true },
        new("lexicon-review-correct", MemoryStoreFamily.Lexicon, static world => EraseForCorrectionAsync(world, MemoryReviewStore.Lexicon), static world => ReviewLexiconOnceAsync(world, MemoryReviewAction.Correct, GlobalEntry, null, CorrectedFacts)) { NeedsAnnals = true },
        new("covenant-review-correct", MemoryStoreFamily.Covenant, static world => EraseForCorrectionAsync(world, MemoryReviewStore.Covenant), static world => ReviewCovenantOnceAsync(world, MemoryReviewAction.Correct, KeyA, "Answer in Scottish English.")) { NeedsAnnals = true },
        new("saga-review-unpin", MemoryStoreFamily.Saga, static world => SagaPinVerbAsync(world, "pin"), static world => ReviewSagaOnceAsync(world, MemoryReviewAction.Unpin, world.SagaOneId)) { NeedsAnnals = true },
        new("lexicon-review-unpin", MemoryStoreFamily.Lexicon, static world => LexiconVerbAsync(world, "pin"), static world => ReviewLexiconOnceAsync(world, MemoryReviewAction.Unpin, GlobalEntry, null)) { NeedsAnnals = true },
        new("covenant-review-unpin", MemoryStoreFamily.Covenant, static world => CurateAsync(world, CovenantCurationKind.Pin, CovenantScope.Global, null), static world => ReviewCovenantOnceAsync(world, MemoryReviewAction.Unpin, KeyA)) { NeedsAnnals = true },
        new("saga-erase", MemoryStoreFamily.Saga, NoPrecondition, static world => EraseAsync(world, MemoryReviewStore.Saga)),
        new("lexicon-erase", MemoryStoreFamily.Lexicon, NoPrecondition, static world => EraseAsync(world, MemoryReviewStore.Lexicon)),
        new("covenant-erase", MemoryStoreFamily.Covenant, NoPrecondition, static world => EraseAsync(world, MemoryReviewStore.Covenant)),
        new("saga-release", MemoryStoreFamily.Saga, static world => EraseAsync(world, MemoryReviewStore.Saga), static world => ReleaseAsync(world, MemoryReviewStore.Saga)),
        new("lexicon-release", MemoryStoreFamily.Lexicon, static world => EraseAsync(world, MemoryReviewStore.Lexicon), static world => ReleaseAsync(world, MemoryReviewStore.Lexicon)),
        new("covenant-release", MemoryStoreFamily.Covenant, static world => EraseAsync(world, MemoryReviewStore.Covenant), static world => ReleaseAsync(world, MemoryReviewStore.Covenant)),
        new("legacy-saga-delete", MemoryStoreFamily.Saga, NoPrecondition, static world => DeleteAsync(world, $"/api/saga/{world.SagaOneId}")),
        new("legacy-lexicon-delete", MemoryStoreFamily.Lexicon, NoPrecondition, static world => DeleteAsync(world, $"/api/memory/lexicon/{Uri.EscapeDataString(GlobalEntry)}")),
        new("agent-scribe-lexicon", MemoryStoreFamily.Lexicon, NoPrecondition, static world => ScribeByToolAsync(world)) { AgentTool = true },

        // An agent tool call never carries sensitivity-purge authority, so delete_lexicon refuses a
        // labelled target by design; this row deletes an unlabelled entry beside labelled bystanders.
        new("agent-delete-lexicon", MemoryStoreFamily.Lexicon, NoPrecondition, static world => DeleteByToolAsync(world)) { AgentTool = true, Unlabelled = SeededItem.GlobalEntry },
    ];

    /// <summary>How long one row may run before every request and wait it makes is cancelled.</summary>
    private static readonly TimeSpan RowDeadline = TimeSpan.FromMinutes(3);

    public static TheoryData<string> Cases => Names(Verbs);

    /// <summary>Every row that runs without the Annals, which a host holding labelled Lexicon entries does.</summary>
    public static TheoryData<string> LabelledCases => Names(Verbs.Where(static verb => !verb.NeedsAnnals));

    /// <summary>
    /// Runs one verb in a host whose Saga memories carry sensitivity labels, beside unlabelled Lexicon
    /// entries and the Covenant keys.
    /// </summary>
    /// <remarks>
    /// The Annals stay on, so the review verbs run here. A Lexicon entry that already holds an Annals claim
    /// cannot be labelled afterwards in any state production reaches, so the Lexicon's labels are the
    /// other theory's.
    /// </remarks>
    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public async Task A_per_item_verb_changes_only_its_own_store(string row)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await RunAsync(Assert.Single(Verbs, candidate => candidate.Name == row), labelLexicon: false);
    }

    /// <summary>
    /// Runs one verb in a host without the Annals, where every seeded Saga memory and Lexicon entry
    /// carries a sensitivity label, so each verb meets a labelled target and labelled bystanders in the
    /// other store.
    /// </summary>
    /// <remarks>
    /// This is the theory that reaches the labelled branches: the sensitive-artifact purge behind both
    /// legacy deletes, and the label removal inside a Saga or Lexicon erase. The review verbs need the
    /// Annals, so they run only in the other theory. A row whose verb treats a labelled target differently
    /// leaves that one target unlabelled and says why.
    /// </remarks>
    [SkippableTheory]
    [MemberData(nameof(LabelledCases))]
    public async Task A_per_item_verb_beside_labelled_memories_changes_only_its_own_store(string row)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        MemoryMutationCase verb = Assert.Single(Verbs, candidate => candidate.Name == row);

        Assert.False(verb.NeedsAnnals);

        await RunAsync(verb, labelLexicon: true);
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

        await MemoryStoreSnapshot.QuiesceAsync(host.Services, target, world.Token);

        MemoryStoreSnapshot erased = await CaptureAsync(world);

        MemoryStoreSnapshot.AssertOnlyChanged(before, erased, target);

        Assert.Equal(before.CountRows(target, Fingerprints) + 1, erased.CountRows(target, Fingerprints));

        MemoryErasureReleaseResultDto released = await ReleaseAsync(world, store);

        Assert.Equal(store, released.Store);

        Assert.Equal(MemoryErasureReleaseOutcome.Released, released.Outcome);

        Assert.Equal(1, released.ReleasedCount);

        await MemoryStoreSnapshot.QuiesceAsync(host.Services, target, world.Token);

        MemoryStoreSnapshot after = await CaptureAsync(world);

        MemoryStoreSnapshot.AssertOnlyChanged(erased, after, target);

        Assert.Equal(erased.CountRows(target, Fingerprints) - 1, after.CountRows(target, Fingerprints));

        foreach (MemoryStoreFamily family in Families.Where(family => family != target))
        {
            Assert.Equal(FingerprintRows(before, family), FingerprintRows(erased, family));

            Assert.Equal(FingerprintRows(before, family), FingerprintRows(after, family));
        }
    }

    /// <summary>
    /// Seeds a host, labels its memories, writes the row's precondition, and requires the verb to change
    /// only its own store.
    /// </summary>
    private static async Task RunAsync(MemoryMutationCase verb, bool labelLexicon)
    {
        using CancellationTokenSource deadline = new(RowDeadline);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            covenant: true,
            configure: settings =>
            {
                // The agent tools are advertised only while the Lexicon feature is on.
                settings.Features.Lexicon |= verb.AgentTool;

                // A Lexicon entry labelled after it was scribed is a production state only without the
                // Annals, the way LexiconErasureEndpointTests labels one.
                settings.Features.Annals &= !labelLexicon;
            });

        IsolationWorld world = await SeedAsync(host, deadline.Token);

        await LabelAsync(world, labelLexicon, verb.Unlabelled);

        await verb.Arrange(world);

        await MemoryStoreSnapshot.QuiesceAsync(host.Services, world.Token);

        MemoryStoreSnapshot before = await CaptureAsync(world);

        // Labelled rows exist in every family this theory labels, so the label partition is compared on
        // rows that are there, not on an empty table.
        Assert.True(before.CountRows(MemoryStoreFamily.Saga, Labels) > 0, "No labelled Saga memory remains before the verb.");

        Assert.True(
            !labelLexicon || before.CountRows(MemoryStoreFamily.Lexicon, Labels) > 0,
            "No labelled Lexicon entry remains before the verb.");

        await verb.Act(world);

        await MemoryStoreSnapshot.QuiesceAsync(host.Services, verb.Target, world.Token);

        MemoryStoreSnapshot.AssertOnlyChanged(before, await CaptureAsync(world), verb.Target);
    }

    private static TheoryData<string> Names(IEnumerable<MemoryMutationCase> verbs)
    {
        TheoryData<string> names = [];

        foreach (MemoryMutationCase verb in verbs)
        {
            names.Add(verb.Name);
        }

        return names;
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

    private static async Task<MemoryReviewBulkResultDto> ReviewSagaOnceAsync(
        IsolationWorld world,
        MemoryReviewAction action,
        string memoryId,
        string? replacement = null)
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
            [new SagaReviewDecision(item.ObservationToken, replacement)]);

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

        return result;
    }

    /// <summary>A bulk review of both Lexicon queues: the Global entry retired, the Campaign entry pinned.</summary>
    private static async Task ReviewLexiconAsync(IsolationWorld world)
    {
        await ReviewLexiconOnceAsync(world, MemoryReviewAction.Retire, GlobalEntry, null);

        await ReviewLexiconOnceAsync(world, MemoryReviewAction.Pin, CampaignEntry, world.Campaign);
    }

    private static async Task ReviewLexiconOnceAsync(
        IsolationWorld world,
        MemoryReviewAction action,
        string name,
        Guid? campaignId,
        LexiconReplacementContent? replacement = null)
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
            [new LexiconReviewDecision(item.ObservationToken, replacement)]);

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

    private static async Task ReviewCovenantOnceAsync(
        IsolationWorld world,
        MemoryReviewAction action,
        string key,
        string? replacement = null)
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
            [new CovenantReviewDecision(item.ObservationToken, replacement)]);

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

        // Each store names its own outcomes: the Lexicon spells them in lower case and acknowledges what
        // the other two confirm.
        string expected = (action, store) switch
        {
            (MemoryReviewAction.Confirm, MemoryReviewStore.Lexicon) => "acknowledged",
            (MemoryReviewAction.Confirm, _) => "Confirmed",
            (MemoryReviewAction.Correct, _) => "Corrected",
            (MemoryReviewAction.Retire, _) => "Retired",
            (MemoryReviewAction.Pin, _) => "Pinned",
            _ => "Unpinned",
        };

        Assert.Equal(expected, Assert.Single(result.Items).Outcome, ignoreCase: true);
    }

    /// <summary>
    /// Erases the seeded item of every store but the one a review correction targets, so that
    /// correction runs beside fingerprints the other stores hold. A Saga correction also needs the
    /// fingerprint of the content it corrects to, which the erase of the second memory records.
    /// </summary>
    private static async Task EraseForCorrectionAsync(IsolationWorld world, MemoryReviewStore corrected)
    {
        _ = await world.Driver.EraseSagaAsync(world.SagaTwoId, ct: world.Token);

        if (corrected != MemoryReviewStore.Lexicon)
        {
            _ = await world.Driver.EraseLexiconAsync(CampaignEntry, world.Campaign, ct: world.Token);
        }

        if (corrected != MemoryReviewStore.Covenant)
        {
            _ = await world.Driver.EraseCovenantAsync(CovenantScope.Global, null, KeyB, ct: world.Token);
        }
    }

    /// <summary>
    /// Corrects the first memory, through the Saga review queue, to the content the second memory's
    /// erase fingerprinted, so the correction releases that fingerprint implicitly and reports it.
    /// </summary>
    private static async Task CorrectSagaByReviewAsync(IsolationWorld world)
    {
        MemoryReviewBulkResultDto result = await ReviewSagaOnceAsync(world, MemoryReviewAction.Correct, world.SagaOneId, SagaTwoContent);

        Assert.True(Assert.Single(result.Items).ReleasedErasureFingerprint);
    }

    /// <summary>
    /// Scribes a new Global entry through the agent's <c>scribe_lexicon</c> tool. Outside a turn a tool
    /// call has no Session, so it writes the Global tier.
    /// </summary>
    private static async Task ScribeByToolAsync(IsolationWorld world)
    {
        string text = await InvokeToolAsync(
            world,
            "scribe_lexicon",
            new Dictionary<string, object?>
            {
                ["name"] = "Iso Herald",
                ["type"] = "Person",
                ["facts"] = new[] { "rings the harbour bell" },
            });

        Assert.Contains("Iso Herald", text, StringComparison.Ordinal);
    }

    /// <summary>Deletes the Global entry through the agent's <c>delete_lexicon</c> tool.</summary>
    private static async Task DeleteByToolAsync(IsolationWorld world)
    {
        string text = await InvokeToolAsync(world, "delete_lexicon", new Dictionary<string, object?> { ["name"] = GlobalEntry });

        Assert.Contains("was removed", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Invokes one internal tool through the function the host's MCP bridge hands a turn, the production
    /// entry point for every agent tool call.
    /// </summary>
    private static async Task<string> InvokeToolAsync(IsolationWorld world, string tool, Dictionary<string, object?> arguments)
    {
        IReadOnlyList<AITool> tools = await world.Host.Services
            .GetRequiredService<IMcpConnectionManager>()
            .GetAvailableToolsAsync(null, world.Token);

        AIFunction function = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(tools, candidate => string.Equals(candidate.Name, tool, StringComparison.Ordinal)));

        object? result = await function.InvokeAsync(new AIFunctionArguments(arguments), world.Token);

        return Convert.ToString(result, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// Labels the seeded Saga memories, and the Lexicon entries when asked, through the one production
    /// writer of sensitivity labels, leaving out whatever the row keeps unlabelled.
    /// </summary>
    private static async Task LabelAsync(IsolationWorld world, bool lexicon, SeededItem unlabelled)
    {
        if (!unlabelled.HasFlag(SeededItem.SagaOne))
        {
            await LabelSagaAsync(world, world.SagaOneId, SagaOneContent);
        }

        if (!unlabelled.HasFlag(SeededItem.SagaTwo))
        {
            await LabelSagaAsync(world, world.SagaTwoId, SagaTwoContent);
        }

        if (lexicon && !unlabelled.HasFlag(SeededItem.GlobalEntry))
        {
            await LabelLexiconAsync(world, GlobalEntry, null);
        }

        if (lexicon && !unlabelled.HasFlag(SeededItem.CampaignEntry))
        {
            await LabelLexiconAsync(world, CampaignEntry, world.Campaign);
        }
    }

    private static Task LabelSagaAsync(IsolationWorld world, string id, string content) =>
        LabelArtifactAsync(
            world,
            new DerivedArtifactWrite(
                SensitiveArtifactKind.Saga,
                Guid.Parse(id),
                sessionId: null,
                campaignId: null,
                turnId: null,
                artifactRevision: 1,
                DerivedArtifactContentDigest.ForText(content),
                ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Guid.NewGuid()])));

    private static async Task LabelLexiconAsync(IsolationWorld world, string name, Guid? campaignId)
    {
        LexiconEntryDto entry = (await ShowLexiconAsync(world, name, campaignId)).Entry;

        LexiconCanonicalValue canonical = LexiconValueNormalizer.NormalizeCorrection(entry.Name, entry.Type, entry.Facts).Value;

        await LabelArtifactAsync(
            world,
            new DerivedArtifactWrite(
                SensitiveArtifactKind.Lexicon,
                entry.Id,
                sessionId: null,
                entry.ScopeCampaignId,
                turnId: null,
                artifactRevision: 1,
                DerivedArtifactContentDigest.ForBytes(LexiconSnapshotDigest.Encode(canonical)),
                ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Guid.NewGuid()])));
    }

    private static async Task LabelArtifactAsync(IsolationWorld world, DerivedArtifactWrite write)
    {
        using IServiceScope scope = world.Host.Services.CreateScope();

        Result<LabeledArtifactWriteReceipt> labelled = await scope.ServiceProvider
            .GetRequiredService<IArtifactSensitivityLedger>()
            .LabelAsync(write, world.Token);

        Assert.True(labelled.IsSuccess, labelled.IsFailure ? labelled.Error.Message : null);
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
        Func<IsolationWorld, Task> Act)
    {
        /// <summary>Seeded items the row leaves unlabelled, because the verb treats a labelled one differently.</summary>
        public SeededItem Unlabelled { get; init; }

        /// <summary>Whether the verb needs the Annals, so it cannot run beside labelled Lexicon entries.</summary>
        public bool NeedsAnnals { get; init; }

        /// <summary>Whether the verb is an agent tool, which the host advertises only with the Lexicon feature on.</summary>
        public bool AgentTool { get; init; }
    }

    /// <summary>The seeded Saga memories and Lexicon entries a row can leave unlabelled.</summary>
    [Flags]
    private enum SeededItem
    {
        None = 0,
        SagaOne = 1,
        SagaTwo = 2,
        GlobalEntry = 4,
        CampaignEntry = 8,
    }

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
