using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Reliquary;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class ReliquaryViewModelTests
{

    [Fact]
    public async Task Refresh_PopulatesCachedModelsAndServers()
    {

        FakeReliquaryDataSource dataSource = new()
        {

            Models = [new CachedModelInfo { CacheKey = "m", SourceUrl = "https://example.com/m.gguf" }],

            Servers = [new LlamaServerInfo { CacheKey = "m", State = LlamaServerState.Running, Port = 8080, Endpoint = "http://localhost:8080" }],

        };

        ReliquaryViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Single(viewModel.CachedModels);

        Assert.Single(viewModel.Servers);

        Assert.Equal("m", viewModel.CachedModels[0].CacheKey);

    }

    [Fact]
    public async Task Start_Stop_Warmup_CallDataSourceWithSelectedCacheKey()
    {

        FakeReliquaryDataSource dataSource = new()
        {

            Models = [new CachedModelInfo { CacheKey = "m" }],

            StartResult = new LlamaServerInfo { CacheKey = "m", State = LlamaServerState.Running, Port = 8080, Endpoint = "e" },

            WarmupResult = new WarmupResultDto(true, 12, "e"),

        };

        ReliquaryViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        viewModel.SelectedModel = viewModel.CachedModels[0];

        await viewModel.StartServerAsync(CancellationToken.None);

        await viewModel.StopServerAsync(CancellationToken.None);

        await viewModel.WarmupServerAsync(CancellationToken.None);

        Assert.Equal("m", dataSource.LastStartKey);

        Assert.Equal("m", dataSource.LastStopKey);

        Assert.Equal("m", dataSource.LastWarmupKey);

        Assert.Null(viewModel.LastError);

    }

    [Fact]
    public async Task StartWithoutSelection_SetsStatusText()
    {

        ReliquaryViewModel viewModel = NewViewModel(new FakeReliquaryDataSource());

        await viewModel.StartServerAsync(CancellationToken.None);

        Assert.Equal("Select a cached model first.", viewModel.StatusText);

    }

    [Fact]
    public async Task Pull_ConsumesProgressAndAppendsLines()
    {

        FakeReliquaryDataSource dataSource = new()
        {

            PullProgress =
            [

                new LlamaPullProgress { CacheKey = "m", BytesDownloaded = 100, Percent = 50, Completed = false },

                new LlamaPullProgress { CacheKey = "m", BytesDownloaded = 200, Percent = 100, Completed = true },

            ],

        };

        ReliquaryViewModel viewModel = NewViewModel(dataSource);

        viewModel.PullSourceUrl = "https://example.com/m.gguf";

        await viewModel.PullAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.PullLines.Count);

        Assert.Contains(viewModel.PullLines, static line => line.Contains("completed"));

        Assert.Equal("Pull complete.", viewModel.StatusText);

        Assert.False(viewModel.IsPulling);

    }

    [Fact]
    public async Task Pull_Cancel_SurfacesCancelledStatus()
    {

        FakeReliquaryDataSource dataSource = new()
        {

            PullProgress = [new LlamaPullProgress { CacheKey = "m", BytesDownloaded = 1, Percent = 10, Completed = false }],

            StallAfterFirst = true,

        };

        ReliquaryViewModel viewModel = NewViewModel(dataSource);

        viewModel.PullSourceUrl = "https://example.com/m.gguf";

        Task pull = viewModel.PullAsync(CancellationToken.None);

        await dataSource.YieldedFirst.Task.ConfigureAwait(true);

        viewModel.CancelPullCommand.Execute(null);

        await pull.ConfigureAwait(true);

        Assert.Equal("Pull cancelled.", viewModel.StatusText);

        Assert.False(viewModel.IsPulling);

        Assert.Contains(viewModel.PullLines, static line => line.Contains("pulling"));

        viewModel.Dispose();

    }

    [Fact]
    public async Task Pull_WhenStreamThrows_SetsLastError()
    {

        FakeReliquaryDataSource dataSource = new() { ThrowOnPull = true };

        ReliquaryViewModel viewModel = NewViewModel(dataSource);

        viewModel.PullSourceUrl = "https://example.com/m.gguf";

        await viewModel.PullAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(viewModel.LastError));

        Assert.False(viewModel.IsPulling);

    }

    private static ReliquaryViewModel NewViewModel(FakeReliquaryDataSource dataSource) =>
        new(dataSource, new FoundryFloorViewModel(new NullLogService()));

    private sealed class FakeReliquaryDataSource : IReliquaryDataSource
    {

        public IReadOnlyList<CachedModelInfo> Models { get; init; } = [];

        public IReadOnlyList<LlamaServerInfo> Servers { get; init; } = [];

        public LlamaServerInfo? StartResult { get; init; }

        public bool StopResult { get; init; } = true;

        public WarmupResultDto? WarmupResult { get; init; }

        public IReadOnlyList<LlamaPullProgress> PullProgress { get; init; } = [];

        public bool ThrowOnPull { get; init; }

        public bool StallAfterFirst { get; init; }

        public TaskCompletionSource YieldedFirst { get; } = new();

        public string? LastStartKey { get; private set; }

        public string? LastStopKey { get; private set; }

        public string? LastWarmupKey { get; private set; }

        public Task<IReadOnlyList<CachedModelInfo>> ListCachedModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CachedModelInfo>>(Models);

        public Task<IReadOnlyList<LlamaServerInfo>> ListServersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LlamaServerInfo>>(Servers);

        public Task<LlamaServerInfo?> StartServerAsync(string cacheKey, CancellationToken cancellationToken)
        {

            LastStartKey = cacheKey;

            return Task.FromResult(StartResult);

        }

        public Task<bool> StopServerAsync(string cacheKey, CancellationToken cancellationToken)
        {

            LastStopKey = cacheKey;

            return Task.FromResult(StopResult);

        }

        public Task<WarmupResultDto?> WarmupServerAsync(string cacheKey, CancellationToken cancellationToken)
        {

            LastWarmupKey = cacheKey;

            return Task.FromResult(WarmupResult);

        }

        public async IAsyncEnumerable<LlamaPullProgress> PullModelAsync(
            PullModelRequestDto request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {

            if (ThrowOnPull)
            {

                throw new InvalidOperationException("pull boom");

            }

            foreach (LlamaPullProgress progress in PullProgress)
            {

                yield return progress;

                if (StallAfterFirst)
                {

                    YieldedFirst.TrySetResult();

                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(true);

                }

                await Task.Yield();

            }

        }

    }

}
