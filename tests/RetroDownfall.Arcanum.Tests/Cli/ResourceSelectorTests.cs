using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ResourceSelectorTests
{
    [Fact]
    public async Task Exact_id_wins_over_name_matches()
    {
        SelectionFixture fixture = new(
            new Candidate("alpha-id", "first", "first summary"),
            new Candidate("other-id", "alpha-id", "name collision"));

        ResourceSelectionResult<Candidate> result = await fixture.SelectAsync("alpha-id", interactive: false);

        Assert.Equal(ResourceSelectionStatus.Selected, result.Status);
        Assert.Equal("alpha-id", result.Value!.Id);
    }

    [Fact]
    public async Task Exact_name_is_case_insensitive_and_wins_over_prefixes()
    {
        SelectionFixture fixture = new(
            new Candidate("one", "Mordain", "exact"),
            new Candidate("two", "Mordain Prime", "prefix"));

        ResourceSelectionResult<Candidate> result = await fixture.SelectAsync("mOrDaIn", interactive: false);

        Assert.Equal(ResourceSelectionStatus.Selected, result.Status);
        Assert.Equal("one", result.Value!.Id);
    }

    [Fact]
    public async Task Unique_name_prefix_resolves_deterministically()
    {
        SelectionFixture fixture = new(
            new Candidate("one", "Mordain", "first"),
            new Candidate("two", "Selene", "second"));

        ResourceSelectionResult<Candidate> result = await fixture.SelectAsync("mor", interactive: false);

        Assert.Equal(ResourceSelectionStatus.Selected, result.Status);
        Assert.Equal("one", result.Value!.Id);
    }

    [Fact]
    public async Task Ambiguous_name_never_selects_and_reports_safe_candidate_summaries()
    {
        SelectionFixture fixture = new(
            new Candidate("one", "Mordain", "active, updated today"),
            new Candidate("two", "Morrigan", "paused, updated yesterday"));

        ResourceSelectionResult<Candidate> result = await fixture.SelectAsync("mor", interactive: true);

        Assert.Equal(ResourceSelectionStatus.Error, result.Status);
        Assert.Contains("ambiguous", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Mordain", result.Error, StringComparison.Ordinal);
        Assert.Contains("active, updated today", result.Error, StringComparison.Ordinal);
        Assert.Contains("Morrigan", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Picker.CallCount);
    }

    [Fact]
    public async Task Omitted_identifier_in_non_interactive_mode_is_actionable_without_prompting()
    {
        SelectionFixture fixture = new(
            new Candidate("one", "Mordain", "active"),
            new Candidate("two", "Selene", "paused"));

        ResourceSelectionResult<Candidate> result = await fixture.SelectAsync(null, interactive: false);

        Assert.Equal(ResourceSelectionStatus.Error, result.Status);
        Assert.Contains("required when input or output is redirected", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Mordain", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Picker.CallCount);
    }

    [Fact]
    public async Task Interactive_omission_uses_searchable_picker_and_cancellation_does_not_remember()
    {
        SelectionFixture fixture = new(
            new Candidate("one", "Mordain", "active"),
            new Candidate("two", "Selene", "paused"));
        fixture.Picker.SelectedId = null;

        ResourceSelectionResult<Candidate> result = await fixture.SelectAsync(null, interactive: true);

        Assert.Equal(ResourceSelectionStatus.Cancelled, result.Status);
        Assert.Equal(1, fixture.Picker.CallCount);
        Assert.True(fixture.Picker.LastRequestWasSearchable);
        Assert.Empty(fixture.Recent.Remembered);
    }

    [Fact]
    public async Task Recent_selection_changes_picker_order_but_not_ambiguous_authority()
    {
        SelectionFixture fixture = new(
            new Candidate("one", "Mordain", "active"),
            new Candidate("two", "Morrigan", "paused"));
        fixture.Recent.RecentIds = ["two"];
        fixture.Picker.SelectedId = "two";

        ResourceSelectionResult<Candidate> picked = await fixture.SelectAsync(null, interactive: true);
        ResourceSelectionResult<Candidate> ambiguous = await fixture.SelectAsync("mor", interactive: false);

        Assert.Equal(ResourceSelectionStatus.Selected, picked.Status);
        Assert.Equal(["two", "one"], fixture.Picker.LastChoiceIds);
        Assert.Equal(ResourceSelectionStatus.Error, ambiguous.Status);
    }

    [Fact]
    public async Task Selector_fetches_all_pages_before_resolving_large_collection()
    {
        FakePicker picker = new();
        FakeRecentResourceStore recent = new();
        ResourceSelector<Candidate> selector = new(picker, recent);
        int calls = 0;
        ResourceSelectionRequest<Candidate> request = new(
            "session",
            "target",
            IsInteractive: false,
            new ResourceDescriptor<Candidate>(
                "session",
                ["Title", "Status"],
                static x => x.Id,
                static x => x.Name,
                static x => x.Summary,
                static x => [x.Name, x.Summary]),
            (token, _) =>
            {
                calls++;
                ResourcePage<Candidate> page = token is null
                    ? new ResourcePage<Candidate>([new Candidate("one", "first", "active")], "page-2")
                    : new ResourcePage<Candidate>([new Candidate("two", "target", "active")], null);
                return Task.FromResult(Result<ResourcePage<Candidate>>.Success(page));
            });

        ResourceSelectionResult<Candidate> result = await selector.SelectAsync(request);

        Assert.Equal(ResourceSelectionStatus.Selected, result.Status);
        Assert.Equal("two", result.Value!.Id);
        Assert.Equal(2, calls);
    }

    private sealed record Candidate(string Id, string Name, string Summary);

    private sealed class SelectionFixture
    {
        private readonly Candidate[] _candidates;

        public SelectionFixture(params Candidate[] candidates)
        {
            _candidates = candidates;
            Picker = new FakePicker { SelectedId = candidates.FirstOrDefault()?.Id };
            Recent = new FakeRecentResourceStore();
        }

        public FakePicker Picker { get; }

        public FakeRecentResourceStore Recent { get; }

        public Task<ResourceSelectionResult<Candidate>> SelectAsync(string? identifier, bool interactive)
        {
            ResourceSelector<Candidate> selector = new(Picker, Recent);
            ResourceSelectionRequest<Candidate> request = new(
                "candidate",
                identifier,
                interactive,
                new ResourceDescriptor<Candidate>(
                    "candidate",
                    ["Name", "Summary"],
                    static x => x.Id,
                    static x => x.Name,
                    static x => x.Summary,
                    static x => [x.Name, x.Summary]),
                (_, _) => Task.FromResult(
                    Result<ResourcePage<Candidate>>.Success(new ResourcePage<Candidate>(_candidates, null))));

            return selector.SelectAsync(request);
        }
    }

    private sealed class FakePicker : IResourcePicker
    {
        public int CallCount { get; private set; }

        public string? SelectedId { get; set; }

        public bool LastRequestWasSearchable { get; private set; }

        public string[] LastChoiceIds { get; private set; } = [];

        public Task<T?> PickAsync<T>(ResourcePickerRequest<T> request, CancellationToken cancellationToken)
            where T : class
        {
            CallCount++;
            LastRequestWasSearchable = request.Searchable;
            LastChoiceIds = request.Choices.Select(request.Descriptor.GetId).ToArray();
            T? selected = request.Choices.FirstOrDefault(
                value => string.Equals(request.Descriptor.GetId(value), SelectedId, StringComparison.Ordinal));
            return Task.FromResult(selected);
        }
    }

    private sealed class FakeRecentResourceStore : IRecentResourceStore
    {
        public string[] RecentIds { get; set; } = [];

        public List<(string Kind, string Id)> Remembered { get; } = [];

        public IReadOnlyList<string> GetRecentIds(string resourceKind) => RecentIds;

        public void Remember(string resourceKind, string id) => Remembered.Add((resourceKind, id));
    }
}
