using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Chronicle;
using RetroDownfall.TheForge.Ux.ViewModels.WarTable;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class WarTableViewModelTests
{

    private static readonly Guid ChildId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid ParentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task RefreshAsync_LoadsApprenticeSummaries()
    {

        FakeWarTableDataSource dataSource = new()
        {
            Summaries =
            [
                NewSummary(ChildId, "scout", "Running"),
            ],
        };

        WarTableViewModel viewModel = new(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Single(viewModel.Apprentices);

        Assert.Equal("scout", viewModel.Apprentices[0].Name);

        Assert.False(viewModel.HasNoApprentices);

    }

    [Fact]
    public async Task SelectApprenticeAsync_LoadsDetailPlanLineageAndStartsChronicle()
    {

        FakeWarTableDataSource dataSource = new()
        {
            Summaries = [NewSummary(ChildId, "scout", "Running")],
            Details =
            {
                [ChildId] = NewDetail(
                    ChildId,
                    ParentId,
                    "scout",
                    [
                        new PlanStep { Index = 0, Description = "recon", Status = "completed" },
                        new PlanStep { Index = 1, Description = "strike", Status = "running" },
                        new PlanStep { Index = 2, Description = "report", Status = "pending" },
                        new PlanStep { Index = 3, Description = "fail", Status = "failed" },
                        new PlanStep { Index = 4, Description = "escalate", Status = "escalated" },
                    ]),
                [ParentId] = NewDetail(ParentId, null, "marshal", []),
            },
            ChronicleFrames =
            [
                new ChronicleFrame("toolCall", ChildId, DateTimeOffset.UtcNow, Message: "calling hammer", ToolName: "hammer"),
                new ChronicleFrame("eventsDropped", ChildId, DateTimeOffset.UtcNow, Summary: "2 dropped"),
            ],
        };

        WarTableViewModel viewModel = new(dataSource)
        {
            IsVisible = true,
        };

        await viewModel.RefreshAsync(CancellationToken.None);

        await viewModel.SelectApprenticeAsync(viewModel.Apprentices[0], CancellationToken.None);

        for (int i = 0; i < 20 && (viewModel.SelectedApprentice?.Chronicle.Entries.Count ?? 0) < 2; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(viewModel.SelectedApprentice);

        Assert.Equal(5, viewModel.SelectedApprentice.PlanSteps.Count);

        Assert.Equal("completed", viewModel.SelectedApprentice.PlanSteps[0].StatusKind, ignoreCase: false, ignoreLineEndingDifferences: false, ignoreWhiteSpaceDifferences: false);

        Assert.Equal("running", viewModel.SelectedApprentice.PlanSteps[1].StatusKind, ignoreCase: true);

        Assert.Equal("pending", viewModel.SelectedApprentice.PlanSteps[2].StatusKind, ignoreCase: true);

        Assert.Equal("failed", viewModel.SelectedApprentice.PlanSteps[3].StatusKind, ignoreCase: true);

        Assert.Equal("escalated", viewModel.SelectedApprentice.PlanSteps[4].StatusKind, ignoreCase: true);

        Assert.Equal(5, viewModel.SelectedApprentice.PlanSteps.Select(s => s.StatusKind).Distinct().Count());

        Assert.NotNull(viewModel.SelectedApprentice.LineageRoot);

        Assert.Equal(ParentId, viewModel.SelectedApprentice.LineageRoot.Id);

        Assert.Single(viewModel.SelectedApprentice.LineageRoot.Children);

        Assert.Equal(ChildId, viewModel.SelectedApprentice.LineageRoot.Children[0].Id);

        await Task.Delay(50);

        Assert.True(viewModel.SelectedApprentice.Chronicle.Entries.Count >= 2);

        Assert.Contains(viewModel.SelectedApprentice.Chronicle.Entries, static e => e.IsPassThrough);

        Assert.Contains(viewModel.SelectedApprentice.Chronicle.Entries, static e => e.IsWarning);

        Assert.Equal(1, dataSource.ChronicleSubscriptions);

        viewModel.Dispose();

    }

    [Fact]
    public async Task SelectApprenticeAsync_SubscribesToTheChronicleExactlyOnce()
    {

        FakeWarTableDataSource dataSource = new()
        {
            Summaries = [NewSummary(ChildId, "scout", "Running")],
            Details =
            {
                [ChildId] = NewDetail(ChildId, null, "scout", []),
            },
            ChronicleFrames =
            [
                new ChronicleFrame("planGenerated", ChildId, DateTimeOffset.UtcNow, Message: "plan ready"),
            ],
        };

        WarTableViewModel viewModel = new(dataSource)
        {
            IsVisible = true,
        };

        await viewModel.RefreshAsync(CancellationToken.None);

        await viewModel.SelectApprenticeAsync(viewModel.Apprentices[0], CancellationToken.None);

        await Task.Delay(50);

        Assert.Equal(1, dataSource.ChronicleSubscriptions);

        Assert.NotNull(viewModel.SelectedApprentice);

        Assert.Single(viewModel.SelectedApprentice.Chronicle.Entries);

        viewModel.Dispose();

    }

    [Fact]
    public async Task Chronicle_Start_ClearsReplayedEntriesSoReactivationDoesNotDuplicateThem()
    {

        FakeWarTableDataSource dataSource = new()
        {
            ChronicleFrames =
            [
                new ChronicleFrame("planGenerated", ChildId, DateTimeOffset.UtcNow, Message: "plan ready"),
                new ChronicleFrame("stepStarted", ChildId, DateTimeOffset.UtcNow, Message: "step 0"),
            ],
        };

        ChronicleViewModel chronicle = new(ChildId, dataSource);

        chronicle.Start();

        await WaitForEntriesAsync(chronicle, 2);

        chronicle.Stop();

        chronicle.Start();

        await WaitForEntriesAsync(chronicle, 2);

        Assert.Equal(2, chronicle.Entries.Count);

        chronicle.Dispose();

    }

    [Fact]
    public async Task Chronicle_EnforcesEntryCapOnALongStream()
    {

        List<ChronicleFrame> frames = [];

        for (int i = 0; i < ChronicleViewModel.MaxEntries + 25; i++)
        {

            frames.Add(new ChronicleFrame("toolCall", ChildId, DateTimeOffset.UtcNow, Message: $"frame-{i}"));

        }

        FakeWarTableDataSource dataSource = new()
        {
            ChronicleFrames = frames,
        };

        ChronicleViewModel chronicle = new(ChildId, dataSource);

        chronicle.Start();

        await WaitForEntriesAsync(chronicle, ChronicleViewModel.MaxEntries);

        Assert.Equal(ChronicleViewModel.MaxEntries, chronicle.Entries.Count);

        Assert.Equal(
            $"frame-{ChronicleViewModel.MaxEntries + 24}",
            chronicle.Entries[0].Message);

        chronicle.Dispose();

    }

    private static async Task WaitForEntriesAsync(ChronicleViewModel chronicle, int expected)
    {

        for (int i = 0; i < 400 && chronicle.Entries.Count < expected; i++)
        {

            await Task.Delay(10);

        }

    }

    [Fact]
    public async Task SelectApprenticeByIdAsync_UsesDirectDetailOutsidePagedSummaryList()
    {

        Guid olderApprenticeId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        FakeWarTableDataSource dataSource = new()
        {

            Summaries = Enumerable.Range(0, 100)
                .Select(index => NewSummary(
                    Guid.NewGuid(),
                    $"recent-{index}",
                    "Running"))
                .ToArray(),

            Details =
            {

                [olderApprenticeId] = NewDetail(
                    olderApprenticeId,
                    null,
                    "older-apprentice",
                    []),

            },

        };

        WarTableViewModel viewModel = new(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal(100, viewModel.Apprentices.Count);

        Assert.DoesNotContain(
            viewModel.Apprentices,
            apprentice => apprentice.Id == olderApprenticeId);

        bool selected = await viewModel.SelectApprenticeByIdAsync(
            olderApprenticeId,
            CancellationToken.None);

        Assert.True(selected);

        Assert.Equal(olderApprenticeId, viewModel.SelectedApprentice?.Id);

        Assert.Equal([olderApprenticeId], dataSource.RequestedDetailIds);

        viewModel.Dispose();

    }

    [Fact]

    public async Task SelectApprenticeByIdAsync_PropagatesCancellationDuringDetailLoading()
    {

        Guid apprenticeId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        FakeWarTableDataSource dataSource = new()
        {

            ThrowCancellationOnLineage = true,

            Details =
            {

                [apprenticeId] = NewDetail(
                    apprenticeId,
                    null,
                    "cancelled-apprentice",
                    []),

            },

        };

        WarTableViewModel viewModel = new(dataSource);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            viewModel.SelectApprenticeByIdAsync(
                apprenticeId,
                new CancellationToken(canceled: true)));

        Assert.Null(viewModel.SelectedApprentice);

        viewModel.Dispose();

    }

    [Fact]
    public async Task StartPauseResumeCancelIntervene_RoundTripThroughDataSource()
    {

        FakeWarTableDataSource dataSource = new()
        {
            Summaries = [NewSummary(ChildId, "scout", "Paused")],
            Details = { [ChildId] = NewDetail(ChildId, null, "scout", []) },
        };

        WarTableViewModel viewModel = new(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        await viewModel.SelectApprenticeAsync(viewModel.Apprentices[0], CancellationToken.None);

        ApprenticeDetailViewModel detail = viewModel.SelectedApprentice!;

        await detail.StartAsync(CancellationToken.None);

        await detail.PauseAsync(CancellationToken.None);

        await detail.ResumeAsync(CancellationToken.None);

        detail.InterveneGuidance = "hold the line";

        await detail.InterveneAsync(CancellationToken.None);

        await detail.CancelAsync(CancellationToken.None);

        Assert.Contains("start", dataSource.Actions);

        Assert.Contains("pause", dataSource.Actions);

        Assert.Contains("resume", dataSource.Actions);

        Assert.Contains("intervene", dataSource.Actions);

        Assert.Contains("cancel", dataSource.Actions);

        Assert.Equal("hold the line", dataSource.LastIntervention?.Guidance);

        viewModel.Dispose();

    }

    [Fact]
    public async Task CreateApprenticeAsync_PostsRequestAndSelectsCreated()
    {

        Guid createdId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        FakeWarTableDataSource dataSource = new()
        {
            Created = NewDetail(createdId, null, "forged", []),
        };

        WarTableViewModel viewModel = new(dataSource)
        {
            NewName = "forged",
            NewGoal = "secure the gate @file notes.md",
            IsCreatePanelOpen = true,
        };

        await viewModel.CreateApprenticeAsync(CancellationToken.None);

        Assert.NotNull(dataSource.LastCreate);

        Assert.Equal("forged", dataSource.LastCreate.Name);

        Assert.Contains("@file", dataSource.LastCreate.Goal);

        Assert.Equal(createdId, viewModel.SelectedApprentice?.Id);

        Assert.False(viewModel.IsCreatePanelOpen);

        viewModel.Dispose();

    }

    private static ApprenticeSummaryDto NewSummary(Guid id, string name, string status) =>
        new(id, null, name, "goal", status, 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ApprenticeDetailDto NewDetail(Guid id, Guid? parentId, string name, IReadOnlyList<PlanStep> plan) =>
        new(
            id,
            null,
            parentId,
            name,
            "goal",
            plan,
            0,
            "Running",
            null,
            "/tmp/ws",
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private sealed class FakeWarTableDataSource : IWarTableDataSource
    {

        public IReadOnlyList<ApprenticeSummaryDto> Summaries { get; init; } = [];

        public Dictionary<Guid, ApprenticeDetailDto> Details { get; } = [];

        public IReadOnlyList<ChronicleFrame> ChronicleFrames { get; init; } = [];

        public bool ThrowCancellationOnLineage { get; init; }

        public ApprenticeDetailDto? Created { get; init; }

        public CreateApprenticeRequest? LastCreate { get; private set; }

        public InterveneApprenticeRequest? LastIntervention { get; private set; }

        public List<string> Actions { get; } = [];

        public List<Guid> RequestedDetailIds { get; } = [];

        public int ChronicleSubscriptions { get; private set; }

        public Task<IReadOnlyList<ApprenticeSummaryDto>> ListApprenticesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Summaries);

        public Task<ApprenticeDetailDto?> GetApprenticeAsync(Guid id, CancellationToken cancellationToken)
        {

            RequestedDetailIds.Add(id);

            return Task.FromResult(
                Details.TryGetValue(id, out ApprenticeDetailDto? detail)
                    ? detail
                    : null);

        }

        public Task<ApprenticeDetailDto?> CreateApprenticeAsync(CreateApprenticeRequest request, CancellationToken cancellationToken)
        {

            LastCreate = request;

            return Task.FromResult(Created);

        }

        public Task<bool> StartAsync(Guid id, CancellationToken cancellationToken)
        {

            Actions.Add("start");

            return Task.FromResult(true);

        }

        public Task<bool> PauseAsync(Guid id, CancellationToken cancellationToken)
        {

            Actions.Add("pause");

            return Task.FromResult(true);

        }

        public Task<bool> ResumeAsync(Guid id, CancellationToken cancellationToken)
        {

            Actions.Add("resume");

            return Task.FromResult(true);

        }

        public Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken)
        {

            Actions.Add("cancel");

            return Task.FromResult(true);

        }

        public Task<ApprenticeDetailDto?> ReweaveAsync(Guid id, ReweaveApprenticeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Details.TryGetValue(id, out ApprenticeDetailDto? detail) ? detail : null);

        public Task<bool> InterveneAsync(Guid id, InterveneApprenticeRequest request, CancellationToken cancellationToken)
        {

            Actions.Add("intervene");

            LastIntervention = request;

            return Task.FromResult(true);

        }

        public Task<IReadOnlyList<ApprenticeDetailDto>> GetLineageAsync(Guid id, CancellationToken cancellationToken)
        {

            if (ThrowCancellationOnLineage)
            {

                throw new OperationCanceledException(cancellationToken);

            }

            List<ApprenticeDetailDto> chain = [];

            Guid? current = id;

            HashSet<Guid> visited = [];

            while (current is { } currentId && visited.Add(currentId) && Details.TryGetValue(currentId, out ApprenticeDetailDto? detail))
            {

                chain.Add(detail);

                current = detail.ParentApprenticeId;

            }

            return Task.FromResult<IReadOnlyList<ApprenticeDetailDto>>(chain);

        }

        public async IAsyncEnumerable<ChronicleFrame> StreamChronicleAsync(Guid id, [EnumeratorCancellation] CancellationToken cancellationToken)
        {

            ChronicleSubscriptions++;

            foreach (ChronicleFrame frame in ChronicleFrames)
            {

                yield return frame;

                await Task.Yield();

            }

        }

        public Task<IReadOnlyList<CampaignDto>> ListCampaignsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CampaignDto>>([]);

        public Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkspaceInfo>>([]);

    }

}
