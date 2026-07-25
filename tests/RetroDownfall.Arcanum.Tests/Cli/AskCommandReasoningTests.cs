using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class AskCommandReasoningTests
{
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
        ServiceCollection services = new();
        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());
        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();
        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(new NdjsonHandler(ndjson)));
        services.AddSingleton<ISecretStore>(new FakeSecretStore());
        services.AddSingleton<IEyeOfTheWorld, FakeEye>();
        services.AddSingleton<IGrimoireCliInitialization, NoopGrimoireInitialization>();
        services.AddSingleton<IChronosyncEngine, NoopChronosyncEngine>();
        services.AddSingleton<IArcanumServeLauncher, NoopServeLauncher>();

        CliTestResult result = await CliTestHarness.RunAsync(services, ["ask", "question"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("final answer", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(reasoningText, result.Output, StringComparison.Ordinal);
        Assert.Contains("Reasoning", result.Error, StringComparison.Ordinal);
        Assert.Contains(reasoningText, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_coalesces_fragmented_reasoning_escapes_markup_and_keeps_answer_clean()
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
        ServiceCollection services = new();
        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());
        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();
        services.AddSingleton<IHttpClientFactory>(
            new FakeHttpClientFactory(new NdjsonHandler(SerializeFrames(frames))));
        services.AddSingleton<ISecretStore>(new FakeSecretStore());
        services.AddSingleton<IEyeOfTheWorld, FakeEye>();
        services.AddSingleton<IGrimoireCliInitialization, NoopGrimoireInitialization>();
        services.AddSingleton<IChronosyncEngine, NoopChronosyncEngine>();
        services.AddSingleton<IArcanumServeLauncher, NoopServeLauncher>();

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["chat"],
            input: "question" + System.Environment.NewLine + "/exit" + System.Environment.NewLine);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("final answer", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(escapedReasoning, result.Output, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result.Error, "Reasoning (ephemeral)"));
        Assert.Contains("literal", result.Error, StringComparison.Ordinal);
        Assert.Contains("reasoning truncated", result.Error, StringComparison.Ordinal);
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

    private sealed class NdjsonHandler(string ndjson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
            });
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

    private sealed class NoopGrimoireInitialization : IGrimoireCliInitialization
    {
        public Task EnsureInitializedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
