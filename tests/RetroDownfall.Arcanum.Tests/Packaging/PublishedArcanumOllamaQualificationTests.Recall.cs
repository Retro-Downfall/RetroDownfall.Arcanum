using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed partial class PublishedArcanumOllamaQualificationTests
{
    private const string RecallSubject = "local.recall.subject";

    private const string RecallTurnPrompt =
        "Call recall_memory with query local.recall.subject, scope Covenant, and limit 1. "
        + "Read the qualification token and the exact versionId from the returned item. "
        + "Reply only: RECALLED_TOKEN=<token>; VERSION=<versionId>";

    private static async Task RunNativeMemoryRecallAsync(HttpClient api, int hostPort, string masterApiKey,
        string model, string workspace, Guid sessionId)
    {
        using CancellationTokenSource deadline = new(TurnBudget);

        string token = CreateOpaqueToken();

        string content = $"Local recall qualification token: {token}.";

        var seeded = await new MemoryErasureRouteDriver(api).SetCovenantAsync(
            CovenantScope.Global, null, RecallSubject, content, deadline.Token);

        Assert.Equal(CovenantLane.Confirmed, seeded.Lane);

        Assert.NotNull(seeded.ResultingVersionId);

        Guid version = seeded.ResultingVersionId.Value;

        // The source's exact version is not rendered into the ordinary Covenant section. Copying it
        // in the final answer therefore requires consuming the tool result, even if the fact was injected.
        Assert.DoesNotContain(token, RecallTurnPrompt, StringComparison.Ordinal);

        Assert.DoesNotContain(version.ToString("D"), RecallTurnPrompt, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(version.ToString("D"), content, StringComparison.OrdinalIgnoreCase);

        JsonElement beforeMemory = await ReadRecallSourceAsync(api, deadline.Token);

        Assert.Equal(version, beforeMemory.GetProperty("confirmed").GetProperty("versionId").GetGuid());

        JsonElement[] beforeRows = await ReadRecallRowsAsync(api, sessionId, deadline.Token);

        using HttpClient client = new(LocalOllamaQualificationGuards.CreateLoopbackOnlyHttpHandler())
        {
            BaseAddress = new Uri($"http://127.0.0.1:{hostPort}/"),
            Timeout = global::System.Threading.Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, masterApiKey);

        PingRequest ping = new(RecallTurnPrompt, Model: model, WorkingDirectory: workspace, SessionId: sessionId,
            DisableMcpTools: true, UnattendedMode: false, SkipSpellRouting: true, Temperature: 0,
            MaxOutputTokens: 1024, ToolPolicy: ToolPolicy.ReadOnlyTools);

        using HttpRequestMessage request = new(HttpMethod.Post, "api/intelligence/ping-stream")
        {
            Content = JsonContent.Create(ping, ArcanumJsonContext.Default.PingRequest),
        };

        using HttpResponseMessage response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, deadline.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

        AssertRecallProtectedHeaders(response);

        await using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token);

        using StreamReader reader = new(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);

        var evidence = await LocalOllamaRecallQualificationGuards.ReadAsync(reader, sessionId, RecallSubject, token, version, deadline.Token);

        Assert.NotNull(evidence);

        JsonElement[] afterRows = await ReadRecallRowsAsync(api, sessionId, deadline.Token);

        Assert.True(LocalOllamaRecallQualificationGuards.MatchesPersistedTurn(beforeRows, afterRows, sessionId, RecallTurnPrompt, evidence),
            "The recall turn must preserve all previous entries and append only its actual user/reply pair and exact observed tool call/result bytes.");

        JsonElement afterMemory = await ReadRecallSourceAsync(api, deadline.Token);

        Assert.True(JsonElement.DeepEquals(beforeMemory, afterMemory),
            "The operator-confirmed recall source must retain its exact head, version, curation, and provenance after the read-only turn.");
    }

    private static async Task<JsonElement> ReadRecallSourceAsync(HttpClient api, CancellationToken cancellationToken)
    {
        using var response = await api.PostAsync("api/memory/covenant/detail",
            JsonContent.Create(new CovenantDetailRequest(CovenantScope.Global, null, RecallSubject),
                ArcanumJsonContext.Default.CovenantDetailRequest), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.True(document.RootElement.GetProperty("isSuccess").GetBoolean());

        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<JsonElement[]> ReadRecallRowsAsync(HttpClient api, Guid sessionId, CancellationToken cancellationToken)
    {
        using var response = await api.GetAsync($"api/sessions/{sessionId:D}/entries?limit=100", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.True(document.RootElement.GetProperty("isSuccess").GetBoolean());

        return document.RootElement.GetProperty("data").EnumerateArray().Reverse().Select(static row => row.Clone()).ToArray();
    }

    private static void AssertRecallProtectedHeaders(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);

        Assert.True(response.Headers.CacheControl?.Private);

        Assert.Equal("no-cache", response.Headers.Pragma.ToString());

        Assert.Equal("0", Assert.Single(response.Content.Headers.GetValues("Expires")));

        Assert.Null(response.Headers.ETag);

        Assert.Null(response.Content.Headers.LastModified);
    }
}
