using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The dynamic resolver runs inside a tab-completion keystroke. Its obligations are behavioural,
/// not cosmetic: never print, never block the shell, never start the host, and never cache
/// anything an operator would mind seeing in a scrollback buffer.
/// </summary>
[Collection("GlobalConsole")]
public sealed class CliCompletionResolverTests
{

    [Fact]
    public async Task An_unavailable_host_yields_no_suggestions_and_no_output()
    {

        CliCompletionResolver.ClearCache();

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(new RefusingHandler()),
            ["completion", "resolve", "model"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.True(
            string.IsNullOrWhiteSpace(result.Output),
            $"Expected no stdout suggestions, got: {result.Output}");

        Assert.True(
            string.IsNullOrWhiteSpace(result.Error),
            $"A completion must never write to stderr, got: {result.Error}");

    }

    /// <summary>
    /// A hung host must not hang the operator's shell. The resolver gives up on its own budget
    /// rather than waiting for a response that may never come.
    /// </summary>
    [Fact]
    public async Task A_hanging_host_gives_up_within_the_budget()
    {

        CliCompletionResolver.ClearCache();

        Stopwatch stopwatch = Stopwatch.StartNew();

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(new HangingHandler()),
            ["completion", "resolve", "campaign"]);

        stopwatch.Stop();

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.True(string.IsNullOrWhiteSpace(result.Output));

        Assert.True(
            stopwatch.Elapsed < CliCompletionResolver.Budget + TimeSpan.FromSeconds(5),
            $"Resolution took {stopwatch.Elapsed}, which is well past the {CliCompletionResolver.Budget} budget.");

    }

    /// <summary>
    /// Resolution must never spawn <c>arcanum serve</c>: a tab keystroke starting a background
    /// server would be a serious surprise.
    /// </summary>
    [Fact]
    public async Task Resolution_never_starts_the_host()
    {

        CliCompletionResolver.ClearCache();

        RecordingServeLauncher launcher = new();

        ServiceCollection services = CreateServices(new RefusingHandler());

        services.AddSingleton<IArcanumServeLauncher>(launcher);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["completion", "resolve", "session"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(0, launcher.Calls);

    }

    [Fact]
    public async Task An_unknown_provider_yields_nothing_without_calling_the_api()
    {

        CliCompletionResolver.ClearCache();

        RefusingHandler handler = new();

        CliCompletionResolver resolver = new(
            ApiClient(handler),
            TimeProvider.System);

        IReadOnlyList<string> values = await resolver.ResolveAsync("not-a-provider", CancellationToken.None);

        Assert.Empty(values);

        Assert.Equal(0, handler.Calls);

    }

    [Fact]
    public void Every_declared_provider_has_a_stable_identifier()
    {

        Assert.Equal(
            CliCompletionProviders.All.Length,
            CliCompletionProviders.All.Distinct(StringComparer.Ordinal).Count());

        Assert.All(
            CliCompletionProviders.All,
            static provider => Assert.Matches("^[a-z][a-z-]*$", provider));

    }

    /// <summary>
    /// The binding table annotates which symbols have a live source. Both directions are checked so
    /// it cannot drift from the tree: every binding must name a symbol that exists.
    /// </summary>
    [Fact]
    public void Every_completion_binding_names_a_symbol_that_exists_in_the_tree()
    {

        HashSet<string> symbols = [];

        foreach (CliSurfaceCommand command in CliSurfaceTests.Walk(CliSurfaceTests.BuildMap()))
        {

            foreach (CliSurfaceOption option in command.Options)
            {

                symbols.Add(option.Name);

            }

            foreach (CliSurfaceArgument argument in command.Arguments)
            {

                symbols.Add(argument.Name);

            }

        }

        List<string> stale =
        [
            .. CliCompletionBindings.SymbolNames.Where(name => !symbols.Contains(name)),
        ];

        Assert.Empty(stale);

    }

    private static ArcanumApiClient ApiClient(HttpMessageHandler handler)
    {

        ServiceCollection services = CreateServices(handler);

        ServiceProvider provider = services.BuildServiceProvider();

        return provider.GetRequiredService<ArcanumApiClient>();

    }

    private static ServiceCollection CreateServices(HttpMessageHandler handler)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<IHttpClientFactory>(new SingleHandlerFactory(handler));

        return services;

    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://127.0.0.1:9/"),
                Timeout = Timeout.InfiniteTimeSpan,
            };

    }

    private sealed class RefusingHandler : HttpMessageHandler
    {

        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            Interlocked.Increment(ref _calls);

            throw new HttpRequestException("connection refused");

        }

    }

    private sealed class HangingHandler : HttpMessageHandler
    {

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK);

        }

    }

    private sealed class RecordingServeLauncher : IArcanumServeLauncher
    {

        private int _calls;

        public int Calls => _calls;

        public Task<ServeLaunchResult> EnsureRunningAsync(CancellationToken cancellationToken)
        {

            Interlocked.Increment(ref _calls);

            return Task.FromResult(
                new ServeLaunchResult(
                    ServeLaunchStatus.AlreadyRunning,
                    HealthProbeState.Healthy,
                    TimeSpan.Zero,
                    null,
                    null));

        }

    }

}
