using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Tower;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed partial class PublishedArcanumSessionSmokeTests
{
    private const string PartialOutputLimitPrompt = "published-partial-output-limit";

    private const string PartialOutputProgress = "I will begin the implementation and then run the tests.\n";

    private static async Task ExercisePartialOutputLimitAsync(HttpClient client, FakeOpenAiProvider provider,
        int priorCalls, CancellationToken ct)
    {
        List<InferenceNotice[]> terminalNotices = [];

        foreach (bool streaming in new[] { false, true })
        {
            using HttpResponseMessage created = await client.PostAsync("api/sessions",
                JsonContent.Create(new CreateSessionRequest(null, "published partial output"), ArcanumJsonContext.Default.CreateSessionRequest), ct);

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            Guid sessionId = (await ReadDataAsync(created, ArcanumJsonContext.Default.ApiResponseSessionDetailDto, ct)).Id;

            PingRequest ping = new(PartialOutputLimitPrompt, Model: ExpectedModel, SessionId: sessionId,
                DisableMcpTools: true, SkipSpellRouting: true, MaxOutputTokens: 32);

            using HttpResponseMessage response = await client.PostAsync(streaming ? "api/intelligence/ping-stream" : "api/intelligence/ping",
                JsonContent.Create(ping, ArcanumJsonContext.Default.PingRequest), ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string text;

            ChatCompletionUsage? usage;

            string? finishReason;

            if (streaming)
            {
                Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

                string ndjson = await response.Content.ReadAsStringAsync(ct);

                IntelligenceEvent[] frames = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => JsonSerializer.Deserialize(line, ArcanumJsonContext.Default.IntelligenceEvent)!).ToArray();

                Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);

                IntelligenceEvent terminal = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Result);

                text = string.Concat(frames.Where(static frame => frame.Type == IntelligenceEventType.Token).Select(static frame => frame.Data));

                Assert.Equal(text, terminal.Message);

                usage = terminal.Usage;

                finishReason = terminal.FinishReason;

                terminalNotices.Add(terminal.Notices.ToArray());

                // Keep both physical lanes observable before the final old-binary RED assertion.
                Assert.Equal(terminal.Notices.Count(static notice => (int)notice.Code == 4),
                    frames.Count(static frame => frame.Type == IntelligenceEventType.Status && frame.Notices.Any(notice => (int)notice.Code == 4)));
            }
            else
            {
                PromptResponseDto result = await ReadDataAsync(response, ArcanumJsonContext.Default.ApiResponsePromptResponseDto, ct);

                text = result.Text;

                usage = result.Usage;

                finishReason = result.FinishReason;

                terminalNotices.Add(result.Notices.ToArray());
            }

            Assert.Equal(PartialOutputProgress, text);

            Assert.Equal("length", finishReason);

            Assert.NotNull(usage);

            Assert.Equal(40, usage.PromptTokens);

            Assert.Equal(8, usage.CompletionTokens);

            Assert.Equal(48, usage.TotalTokens);

            using HttpResponseMessage entriesResponse = await client.GetAsync($"api/sessions/{sessionId:D}/entries?limit=20", ct);

            EntryDto[] entries = await ReadDataAsync(entriesResponse, ArcanumJsonContext.Default.ApiResponseEntryDtoArray, ct);

            Assert.Single(entries, static entry => entry.ToolName == "get_local_system_time");

            Assert.Single(entries, static entry => entry.Content.StartsWith("[ToolResult:", StringComparison.Ordinal));

            Assert.Single(entries, static entry => entry.Content == PartialOutputProgress);
        }

        Assert.Equal(priorCalls + 4, provider.RequestCount);

        ProviderRequest[] calls = provider.Requests.Skip(priorCalls).ToArray();

        Assert.Equal(4, calls.Length);

        foreach (bool streaming in new[] { false, true })
        {
            ProviderRequest[] lane = calls.Where(call => call.Streaming == streaming).ToArray();

            Assert.Equal(2, lane.Length);

            JsonElement toolResult = Assert.Single(lane[1].Payload.GetProperty("messages").EnumerateArray(),
                static message => message.GetProperty("role").GetString() == "tool");

            Assert.Equal("call-published-delegation", toolResult.GetProperty("tool_call_id").GetString());
        }

        Assert.Equal(new[] { 4, 4 }, terminalNotices.Select(static notices => notices.Length == 1 ? (int)notices[0].Code : 0).ToArray());

        Assert.All(terminalNotices, static notices => Assert.Equal(new InferenceNotice(InferenceNoticeCode.OutputLimitWithPartialAnswer).Message, notices[0].Message));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Loopback_fixture_partial_output_limit_keeps_progress_then_empty_length_and_refuses_replay(bool streaming)
    {
        await using FakeOpenAiProvider provider = await FakeOpenAiProvider.StartAsync();

        using HttpClient client = new();

        string json = "{\"model\":\"published-smoke-model\",\"stream\":" + (streaming ? "true" : "false")
            + ",\"messages\":[{\"role\":\"user\",\"content\":\"" + PartialOutputLimitPrompt + "\"}]}";

        for (int phase = 0; phase < 3; phase++)
        {
            using HttpResponseMessage response = await client.PostAsync(new Uri(provider.Endpoint.ToString().TrimEnd('/') + "/chat/completions"),
                new StringContent(json, Encoding.UTF8, "application/json"));

            Assert.Equal(phase < 2 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, response.StatusCode);

            if (phase >= 2) continue;

            string body = await response.Content.ReadAsStringAsync();

            if (streaming) body = body.Split('\n')[0]["data: ".Length..];

            using JsonDocument document = JsonDocument.Parse(body);

            JsonElement choice = document.RootElement.GetProperty("choices")[0];

            Assert.Equal(phase == 0 ? "tool_calls" : "length", choice.GetProperty("finish_reason").GetString());

            Assert.Equal(phase == 0 ? PartialOutputProgress : string.Empty,
                choice.GetProperty(streaming ? "delta" : "message").GetProperty("content").GetString());
        }

        Assert.Equal(3, provider.RequestCount);
    }
}
