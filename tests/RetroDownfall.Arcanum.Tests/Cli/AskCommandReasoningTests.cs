using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class AskCommandReasoningTests
{
    [Theory]
    [InlineData((byte)ArcanumClientMutationDisposition.Blocked)]
    [InlineData((byte)ArcanumClientMutationDisposition.Unsafe)]
    public async Task New_session_refusal_fails_before_the_remote_turn_and_retains_the_prior_binding(
        byte dispositionValue)
    {

        Guid priorSessionId = Guid.Parse(
            "17171717-1717-1717-1717-171717171717");

        RecordingContextStore contextStore = new(priorSessionId);

        RecordingArcanumClientMutationBoundary boundary = new(
            (ArcanumClientMutationDisposition)dispositionValue);

        NdjsonHandler handler = new(
            SerializeFrames(
                new IntelligenceEvent(
                    IntelligenceEventType.Token,
                    string.Empty,
                    "must not run"),
                new IntelligenceEvent(
                    IntelligenceEventType.Result,
                    "must not run",
                    "must not run")));

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddSingleton<IHttpClientFactory>(
            new FakeHttpClientFactory(handler));

        services.AddSingleton<ISecretStore>(new FakeSecretStore());

        services.AddSingleton<IEyeOfTheWorld, FakeEye>();

        services.AddSingleton<IGrimoireCliInitialization, NoopGrimoireInitialization>();

        services.AddSingleton<IArcanumServeLauncher, NoopServeLauncher>();

        services.RemoveAll<ICliContextStore>();

        services.AddSingleton<ICliContextStore>(contextStore);

        services.RemoveAll<IArcanumClientMutationBoundary>();

        services.AddSingleton<IArcanumClientMutationBoundary>(boundary);

        services.RemoveAll<ICliInferenceContextResolver>();

        services.AddSingleton<ICliInferenceContextResolver>(
            new FixedContextResolver());

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["run", "--new", "question"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Contains(
            dispositionValue == (byte)ArcanumClientMutationDisposition.Blocked
                ? "maintenance"
                : "safely",
            result.Error,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(priorSessionId, contextStore.Load().SessionId);

        Assert.Equal(0, contextStore.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);

        Assert.DoesNotContain(
            "/api/perception/chronosync",
            handler.RequestPaths);

        Assert.DoesNotContain(
            "/api/intelligence/ping-stream",
            handler.RequestPaths);

    }

    [Theory]
    [InlineData((byte)ArcanumClientMutationDisposition.Blocked)]
    [InlineData((byte)ArcanumClientMutationDisposition.Unsafe)]
    public async Task Session_bound_refusal_warns_but_keeps_the_successful_remote_turn(
        byte dispositionValue)
    {

        Guid priorSessionId = Guid.Parse(
            "18181818-1818-1818-1818-181818181818");

        Guid remoteSessionId = Guid.Parse(
            "19191919-1919-1919-1919-191919191919");

        NdjsonHandler handler = new(
            SerializeFrames(
                new IntelligenceEvent(
                    IntelligenceEventType.SessionBound,
                    "Session bound",
                    remoteSessionId.ToString("D")),
                new IntelligenceEvent(
                    IntelligenceEventType.Token,
                    string.Empty,
                    "successful answer"),
                new IntelligenceEvent(
                    IntelligenceEventType.Result,
                    "successful answer",
                    "successful answer")));

        RecordingContextStore contextStore = new(priorSessionId);

        RecordingArcanumClientMutationBoundary boundary = new(
            (ArcanumClientMutationDisposition)dispositionValue);

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddSingleton<IHttpClientFactory>(
            new FakeHttpClientFactory(handler));

        services.AddSingleton<ISecretStore>(new FakeSecretStore());

        services.AddSingleton<IEyeOfTheWorld, FakeEye>();

        services.AddSingleton<IGrimoireCliInitialization, NoopGrimoireInitialization>();

        services.AddSingleton<IArcanumServeLauncher, NoopServeLauncher>();

        services.RemoveAll<ICliContextStore>();

        services.AddSingleton<ICliContextStore>(contextStore);

        services.RemoveAll<IArcanumClientMutationBoundary>();

        services.AddSingleton<IArcanumClientMutationBoundary>(boundary);

        services.RemoveAll<ICliInferenceContextResolver>();

        services.AddSingleton<ICliInferenceContextResolver>(
            new FixedContextResolver());

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["run", "question"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("successful answer", result.Output, StringComparison.Ordinal);

        Assert.Equal(priorSessionId, contextStore.Load().SessionId);

        Assert.Equal(0, contextStore.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);

        Assert.Contains(
            dispositionValue == (byte)ArcanumClientMutationDisposition.Blocked
                ? "maintenance"
                : "safely",
            result.Error,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "/api/intelligence/ping-stream",
            handler.RequestPaths);

    }

    [Fact]
    public async Task Session_bound_does_not_repopulate_context_when_missing_at_client_admission()
    {

        Guid priorSessionId = Guid.Parse(
            "20212223-2425-2627-2829-303132333435");

        Guid remoteSessionId = Guid.Parse(
            "30313233-3435-3637-3839-404142434445");

        NdjsonHandler handler = new(
            SerializeFrames(
                new IntelligenceEvent(
                    IntelligenceEventType.SessionBound,
                    "Session bound",
                    remoteSessionId.ToString("D")),
                new IntelligenceEvent(
                    IntelligenceEventType.Token,
                    string.Empty,
                    "successful answer"),
                new IntelligenceEvent(
                    IntelligenceEventType.Result,
                    "successful answer",
                    "successful answer")),
            boundSessionId: remoteSessionId);

        RecordingContextStore contextStore = new(priorSessionId);

        RecordingArcanumClientMutationBoundary boundary = new()
        {
            BeforeMutation = () => handler.BoundSessionAvailable = false,
        };

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddSingleton<IHttpClientFactory>(
            new FakeHttpClientFactory(handler));

        services.AddSingleton<ISecretStore>(new FakeSecretStore());

        services.AddSingleton<IEyeOfTheWorld, FakeEye>();

        services.AddSingleton<IGrimoireCliInitialization, NoopGrimoireInitialization>();

        services.AddSingleton<IArcanumServeLauncher, NoopServeLauncher>();

        services.RemoveAll<ICliContextStore>();

        services.AddSingleton<ICliContextStore>(contextStore);

        services.RemoveAll<IArcanumClientMutationBoundary>();

        services.AddSingleton<IArcanumClientMutationBoundary>(boundary);

        services.RemoveAll<ICliInferenceContextResolver>();

        services.AddSingleton<ICliInferenceContextResolver>(
            new FixedContextResolver());

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["run", "question"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("successful answer", result.Output, StringComparison.Ordinal);

        Assert.Equal(priorSessionId, contextStore.Load().SessionId);

        Assert.Equal(0, contextStore.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);

        Assert.Contains(
            $"/api/sessions/{remoteSessionId:D}",
            handler.RequestPaths);

    }

    [Fact]
    public async Task Ask_renders_reasoning_to_ephemeral_stderr_block_and_stdout_stays_answer_only()
    {
        const string reasoningText = "client-safe summary";
        string ndjson = SerializeFrames(
            new IntelligenceEvent(
                IntelligenceEventType.Reasoning,
                reasoningText,
                Reasoning: new ReasoningContentSegment(
                    reasoningText,
                    ReasoningOutputMode.Summary)),
            new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "final answer"),
            new IntelligenceEvent(IntelligenceEventType.Result, "final answer", "final answer"));
        NdjsonHandler handler = new(ndjson);
        ServiceCollection services = new();
        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());
        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();
        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
        services.AddSingleton<ISecretStore>(new FakeSecretStore());
        services.AddSingleton<IEyeOfTheWorld, FakeEye>();
        services.AddSingleton<IGrimoireCliInitialization, NoopGrimoireInitialization>();
        services.AddSingleton<IChronosyncEngine, NoopChronosyncEngine>();
        services.AddSingleton<IArcanumServeLauncher, NoopServeLauncher>();

        CliTestResult result = await CliTestHarness.RunAsync(services, ["run", "question"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("final answer", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(reasoningText, result.Output, StringComparison.Ordinal);
        Assert.Contains("Reasoning", result.Error, StringComparison.Ordinal);
        Assert.Contains(reasoningText, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_coalesces_fragmented_reasoning_escapes_markup_and_keeps_answer_clean()
    {
        const string escapedReasoning = "[red]literal[/]";
        IntelligenceEvent[] frames =
        [
            new IntelligenceEvent(
                IntelligenceEventType.Reasoning,
                escapedReasoning,
                Reasoning: new ReasoningContentSegment(escapedReasoning, ReasoningOutputMode.Summary)),
            .. Enumerable.Range(0, 7_000).Select(static _ =>
                new IntelligenceEvent(
                    IntelligenceEventType.Reasoning,
                    "abcdefghij",
                    Reasoning: new ReasoningContentSegment("abcdefghij", ReasoningOutputMode.Summary))),
            new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "final answer"),
            new IntelligenceEvent(IntelligenceEventType.Result, "final answer", "final answer"),
        ];
        NdjsonHandler handler = new(SerializeFrames(frames));
        ServiceCollection services = new();
        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());
        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();
        services.AddSingleton<IHttpClientFactory>(
            new FakeHttpClientFactory(handler));
        services.AddSingleton<ISecretStore>(new FakeSecretStore());
        services.AddSingleton<IEyeOfTheWorld, FakeEye>();
        services.AddSingleton<IGrimoireCliInitialization, NoopGrimoireInitialization>();
        services.AddSingleton<IChronosyncEngine, NoopChronosyncEngine>();
        services.AddSingleton<IArcanumServeLauncher, NoopServeLauncher>();

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["run", "question"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("final answer", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(escapedReasoning, result.Output, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result.Error, "Reasoning (ephemeral)"));
        Assert.Contains("literal", result.Error, StringComparison.Ordinal);
        Assert.Contains("reasoning truncated", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_fails_closed_before_inference_when_host_chronosync_fails()
    {

        Error failure = new(
            "Chronosync.Unavailable",
            "The host could not synchronize the workspace pattern.");

        NdjsonHandler handler = new(string.Empty, failure);

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.AddSingleton<ISecretStore>(new FakeSecretStore());

        services.AddSingleton<IEyeOfTheWorld, FakeEye>();

        services.AddSingleton<IGrimoireCliInitialization, NoopGrimoireInitialization>();

        services.AddSingleton<IChronosyncEngine, NoopChronosyncEngine>();

        services.AddSingleton<IArcanumServeLauncher, NoopServeLauncher>();

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["run", "question"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Contains(failure.Message, result.Error, StringComparison.Ordinal);

        Assert.Contains("/api/perception/chronosync", handler.RequestPaths);

        Assert.DoesNotContain("/api/intelligence/ping-stream", handler.RequestPaths);

    }

    private static string SerializeFrames(params IntelligenceEvent[] frames) =>
        string.Join(
            '\n',
            frames.Select(static frame =>
                JsonSerializer.Serialize(frame, ArcanumJsonContext.Default.IntelligenceEvent)))
        + "\n";

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
    }

    private sealed class NdjsonHandler(
        string ndjson,
        Error? chronosyncError = null,
        Guid? boundSessionId = null) : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        public bool BoundSessionAvailable { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            string path = request.RequestUri?.AbsolutePath ?? string.Empty;

            RequestPaths.Add(path);

            if (string.Equals(
                    path,
                    "/api/perception/chronosync",
                    StringComparison.Ordinal))
            {

                Result<ChronosyncReport> result = chronosyncError is null
                    ? Result<ChronosyncReport>.Success(
                        new ChronosyncReport(null, [], [], false))
                    : Result<ChronosyncReport>.Failure(chronosyncError.Value);

                string json = JsonSerializer.Serialize(
                    ApiResponse<ChronosyncReport>.FromResult(result),
                    ArcanumJsonContext.Default.ApiResponseChronosyncReport);

                return Task.FromResult(new HttpResponseMessage(
                    chronosyncError is null
                        ? HttpStatusCode.OK
                        : HttpStatusCode.ServiceUnavailable)
                {

                    Content = new StringContent(json, Encoding.UTF8, "application/json"),

                });

            }

            if (boundSessionId is { } sessionId
                && string.Equals(
                    path,
                    $"/api/sessions/{sessionId:D}",
                    StringComparison.Ordinal))
            {

                Result<SessionDetailDto> result = BoundSessionAvailable
                    ? Result<SessionDetailDto>.Success(
                        new SessionDetailDto(
                            sessionId,
                            null,
                            "Remote",
                            "Active",
                            0,
                            DateTimeOffset.UnixEpoch,
                            DateTimeOffset.UnixEpoch,
                            null,
                            0))
                    : Result<SessionDetailDto>.Failure(
                        new Error(
                            ErrorCodes.Session.NotFound,
                            "Session was not found in the replacement host."));

                string json = JsonSerializer.Serialize(
                    ApiResponse<SessionDetailDto>.FromResult(result),
                    ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

                return Task.FromResult(new HttpResponseMessage(
                    BoundSessionAvailable
                        ? HttpStatusCode.OK
                        : HttpStatusCode.NotFound)
                {

                    Content = new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"),

                });

            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),

            });

        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class FakeEye : IEyeOfTheWorld
    {
        public Task<PatternSnapshot> PerceivePatternAsync(
            string directoryPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PatternSnapshot(DomainType.Unknown, directoryPath, []));
    }

    private sealed class FixedContextResolver : ICliInferenceContextResolver
    {

        public Task<CliInferenceContextResult> ResolveAsync(
            CliInferenceContextRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                CliInferenceContextResult.Success(
                    CliContextPrecedence.Resolve(
                        new CliContextResolutionRequest(
                            null,
                            null,
                            null,
                            null,
                            CliContextDocument.Empty,
                            null,
                            null,
                            null,
                            null,
                            NoContext: false)),
                    []));

    }

    private sealed class RecordingContextStore(
        Guid sessionId) :
        ICliContextStore,
        ICliContextExclusiveWriter
    {

        private CliContextDocument _document =
            CliContextDocument.Empty with { SessionId = sessionId };

        public int ExclusiveSaves { get; private set; }

        public string FilePath =>
            Path.Combine(
                Path.GetTempPath(),
                "arcanum-recording-cli-context.json");

        public CliContextDocument Load() => _document;

        public void SaveUnderExclusive(CliContextDocument document)
        {

            ExclusiveSaves++;

            _document = document;

        }

    }

    private sealed class NoopGrimoireInitialization :
        IGrimoireCliInitialization,
        IServiceProvider
    {
        public Task<T> RunExclusiveAsync<T>(
            Func<IServiceProvider, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) => operation(this, cancellationToken);

        public Task<T> RunExclusiveWithBootstrapAsync<T>(
            Func<IServiceProvider, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) => operation(this, cancellationToken);

        public object? GetService(Type serviceType) => null;
    }

    private sealed class NoopChronosyncEngine : IChronosyncEngine
    {
        public Task<ChronosyncReport> AnalyzeAndSyncAsync(
            PatternSnapshot currentSnapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChronosyncReport(null, [], [], false));
    }

    private sealed class NoopServeLauncher : IArcanumServeLauncher
    {
        public Task<ServeLaunchResult> EnsureRunningAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ServeLaunchResult(
                ServeLaunchStatus.AlreadyRunning,
                HealthProbeState.Healthy,
                TimeSpan.Zero,
                null,
                null));
    }
}
