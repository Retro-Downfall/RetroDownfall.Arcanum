using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ArcanumServeLauncherTests
{

    [Fact]
    public async Task AlreadyRunning_when_authenticated_health_probe_succeeds()
    {

        SequencedHandler handler = new(HttpStatusCode.OK);

        FakeServeProcessLauncher processLauncher = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            handler,
            processLauncher,
            apiKey: "test-key");

        ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.AlreadyRunning, result.Status);

        Assert.Equal(HealthProbeState.Healthy, result.Health);

        Assert.Equal(0, processLauncher.StartCount);

    }

    [Fact]
    public async Task Unauthorized_does_not_spawn()
    {

        SequencedHandler handler = new(HttpStatusCode.Unauthorized);

        FakeServeProcessLauncher processLauncher = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            handler,
            processLauncher,
            apiKey: "bad-key");

        ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.AuthFailed, result.Status);

        Assert.Equal(HealthProbeState.Unauthorized, result.Health);

        Assert.Equal(0, processLauncher.StartCount);

        // Documented: launcher never deletes arcanum.pid on Unauthorized; spawn simply is not called.
    }

    [Fact]
    public async Task No_key_then_spawn_then_key_appears_poll_succeeds()
    {

        FakeSecretStore secretStore = new() { ApiKey = null };

        SequencedHandler handler = new(
            _ => throw ConnectionRefused(),
            _ =>
            {
                secretStore.ApiKey = "appeared-key";

                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        FakeServeProcessLauncher processLauncher = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            handler,
            processLauncher,
            secretStore);

        ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.Started, result.Status);

        Assert.Equal(HealthProbeState.Healthy, result.Health);

        Assert.Equal(1, processLauncher.StartCount);

        Assert.Equal("1", processLauncher.LastOptions!.Env[ArcanumServeLauncher.AutoLaunchedEnvVar]);

    }

    [Fact]
    public async Task Post_spawn_unauthorized_with_null_key_keeps_polling()
    {

        FakeSecretStore secretStore = new() { ApiKey = null };

        int probeIndex = 0;

        SequencedHandler handler = new(_ =>
        {

            probeIndex++;

            if (probeIndex == 1)
            {
                throw ConnectionRefused();
            }

            // Probes 2–3: Unauthorized while key is still null (first-run race — keep polling).
            if (probeIndex is 2 or 3)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            secretStore.ApiKey = "bootstrap-key";

            return new HttpResponseMessage(HttpStatusCode.OK);

        });

        FakeServeProcessLauncher processLauncher = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            handler,
            processLauncher,
            secretStore);

        ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.Started, result.Status);

        Assert.Equal(1, processLauncher.StartCount);

        Assert.True(probeIndex >= 4);

    }

    [Fact]
    public async Task Post_spawn_unauthorized_with_non_null_key_eventually_AuthFailed()
    {

        SequencedHandler handler = new(
            _ => throw ConnectionRefused(),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        FakeServeProcessLauncher processLauncher = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            handler,
            processLauncher,
            apiKey: "stored-key");

        ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.AuthFailed, result.Status);

        Assert.Equal(HealthProbeState.Unauthorized, result.Health);

        Assert.Equal(1, processLauncher.StartCount);

    }

    [Fact]
    public async Task Launch_disabled_when_noninteractive()
    {

        SequencedHandler handler = new(HttpStatusCode.OK);

        FakeServeProcessLauncher processLauncher = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            handler,
            processLauncher,
            apiKey: "test-key",
            interactive: false);

        ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.LaunchDisabled, result.Status);

        Assert.Equal(HealthProbeState.NotAttempted, result.Health);

        Assert.Equal(0, processLauncher.StartCount);

        Assert.Equal(0, handler.CallCount);

    }

    [Fact]
    public async Task Launch_disabled_when_ARCANUM_NO_AUTO_SERVE_set()
    {

        string? original = global::System.Environment.GetEnvironmentVariable(ArcanumServeLauncher.NoAutoServeEnvVar);

        try
        {

            global::System.Environment.SetEnvironmentVariable(ArcanumServeLauncher.NoAutoServeEnvVar, "1");

            SequencedHandler handler = new(HttpStatusCode.OK);

            FakeServeProcessLauncher processLauncher = new();

            ArcanumServeLauncher launcher = CreateLauncher(
                handler,
                processLauncher,
                apiKey: "test-key",
                interactive: true);

            ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

            Assert.Equal(ServeLaunchStatus.LaunchDisabled, result.Status);

            Assert.Equal(0, processLauncher.StartCount);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(ArcanumServeLauncher.NoAutoServeEnvVar, original);

        }

    }

    [Fact]
    public async Task NO_COLOR_does_not_disable_auto_serve()
    {

        // Launcher gates on ICliEnvironment.IsInteractive only (not ColorEnabled / NO_COLOR).
        // CliEnvironment already encodes redirect into IsInteractive; we inject interactive=true.
        string? originalNoColor = global::System.Environment.GetEnvironmentVariable("NO_COLOR");

        string? originalNoAuto = global::System.Environment.GetEnvironmentVariable(ArcanumServeLauncher.NoAutoServeEnvVar);

        try
        {

            global::System.Environment.SetEnvironmentVariable("NO_COLOR", "1");

            global::System.Environment.SetEnvironmentVariable(ArcanumServeLauncher.NoAutoServeEnvVar, null);

            SequencedHandler handler = new(HttpStatusCode.OK);

            FakeServeProcessLauncher processLauncher = new();

            ArcanumServeLauncher launcher = CreateLauncher(
                handler,
                processLauncher,
                apiKey: "test-key",
                interactive: true);

            ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

            Assert.Equal(ServeLaunchStatus.AlreadyRunning, result.Status);

            Assert.Equal(0, processLauncher.StartCount);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable("NO_COLOR", originalNoColor);

            global::System.Environment.SetEnvironmentVariable(ArcanumServeLauncher.NoAutoServeEnvVar, originalNoAuto);

        }

    }

    [Fact]
    public async Task ListenAny_without_ack_does_not_spawn()
    {

        string? originalAck = global::System.Environment.GetEnvironmentVariable(
            ListenAnySecurityPolicy.AcknowledgementEnvironmentVariable);

        string? originalHostAny = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

        string marker = Path.Combine(ArcanumPaths.GrimoireDirectory, ".listen-any-acknowledged");

        string? relocated = null;

        try
        {

            global::System.Environment.SetEnvironmentVariable(ListenAnySecurityPolicy.AcknowledgementEnvironmentVariable, null);

            global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", null);

            if (File.Exists(marker))
            {

                relocated = marker + ".test-bak";

                File.Move(marker, relocated, overwrite: true);

            }

            SequencedHandler handler = new(_ => throw ConnectionRefused());

            FakeServeProcessLauncher processLauncher = new();

            ArcanumSettings settings = new()
            {
                Host = new HostSettings { ListenAny = true, Https = new HttpsSettings { Enabled = true } },
            };

            ArcanumServeLauncher launcher = CreateLauncher(
                handler,
                processLauncher,
                apiKey: "test-key",
                settings: settings);

            ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

            Assert.Equal(ServeLaunchStatus.Failed, result.Status);

            Assert.Equal(HealthProbeState.ConnectionRefused, result.Health);

            Assert.Equal(0, processLauncher.StartCount);

            Assert.Contains("ListenAny", result.Guidance ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(
                ListenAnySecurityPolicy.AcknowledgementEnvironmentVariable,
                originalAck);

            global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", originalHostAny);

            if (relocated is not null && File.Exists(relocated))
            {

                File.Move(relocated, marker, overwrite: true);

            }

        }

    }

    [Fact]
    public async Task Tls_failure_does_not_spawn()
    {

        SequencedHandler handler = new(_ =>
            throw new HttpRequestException(
                "The SSL connection could not be established.",
                new AuthenticationException("cert invalid")));

        FakeServeProcessLauncher processLauncher = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            handler,
            processLauncher,
            apiKey: "test-key");

        ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.Failed, result.Status);

        Assert.Equal(HealthProbeState.TlsFailure, result.Health);

        Assert.Equal(0, processLauncher.StartCount);

    }

    [Fact]
    public async Task Timeout_does_not_spawn()
    {

        SequencedHandler handler = new(async (_, ct) =>
        {

            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK);

        });

        FakeServeProcessLauncher processLauncher = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            handler,
            processLauncher,
            apiKey: "test-key");

        ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.Failed, result.Status);

        Assert.Equal(HealthProbeState.Timeout, result.Health);

        Assert.Equal(0, processLauncher.StartCount);

    }

    [Fact]
    public async Task Connection_refused_spawns()
    {

        SequencedHandler handler = new(
            _ => throw ConnectionRefused(),
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        FakeServeProcessLauncher processLauncher = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            handler,
            processLauncher,
            apiKey: "test-key");

        ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.Started, result.Status);

        Assert.Equal(1, processLauncher.StartCount);

    }

    [Fact]
    public async Task Live_pid_but_health_fails_does_not_delete_pid()
    {

        // Unauthorized means something is already answering — spawn must not run.
        // The launcher never deletes arcanum.pid (no processLauncher delete API); assert spawn not called.
        SequencedHandler handler = new(HttpStatusCode.Unauthorized);

        FakeServeProcessLauncher processLauncher = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            handler,
            processLauncher,
            apiKey: "test-key");

        ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.AuthFailed, result.Status);

        Assert.Equal(0, processLauncher.StartCount);

    }

    [Fact]
    public async Task Returns_Failed_on_timeout()
    {

        TimeSpan? previousDeadline = ArcanumServeLauncher.TestPollDeadline;

        try
        {

            ArcanumServeLauncher.TestPollDeadline = TimeSpan.FromMilliseconds(600);

            SequencedHandler handler = new(_ => throw ConnectionRefused());

            FakeServeProcessLauncher processLauncher = new();

            ArcanumServeLauncher launcher = CreateLauncher(
                handler,
                processLauncher,
                apiKey: "test-key");

            ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

            Assert.Equal(ServeLaunchStatus.Failed, result.Status);

            Assert.Equal(HealthProbeState.Timeout, result.Health);

            Assert.Equal(1, processLauncher.StartCount);

        }
        finally
        {

            ArcanumServeLauncher.TestPollDeadline = previousDeadline;

        }

    }

    [Fact]
    public async Task Does_not_spawn_when_server_unhealthy_503()
    {

        SequencedHandler handler = new(HttpStatusCode.ServiceUnavailable);

        FakeServeProcessLauncher processLauncher = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            handler,
            processLauncher,
            apiKey: "test-key");

        ServeLaunchResult result = await launcher.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.Failed, result.Status);

        Assert.Equal(HealthProbeState.UnhealthyStatus, result.Health);

        Assert.Equal(0, processLauncher.StartCount);

    }

    private static ArcanumServeLauncher CreateLauncher(
        HttpMessageHandler handler,
        FakeServeProcessLauncher processLauncher,
        string? apiKey,
        bool interactive = true,
        ArcanumSettings? settings = null) =>
        CreateLauncher(
            handler,
            processLauncher,
            new FakeSecretStore { ApiKey = apiKey },
            interactive,
            settings);

    private static ArcanumServeLauncher CreateLauncher(
        HttpMessageHandler handler,
        FakeServeProcessLauncher processLauncher,
        FakeSecretStore secretStore,
        bool interactive = true,
        ArcanumSettings? settings = null)
    {

        FakeHttpClientFactory factory = new(handler);

        TestOptionsMonitor<ArcanumSettings> monitor = new(settings ?? new ArcanumSettings());

        FakeCliEnvironment env = new(interactive);

        return new ArcanumServeLauncher(
            factory,
            monitor,
            secretStore,
            env,
            processLauncher,
            NullLogger<ArcanumServeLauncher>.Instance);

    }

    private static HttpRequestException ConnectionRefused() =>
        new(
            "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused));

    private sealed class FakeCliEnvironment : ICliEnvironment
    {

        public FakeCliEnvironment(bool interactive)
        {

            IsInteractive = interactive;

            ColorEnabled = interactive;

            ShouldShowManaBar = interactive;

        }

        public bool IsInteractive { get; }

        public bool ColorEnabled { get; }

        public bool ShouldShowManaBar { get; }

    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public string? ApiKey { get; set; }

        public Task<string?> GetApiKeyAsync() => Task.FromResult(ApiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(
                string.IsNullOrWhiteSpace(ApiKey)
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(ApiKey!));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class FakeServeProcessLauncher : IServeProcessLauncher
    {

        public int StartCount { get; private set; }

        public ServeProcessStartOptions? LastOptions { get; private set; }

        public Task<StartedProcess> StartServeAsync(
            ServeProcessStartOptions options,
            CancellationToken cancellationToken)
        {

            StartCount++;

            LastOptions = options;

            return Task.FromResult(new StartedProcess(42_001));

        }

    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
                Timeout = TimeSpan.FromSeconds(60),
            };

    }

    private sealed class SequencedHandler : HttpMessageHandler
    {

        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] _steps;

        private int _index;

        public SequencedHandler(params HttpStatusCode[] statuses)
            : this(statuses.Select(status =>
                    (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((_, _) =>
                        Task.FromResult(new HttpResponseMessage(status))))
                .ToArray())
        {
        }

        public SequencedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
            : this(steps.Select(step =>
                    (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((req, _) =>
                        Task.FromResult(step(req))))
                .ToArray())
        {
        }

        public SequencedHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps)
        {

            _steps = steps.Length == 0
                ? throw new ArgumentException("At least one step is required.", nameof(steps))
                : steps;

        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            CallCount++;

            int index = Math.Min(_index, _steps.Length - 1);

            if (_index < _steps.Length)
            {
                _index++;
            }

            return _steps[index](request, cancellationToken);

        }

    }

}
