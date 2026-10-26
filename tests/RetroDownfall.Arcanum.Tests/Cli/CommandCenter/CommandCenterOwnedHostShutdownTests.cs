using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterOwnedHostShutdownTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Owned_host_quit_also_bounds_credential_preparation()
    {
        ManualTimerTimeProvider clock = new();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ArcanumApiCredentialLease credentials = ArcanumApiCredentialLeaseTestFactory.Create(
            _ => Task.FromResult(SecretStoreReadResult.Missing()), async token =>
            {
                entered.TrySetResult();

                await release.Task.WaitAsync(token);

                return SecretStoreReadResult.Ok("test-key");
            }, "test-key");

        Handler handler = new((_, _) => Task.FromResult(Success()));

        TestCapturingLogger<CommandCenterHost> logger = new();

        await using ServiceProvider provider = CreateProvider(handler, clock, logger, credentials);

        Task quit = Stop(provider, State(ServeLaunchStatus.Started));

        try
        {
            await entered.Task.WaitAsync(HangGuard);

            clock.Advance(TimeSpan.FromSeconds(5));

            await quit.WaitAsync(HangGuard);

            Assert.Empty(handler.Paths);

            Assert.Equal(0, clock.ActiveTimers);

            Assert.Contains("5 seconds", Assert.Single(logger.Entries).Message, StringComparison.Ordinal);
        }
        finally
        {
            release.TrySetResult();

            await quit.WaitAsync(HangGuard);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_host_quit_bounds_headers_and_body_to_five_seconds(bool holdBody)
    {
        ManualTimerTimeProvider clock = new();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using GatedBodyStream body = new(SuccessBytes());

        Handler handler = new(async (_, token) =>
        {
            entered.TrySetResult();

            if (!holdBody)
            {
                await release.Task.WaitAsync(token);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = holdBody ? new StreamContent(body) : new ByteArrayContent(SuccessBytes()),
            };
        });

        TestCapturingLogger<CommandCenterHost> logger = new();

        await using ServiceProvider provider = CreateProvider(handler, clock, logger);

        CommandCenterState state = State(ServeLaunchStatus.Started);

        state.ExitCode = 130;

        Task quit = Stop(provider, state);

        try
        {
            await entered.Task.WaitAsync(HangGuard);

            if (holdBody)
            {
                await body.ReadStarted.WaitAsync(HangGuard);
            }

            clock.Advance(TimeSpan.FromMilliseconds(4999));

            Assert.False(quit.IsCompleted);

            clock.Advance(TimeSpan.FromMilliseconds(1));

            await quit.WaitAsync(HangGuard);

            Assert.Equal(130, state.ExitCode);

            Assert.Equal(0, clock.ActiveTimers);

            TestLogEntry warning = Assert.Single(logger.Entries);

            Assert.Equal(LogLevel.Warning, warning.Level);

            Assert.Contains("5 seconds", warning.Message, StringComparison.Ordinal);

            Assert.Null(warning.Exception);

            Assert.Equal(["/api/server/quit"], handler.Paths);
        }
        finally
        {
            release.TrySetResult();

            body.Release();

            await quit.WaitAsync(HangGuard);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ServeLaunchStatus.AlreadyRunning)]
    [InlineData(ServeLaunchStatus.AuthFailed)]
    [InlineData(ServeLaunchStatus.LaunchDisabled)]
    [InlineData(ServeLaunchStatus.Failed)]
    public async Task Unowned_hosts_are_never_sent_quit_requests(ServeLaunchStatus? status)
    {
        ManualTimerTimeProvider clock = new();

        Handler handler = new((_, _) => Task.FromResult(Success()));

        TestCapturingLogger<CommandCenterHost> logger = new();

        await using ServiceProvider provider = CreateProvider(handler, clock, logger);

        await Stop(provider, State(status)).WaitAsync(HangGuard);

        Assert.Empty(handler.Paths);

        Assert.Empty(logger.Entries);

        Assert.Equal(0, clock.TimersCreated);
    }

    [Fact]
    public async Task Successful_owned_host_quit_is_quiet_and_releases_its_timers()
    {
        ManualTimerTimeProvider clock = new();

        Handler handler = new((_, _) => Task.FromResult(Success()));

        TestCapturingLogger<CommandCenterHost> logger = new();

        await using ServiceProvider provider = CreateProvider(handler, clock, logger);

        CommandCenterState state = State(ServeLaunchStatus.Started);

        state.ExitCode = 17;

        await Stop(provider, state).WaitAsync(HangGuard);

        Assert.Equal(["/api/server/quit"], handler.Paths);

        Assert.Equal(17, state.ExitCode);

        Assert.Empty(logger.Entries);

        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task Cancelled_startup_still_attempts_owned_host_cleanup_and_returns_130()
    {
        ManualTimerTimeProvider clock = new();

        using CancellationTokenSource invocation = new();

        Handler handler = new((request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/server/quit")
            {
                Assert.False(token.IsCancellationRequested);

                return Task.FromResult(Success());
            }

            invocation.Cancel();

            return Task.FromCanceled<HttpResponseMessage>(token);
        });

        TestCapturingLogger<CommandCenterHost> logger = new();

        await using ServiceProvider provider = CreateProvider(handler, clock, logger);

        int result = await provider.GetRequiredService<ICommandCenterHost>().RunAsync(invocation.Token).WaitAsync(HangGuard);

        Assert.Equal(130, result);

        Assert.Equal(1, handler.Paths.Count(static path => path == "/api/server/quit"));

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level >= LogLevel.Warning);

        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task Failed_quit_warns_without_leaking_response_text_or_changing_exit_code()
    {
        ManualTimerTimeProvider clock = new();

        Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("private-upstream-payload"),
        }));

        TestCapturingLogger<CommandCenterHost> logger = new();

        await using ServiceProvider provider = CreateProvider(handler, clock, logger);

        CommandCenterState state = State(ServeLaunchStatus.Started);

        state.ExitCode = 2;

        await Stop(provider, state).WaitAsync(HangGuard);

        TestLogEntry warning = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, warning.Level);

        Assert.DoesNotContain("private-upstream-payload", warning.Message, StringComparison.Ordinal);

        Assert.Null(warning.Exception);

        Assert.Equal(2, state.ExitCode);

        Assert.Equal(0, clock.ActiveTimers);
    }

    private static Task Stop(ServiceProvider provider, CommandCenterState state) =>
        (Task)typeof(CommandCenterHost).GetMethod("StopHostIfWeStartedItAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(provider.GetRequiredService<ICommandCenterHost>(), [state])!;

    private static CommandCenterState State(ServeLaunchStatus? status) => new(new SessionLogBuffer())
    {
        ServeLaunch = status is { } value
            ? new(value, HealthProbeState.Healthy, TimeSpan.Zero, null, null) : null,
    };

    private static byte[] SuccessBytes() => JsonSerializer.SerializeToUtf8Bytes(
        new ApiResponse<bool>(true, true, null), ArcanumJsonContext.Default.ApiResponseBoolean);

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(SuccessBytes()),
    };

    private static ServiceProvider CreateProvider(Handler handler, TimeProvider clock, ILogger<CommandCenterHost> logger,
        ArcanumApiCredentialLease? credentials = null)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.RemoveAll<IArcanumServeLauncher>();

        services.AddSingleton<IArcanumServeLauncher>(new OwnedLauncher());

        services.RemoveAll<TimeProvider>();

        services.AddSingleton(clock);

        services.RemoveAll<ILogger<CommandCenterHost>>();

        services.AddSingleton(logger);

        services.RemoveAll<ArcanumApiClient>();

        services.AddSingleton(new ArcanumApiClient(new ClientFactory(handler), credentials ?? ArcanumApiCredentialLeaseTestFactory.Create("test-key"))
        {
            HeadersDeadlineClock = clock,
        });

        return services.BuildServiceProvider();
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private sealed class OwnedLauncher : IArcanumServeLauncher
    {
        public Task<ServeLaunchResult> EnsureRunningAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ServeLaunchResult(ServeLaunchStatus.Started, HealthProbeState.Healthy,
                TimeSpan.Zero, null, null));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);

            return respond(request, cancellationToken);
        }
    }
}
