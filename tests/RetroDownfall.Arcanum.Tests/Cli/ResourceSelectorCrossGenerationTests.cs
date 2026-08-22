using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ResourceSelectorCrossGenerationTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-selector-generation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Selection_skips_recency_when_the_exact_id_disappears_before_client_admission()
    {

        bool available = true;

        int fetches = 0;

        RecordingArcanumClientMutationBoundary boundary = new()
        {
            BeforeMutation = () => available = false,
        };

        RecentResourceStore recents = new(
            Path.Combine(_root, "recent-resources.txt"),
            boundary);

        ResourceSelector<Candidate> selector = new(new UnusedPicker(), recents);

        ResourceSelectionResult<Candidate> result = await selector.SelectAsync(
            Request(
                (_, cancellationToken) =>
                {

                    cancellationToken.ThrowIfCancellationRequested();

                    fetches++;

                    return Task.FromResult(
                        Result<ResourcePage<Candidate>>.Success(
                            new ResourcePage<Candidate>(
                                available ? [Selected] : [],
                                null)));

                }),
            CancellationToken.None);

        Assert.Equal(ResourceSelectionStatus.Selected, result.Status);

        Assert.Empty(recents.GetRecentIds("session"));

        Assert.Equal(2, fetches);

        Assert.Equal(1, boundary.Calls);

    }

    [Fact]
    public async Task Recency_revalidation_failure_is_nonfatal_and_does_not_write()
    {

        int fetches = 0;

        RecordingArcanumClientMutationBoundary boundary = new();

        RecentResourceStore recents = new(
            Path.Combine(_root, "recent-resources.txt"),
            boundary);

        ResourceSelector<Candidate> selector = new(new UnusedPicker(), recents);

        ResourceSelectionResult<Candidate> result = await selector.SelectAsync(
            Request(
                (_, cancellationToken) =>
                {

                    cancellationToken.ThrowIfCancellationRequested();

                    fetches++;

                    return Task.FromResult(
                        fetches == 1
                            ? Result<ResourcePage<Candidate>>.Success(
                                new ResourcePage<Candidate>([Selected], null))
                            : Result<ResourcePage<Candidate>>.Failure(
                                new Error(
                                    "Cli.RevalidationUnavailable",
                                    "The replacement host was unavailable.")));

                }),
            CancellationToken.None);

        Assert.Equal(ResourceSelectionStatus.Selected, result.Status);

        Assert.Empty(recents.GetRecentIds("session"));

        Assert.Equal(2, fetches);

        Assert.Equal(1, boundary.Calls);

    }

    [Fact]
    public async Task Cancellation_during_recency_revalidation_propagates_without_writing()
    {

        using CancellationTokenSource cancellation = new();

        RecordingArcanumClientMutationBoundary boundary = new()
        {
            BeforeMutation = cancellation.Cancel,
        };

        RecentResourceStore recents = new(
            Path.Combine(_root, "recent-resources.txt"),
            boundary);

        ResourceSelector<Candidate> selector = new(new UnusedPicker(), recents);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => selector.SelectAsync(
                Request(
                    (_, cancellationToken) =>
                    {

                        cancellationToken.ThrowIfCancellationRequested();

                        return Task.FromResult(
                            Result<ResourcePage<Candidate>>.Success(
                                new ResourcePage<Candidate>([Selected], null)));

                    }),
                cancellation.Token));

        Assert.Empty(recents.GetRecentIds("session"));

        Assert.Equal(1, boundary.Calls);

    }

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    private static ResourceSelectionRequest<Candidate> Request(
        Func<string?, CancellationToken, Task<Result<ResourcePage<Candidate>>>> fetchPageAsync) =>
        new(
            "session",
            Selected.Id,
            IsInteractive: false,
            new ResourceDescriptor<Candidate>(
                "session",
                ["Title"],
                static candidate => candidate.Id,
                static candidate => candidate.Name,
                static candidate => candidate.Name,
                static candidate => [candidate.Name]),
            fetchPageAsync);

    private static Candidate Selected { get; } =
        new("selected-session", "Selected session");

    private sealed record Candidate(string Id, string Name);

    private sealed class UnusedPicker : IResourcePicker
    {

        public Task<ResourcePickerResult<T>> PickAsync<T>(
            ResourcePickerRequest<T> request,
            CancellationToken cancellationToken)
            where T : class =>
            throw new InvalidOperationException(
                "Exact-id selection must not open the interactive picker.");

    }

}
