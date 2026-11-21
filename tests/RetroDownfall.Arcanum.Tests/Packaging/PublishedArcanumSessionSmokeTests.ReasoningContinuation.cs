using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Tower;
using Xunit;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed partial class PublishedArcanumSessionSmokeTests
{
    private const string ReasoningPrompt = "published-reasoning-continuation";

    private const string ReasoningSentinel = "synthetic-published-reasoning-sentinel";

    private const string ReasoningFinal = "published-reasoning-final";

    private const string ReasoningVisible = "published-tool-round-visible";

    [SkippableFact]
    public async Task Published_ollama_tool_continuation_replays_reasoning_without_persisting_it()
    {
        string executable = global::System.Environment.GetEnvironmentVariable(PublishedExecutableVariable) ?? string.Empty;

        Skip.If(string.IsNullOrWhiteSpace(executable), $"Set {PublishedExecutableVariable} to an Arcanum publish apphost to run this gate.");

        Assert.True(File.Exists(executable));

        AssertNativeAotPublish(executable);

        PublishedTestProcessIsolation isolation = PublishedTestProcessIsolation.Create("published-reasoning-continuation");

        DiagnosticsProcess? process = null;

        ProcessOutputCapture? output = null;

        List<(bool Streaming, string? Reasoning, string? Visible)> observedReasoning = [];

        try
        {
            isolation.Prepare();

            string configurationDirectory = Path.Combine(isolation.TestHome, ".config", "arcanum");

            Directory.CreateDirectory(configurationDirectory);

            await using FakeOpenAiProvider provider = await FakeOpenAiProvider.StartAsync();

            using TcpListener reservation = new(IPAddress.Loopback, 0);

            reservation.Start();

            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;

            string configurationPath = Path.Combine(configurationDirectory, "arcanum.json");

            await WriteConfigurationAsync(configurationPath, isolation.TestHome, port, provider.Endpoint);

            ArcanumConfigurationFile configuration = JsonSerializer.Deserialize(await File.ReadAllTextAsync(configurationPath),
                ConfigurationJsonContext.Default.ArcanumConfigurationFile)!;

            configuration.Arcanum.Features.Reasoning = true;

            await File.WriteAllTextAsync(configurationPath, JsonSerializer.Serialize(configuration, ConfigurationJsonContext.Default.ArcanumConfigurationFile));

            ProcessStartInfo start = CreateHostStartInfo(executable, isolation);

            reservation.Stop();

            process = DiagnosticsProcess.Start(start) ?? throw new InvalidOperationException("Published host did not start.");

            output = new ProcessOutputCapture(process, ProviderKey);

            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

            string masterKey = await output.WaitForReadyAsync(deadline.Token);

            using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(45) };

            client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, masterKey);

            foreach (bool streaming in new[] { false, true })
            {
                using HttpResponseMessage created = await client.PostAsync("api/sessions",
                    JsonContent.Create(new CreateSessionRequest(null, "published reasoning"), ArcanumJsonContext.Default.CreateSessionRequest), deadline.Token);

                Assert.Equal(HttpStatusCode.Created, created.StatusCode);

                Guid sessionId = (await ReadDataAsync(created, ArcanumJsonContext.Default.ApiResponseSessionDetailDto, deadline.Token)).Id;

                string prompt = ReasoningPrompt + (streaming ? "-stream" : "-buffered");

                PingRequest ping = new(prompt, Model: ExpectedModel, SessionId: sessionId,
                    DisableMcpTools: true, SkipSpellRouting: true, MaxOutputTokens: 32,
                    Reasoning: new ReasoningRequestOptions(Output: ReasoningOutputMode.None));

                using HttpResponseMessage response = await client.PostAsync(streaming ? "api/intelligence/ping-stream" : "api/intelligence/ping",
                    JsonContent.Create(ping, ArcanumJsonContext.Default.PingRequest), deadline.Token);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                string body = await response.Content.ReadAsStringAsync(deadline.Token);

                Assert.Contains(ReasoningFinal, body, StringComparison.Ordinal);

                Assert.DoesNotContain(ReasoningSentinel, body, StringComparison.Ordinal);

                using HttpResponseMessage entriesResponse = await client.GetAsync($"api/sessions/{sessionId:D}/entries?limit=20", deadline.Token);

                EntryDto[] entries = await ReadDataAsync(entriesResponse, ArcanumJsonContext.Default.ApiResponseEntryDtoArray, deadline.Token);

                Assert.DoesNotContain(entries, entry => entry.Content.Contains(ReasoningSentinel, StringComparison.Ordinal));

                ProviderRequest[] calls = provider.Requests.Where(call => call.Prompt == prompt).ToArray();

                Assert.Equal(2, calls.Length);

                JsonElement[] messages = calls[1].Payload.GetProperty("messages").EnumerateArray().ToArray();

                JsonElement assistant = Assert.Single(messages, message => message.GetProperty("role").GetString() == "assistant"
                    && message.TryGetProperty("tool_calls", out _));

                observedReasoning.Add((streaming, assistant.TryGetProperty("reasoning", out JsonElement reasoning) ? reasoning.GetString() : null,
                    assistant.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null));

                JsonElement toolCall = Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray());

                Assert.Equal("call-published-reasoning", toolCall.GetProperty("id").GetString());

                Assert.Equal("get_local_system_time", toolCall.GetProperty("function").GetProperty("name").GetString());

                Assert.Equal("{}", toolCall.GetProperty("function").GetProperty("arguments").GetString());

                JsonElement toolResult = Assert.Single(messages, message => message.GetProperty("role").GetString() == "tool");

                Assert.Equal("call-published-reasoning", toolResult.GetProperty("tool_call_id").GetString());

                Assert.False(string.IsNullOrEmpty(toolResult.GetProperty("content").GetString()));
            }

            using HttpResponseMessage quit = await client.PostAsync("api/server/quit", null, deadline.Token);

            Assert.Equal(HttpStatusCode.Accepted, quit.StatusCode);

            await process.WaitForExitAsync(deadline.Token);

            await output.WaitForCompletionAsync(deadline.Token);

            Assert.Equal(0, process.ExitCode);

            Assert.DoesNotContain(ReasoningSentinel, output.SafeDiagnostics(masterKey), StringComparison.Ordinal);
        }
        finally
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (output is not null) await output.DisposeAsync();

            process?.Dispose();

            isolation.Delete();
        }

        string? receiptPath = global::System.Environment.GetEnvironmentVariable("ARCANUM_PUBLISHED_REASONING_RECEIPT");

        bool mismatch = observedReasoning.Any(observation => observation.Reasoning != ReasoningSentinel
            || observation.Visible != ReasoningVisible);

        if (mismatch && !string.IsNullOrWhiteSpace(receiptPath))
        {
            await File.WriteAllTextAsync(receiptPath,
                "published-reasoning-continuation:wire-mismatch:v1" + global::System.Environment.NewLine);
        }

        Assert.All(observedReasoning, observation => Assert.True(observation.Reasoning == ReasoningSentinel,
            $"Ollama continuation omitted provider reasoning (streaming={observation.Streaming})."));

        Assert.All(observedReasoning, observation => Assert.True(observation.Visible == ReasoningVisible,
            $"Ollama continuation omitted visible assistant text (streaming={observation.Streaming})."));

        if (!string.IsNullOrWhiteSpace(receiptPath))
        {
            await File.WriteAllTextAsync(receiptPath, "published-reasoning-continuation:v1" + global::System.Environment.NewLine);
        }
    }

    internal sealed partial class FakeOpenAiProvider
    {
        private readonly ConcurrentDictionary<string, int> _reasoningPhases = new(StringComparer.Ordinal);

        private async Task<bool> TryWriteReasoningContinuationAsync(HttpContext context, string model, string authorization,
            string prompt, bool streaming, JsonElement payload)
        {
            if (!prompt.StartsWith(ReasoningPrompt + "-", StringComparison.Ordinal)) return false;

            int phase = _reasoningPhases.AddOrUpdate(prompt, 0, static (_, previous) => previous + 1);

            _requests.Enqueue(new ProviderRequest(model, authorization, prompt, streaming, payload.Clone()));

            if (phase > 1)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                return true;
            }

            string message = phase == 0
                ? "\"role\":\"assistant\",\"reasoning\":\"" + ReasoningSentinel + "\",\"content\":\"" + ReasoningVisible + "\",\"tool_calls\":[{"
                    + (streaming ? "\"index\":0," : "")
                    + "\"id\":\"call-published-reasoning\",\"type\":\"function\",\"function\":{\"name\":\"get_local_system_time\",\"arguments\":\"{}\"}}]"
                : "\"role\":\"assistant\",\"content\":\"" + ReasoningFinal + "\"";

            string body = "{\"id\":\"chatcmpl-reasoning\",\"object\":\"" + (streaming ? "chat.completion.chunk" : "chat.completion")
                + "\",\"created\":0,\"model\":\"published-smoke-model\",\"choices\":[{\"index\":0,\"" + (streaming ? "delta" : "message")
                + "\":{" + message + "},\"finish_reason\":\"" + (phase == 0 ? "tool_calls" : "stop")
                + "\"}],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":4,\"total_tokens\":24}}";

            context.Response.ContentType = streaming ? "text/event-stream" : "application/json";

            await context.Response.WriteAsync(streaming ? "data: " + body + "\n\ndata: [DONE]\n\n" : body, context.RequestAborted);

            return true;
        }
    }
}
