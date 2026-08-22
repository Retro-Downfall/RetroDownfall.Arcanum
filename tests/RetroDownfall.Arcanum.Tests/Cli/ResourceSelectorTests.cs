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
    public async Task Ambiguous_name_can_opt_into_interactive_selection_for_operational_resources()
    {
        SelectionFixture fixture = new(
            new Candidate("one", "Mordain", "global"),
            new Candidate("two", "Morrigan", "workspace"));

        fixture.Picker.SelectedId = "two";

        ResourceSelectionResult<Candidate> result = await fixture.SelectAsync(
            "mor",
            interactive: true,
            pickAmbiguousIdentifiers: true);

        Assert.Equal(ResourceSelectionStatus.Selected, result.Status);
        Assert.Equal("two", result.Value!.Id);
        Assert.Equal(["one", "two"], fixture.Picker.LastChoiceIds);
    }

    [Fact]
    public async Task Omitted_identifier_in_non_interactive_mode_is_actionable_without_prompting()
    {
        SelectionFixture fixture = new(
            new Candidate("one", "Mordain", "active"),
            new Candidate("two", "Selene", "paused"));

        ResourceSelectionResult<Candidate> result = await fixture.SelectAsync(null, interactive: false);

        Assert.Equal(ResourceSelectionStatus.Error, result.Status);
        Assert.Contains("identifier or name is required", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Mordain", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Picker.CallCount);
    }

    /// <summary>
    /// The selector is told only that no picker is available, never why. `--json` and `--print` make
    /// a real terminal report itself non-interactive, so a message that states redirection as fact
    /// asserts something about the operator's shell that is false, and hides the flag that actually
    /// suppressed the picker.
    /// </summary>
    [Fact]
    public async Task Omitted_identifier_does_not_blame_redirection_for_a_headless_flag()
    {
        SelectionFixture fixture = new(
            new Candidate("one", "Mordain", "active"),
            new Candidate("two", "Selene", "paused"));

        ResourceSelectionResult<Candidate> result = await fixture.SelectAsync(null, interactive: false);

        Assert.Equal(ResourceSelectionStatus.Error, result.Status);
        Assert.Contains("no interactive picker is available", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--json", result.Error, StringComparison.Ordinal);
        Assert.Contains("--print", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "required when input or output is redirected",
            result.Error,
            StringComparison.OrdinalIgnoreCase);
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
    public async Task Interactive_omission_fetches_only_the_page_the_picker_needs()
    {

        FakePicker picker = new() { SelectedId = "one" };

        FakeRecentResourceStore recent = new();

        ResourceSelector<Candidate> selector = new(picker, recent);

        int calls = 0;

        ResourceSelectionRequest<Candidate> request = new(
            "session",
            Identifier: null,
            IsInteractive: true,
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
                    ? new ResourcePage<Candidate>(
                        [new Candidate("one", "first", "active")],
                        "page-2")
                    : new ResourcePage<Candidate>(
                        [new Candidate("two", "second", "active")],
                        null);

                return Task.FromResult(
                    Result<ResourcePage<Candidate>>.Success(page));

            });

        ResourceSelectionResult<Candidate> result = await selector.SelectAsync(request);

        Assert.Equal(ResourceSelectionStatus.Selected, result.Status);

        Assert.Equal("one", result.Value!.Id);

        Assert.Equal(1, calls);

        Assert.Equal(["one"], picker.LastChoiceIds);

    }

    [Fact]
    public async Task Interactive_omission_loads_the_next_page_only_when_the_picker_requests_it()
    {

        FakePicker picker = new()
        {

            SelectedId = "two",

            RequestNextPageBeforeSelection = true,

        };

        FakeRecentResourceStore recent = new();

        ResourceSelector<Candidate> selector = new(picker, recent);

        int calls = 0;

        ResourceSelectionRequest<Candidate> request = new(
            "session",
            Identifier: null,
            IsInteractive: true,
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
                    ? new ResourcePage<Candidate>(
                        [new Candidate("one", "first", "active")],
                        "page-2")
                    : new ResourcePage<Candidate>(
                        [new Candidate("two", "second", "active")],
                        null);

                return Task.FromResult(
                    Result<ResourcePage<Candidate>>.Success(page));

            });

        ResourceSelectionResult<Candidate> result = await selector.SelectAsync(request);

        Assert.Equal(ResourceSelectionStatus.Selected, result.Status);

        Assert.Equal("two", result.Value!.Id);

        Assert.Equal(2, calls);

        Assert.Equal(2, picker.CallCount);

        Assert.Equal([["one"], ["two"]], picker.ChoiceHistory);

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
    public async Task Selection_awaits_optional_recency_persistence_before_returning()
    {

        SelectionFixture fixture = new(
            new Candidate("one", "Mordain", "active"));

        TaskCompletionSource persistence = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.Recent.Persistence = persistence.Task;

        Task<ResourceSelectionResult<Candidate>> selection =
            fixture.SelectAsync("one", interactive: false);

        Assert.False(selection.IsCompleted);

        persistence.SetResult();

        ResourceSelectionResult<Candidate> result = await selection;

        Assert.Equal(ResourceSelectionStatus.Selected, result.Status);

        Assert.Equal("one", result.Value!.Id);

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

    [Fact]
    public async Task Selector_fetches_beyond_the_former_total_page_ceiling()
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
            (_, _) =>
            {
                calls++;

                bool isTargetPage = calls == 101;
                ResourcePage<Candidate> page = new(
                    [new Candidate($"id-{calls}", isTargetPage ? "target" : $"candidate-{calls}", "active")],
                    isTargetPage ? null : $"page-{calls + 1}");

                return Task.FromResult(Result<ResourcePage<Candidate>>.Success(page));
            });

        ResourceSelectionResult<Candidate> result = await selector.SelectAsync(request);

        Assert.Equal(ResourceSelectionStatus.Selected, result.Status);
        Assert.Equal("target", result.Value!.Name);
        Assert.Equal(101, calls);
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

        public Task<ResourceSelectionResult<Candidate>> SelectAsync(
            string? identifier,
            bool interactive,
            bool pickAmbiguousIdentifiers = false)
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
                    Result<ResourcePage<Candidate>>.Success(new ResourcePage<Candidate>(_candidates, null))),
                PickAmbiguousIdentifiers: pickAmbiguousIdentifiers);

            return selector.SelectAsync(request);
        }
    }

    private sealed class FakePicker : IResourcePicker
    {
        public int CallCount { get; private set; }

        public string? SelectedId { get; set; }

        public bool RequestNextPageBeforeSelection { get; set; }

        public bool LastRequestWasSearchable { get; private set; }

        public string[] LastChoiceIds { get; private set; } = [];

        public List<string[]> ChoiceHistory { get; } = [];

        public Task<ResourcePickerResult<T>> PickAsync<T>(
            ResourcePickerRequest<T> request,
            CancellationToken cancellationToken)
            where T : class
        {
            CallCount++;
            LastRequestWasSearchable = request.Searchable;
            LastChoiceIds = request.Choices.Select(request.Descriptor.GetId).ToArray();

            ChoiceHistory.Add(LastChoiceIds);

            if (RequestNextPageBeforeSelection && CallCount == 1)
            {

                return Task.FromResult(ResourcePickerResult<T>.NextPage());

            }

            T? selected = request.Choices.FirstOrDefault(
                value => string.Equals(request.Descriptor.GetId(value), SelectedId, StringComparison.Ordinal));

            return Task.FromResult(selected is null
                ? ResourcePickerResult<T>.Cancelled()
                : ResourcePickerResult<T>.Selected(selected));
        }
    }

    private sealed class FakeRecentResourceStore : IRecentResourceStore
    {
        public string[] RecentIds { get; set; } = [];

        public List<(string Kind, string Id)> Remembered { get; } = [];

        public Task Persistence { get; set; } = Task.CompletedTask;

        public IReadOnlyList<string> GetRecentIds(string resourceKind) => RecentIds;

        public async Task RememberAsync(
            string resourceKind,
            string id,
            Func<CancellationToken, Task<Result<bool>>> revalidateAsync,
            CancellationToken cancellationToken = default)
        {

            Remembered.Add((resourceKind, id));

            _ = revalidateAsync;

            await Persistence.WaitAsync(cancellationToken);

        }
    }
}
