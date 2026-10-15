using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Security;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>The durable-operation states, in the order the ledger numbers them.</summary>
internal enum LongRestOperationState
{
    Pending = 0,

    Running = 1,

    Waiting = 2,

    Cancelling = 3,

    Completed = 4,

    Failed = 5,

    Abandoned = 6,

    ReconciliationRequired = 7,
}

/// <summary>The content-free operation status the API returns.</summary>
internal sealed record LongRestOperationSnapshot(
    Guid Id,
    LongRestOperationState State,
    string? TerminalErrorCode,
    int AttemptCount,
    string PublicSummary,
    long Revision,
    DateTimeOffset? CompletedAt)
{
    public bool IsTerminal => State is LongRestOperationState.Completed or LongRestOperationState.Failed or LongRestOperationState.Abandoned;

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{State} code={TerminalErrorCode ?? "-"} attempt={AttemptCount} revision={Revision}");
}

/// <summary>A Saga memory as the listing returns it.</summary>
internal sealed record LongRestApiMemory(
    string Id,
    string Content,
    Guid? SessionId,
    DateTimeOffset CreatedAt,
    bool Retired,
    bool Pinned,
    string? ConsolidatedIntoMemoryId,
    string? LongRestReceiptId);

/// <summary>One component of the readiness report, with its status by name.</summary>
internal sealed record LongRestApiHealthComponent(string Name, string Status, string? Detail);

/// <summary>The readiness report <c>GET /api/health</c> returns, its statuses spelled by name whether the wire carries numbers or names.</summary>
internal sealed record LongRestApiHealth(string Status, IReadOnlyList<LongRestApiHealthComponent> Components);

/// <summary>A Long Rest receipt as <c>GET /api/memory/saga/long-rest/receipts/{id}</c> returns it, with the raw body kept to prove it is content-free.</summary>
internal sealed record LongRestApiReceipt(string ReceiptId, IReadOnlyList<string> TargetMemoryIds, string? SurvivorMemoryId, string RawJson);

/// <summary>The authenticated HTTP surface of the isolated host that the qualification drives.</summary>
internal sealed class LongRestQualificationApi : IDisposable
{
    private readonly HttpClient _client;

    public LongRestQualificationApi(int port, string masterApiKey)
    {
        _client = new HttpClient(LocalOllamaQualificationGuards.CreateLoopbackOnlyHttpHandler())
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(90),
        };

        _client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, masterApiKey);
    }

    public void Dispose() => _client.Dispose();

    public async Task<JsonElement> GetMetaAsync(CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Get, "api/meta", null, cancellationToken)).Data;

    public async Task<int> SagaCountAsync(CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Get, "api/saga/stats", null, cancellationToken)).Data.GetProperty("totalCount").GetInt32();

    public async Task<IReadOnlyList<LongRestApiMemory>> ListMemoriesAsync(CancellationToken cancellationToken)
    {
        JsonElement data = (await SendAsync(HttpMethod.Get, "api/saga?limit=1000", null, cancellationToken)).Data;

        return [.. data.EnumerateArray().Select(ReadMemory)];
    }

    public async Task<string> GetContentHashAsync(string id, CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Get, $"api/memory/saga/{id}", null, cancellationToken)).Data
            .GetProperty("contentHash").GetString()
        ?? throw new InvalidOperationException("The memory detail carried no content hash.");

    public async Task<JsonElement> GetMemoryDetailAsync(string id, CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Get, $"api/memory/saga/{id}", null, cancellationToken)).Data;

    public async Task CorrectAsync(string id, string content, CancellationToken cancellationToken)
    {
        string hash = await GetContentHashAsync(id, cancellationToken);

        ApiResult result = await SendAsync(
            HttpMethod.Post,
            $"api/memory/saga/{id}/correct",
            Json(("expectedContentHash", hash), ("content", content)),
            cancellationToken);

        RequireApplied(result, "correct");
    }

    public async Task RetireAsync(string id, CancellationToken cancellationToken)
    {
        string hash = await GetContentHashAsync(id, cancellationToken);

        ApiResult result = await SendAsync(
            HttpMethod.Post,
            $"api/memory/saga/{id}/retire",
            Json(("expectedContentHash", hash)),
            cancellationToken);

        RequireApplied(result, "retire");
    }

    public async Task PinAsync(string id, CancellationToken cancellationToken)
    {
        ApiResult result = await SendAsync(HttpMethod.Post, $"api/memory/saga/{id}/pin", null, cancellationToken);

        RequireApplied(result, "pin");
    }

    public async Task UnpinAsync(string id, CancellationToken cancellationToken)
    {
        ApiResult result = await SendAsync(HttpMethod.Post, $"api/memory/saga/{id}/unpin", null, cancellationToken);

        RequireApplied(result, "unpin");
    }

    public async Task DeleteMemoryAsync(string id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.DeleteAsync($"api/saga/{id}", cancellationToken);

        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException(
                $"Deleting a surplus seeding memory returned {(int)response.StatusCode}.");
        }
    }

    public async Task<LongRestOperationSnapshot> RequestGlobalSweepAsync(CancellationToken cancellationToken)
    {
        ApiResult result = await SendAsync(
            HttpMethod.Post,
            "api/memory/saga/long-rest/sweeps",
            "{\"scope\":\"Global\",\"campaignId\":null}",
            cancellationToken);

        return result.IsSuccess
            ? ReadOperation(result.Data)
            : throw new InvalidOperationException($"The sweep request returned {(int)result.Status}: {result.ErrorCode}.");
    }

    public async Task<ApiResult> TryRequestGlobalSweepAsync(CancellationToken cancellationToken) =>
        await SendAsync(
            HttpMethod.Post,
            "api/memory/saga/long-rest/sweeps",
            "{\"scope\":\"Global\",\"campaignId\":null}",
            cancellationToken);

    public async Task<LongRestOperationSnapshot> GetOperationAsync(Guid id, CancellationToken cancellationToken) =>
        ReadOperation((await SendAsync(HttpMethod.Get, $"api/operations/{id:D}", null, cancellationToken)).Data);

    public async Task<IReadOnlyList<LongRestOperationSnapshot>> ListSweepOperationsAsync(CancellationToken cancellationToken)
    {
        JsonElement data = (await SendAsync(HttpMethod.Get, "api/operations?kind=long-rest-sweep&limit=100", null, cancellationToken)).Data;

        JsonElement items = data.ValueKind == JsonValueKind.Array ? data : data.GetProperty("items");

        return [.. items.EnumerateArray().Select(ReadOperation)];
    }

    /// <summary>The operator's remedy for a sweep parked on a needs-operator code (<c>arcanum operation retry</c>).</summary>
    public async Task RetryOperationAsync(Guid id, CancellationToken cancellationToken)
    {
        ApiResult result = await SendAsync(HttpMethod.Post, $"api/operations/{id:D}/retry", null, cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Retrying the operation returned {(int)result.Status}: {result.ErrorCode}.");
        }
    }

    public async Task CancelOperationAsync(Guid id, CancellationToken cancellationToken)
    {
        ApiResult result = await SendAsync(HttpMethod.Post, $"api/operations/{id:D}/cancel", null, cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Cancelling the operation returned {(int)result.Status}: {result.ErrorCode}.");
        }
    }

    /// <summary>The readiness report. A valid Unhealthy report is a success envelope answered with 503, and is returned like any other.</summary>
    public async Task<LongRestApiHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        ApiResult result = await SendAsync(HttpMethod.Get, "api/health", null, cancellationToken);

        return result.IsSuccessBody
            ? ReadHealth(result.Data)
            : throw new InvalidOperationException($"The health report returned {(int)result.Status}: {result.ErrorCode}.");
    }

    /// <summary>One immutable, content-free Long Rest receipt by its identity.</summary>
    public async Task<LongRestApiReceipt> GetReceiptAsync(string receiptId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);

        ApiResult result = await SendAsync(HttpMethod.Get, $"api/memory/saga/long-rest/receipts/{Uri.EscapeDataString(receiptId)}", null, cancellationToken);

        return result.IsSuccess
            ? ReadReceipt(result.Data)
            : throw new InvalidOperationException($"The receipt request returned {(int)result.Status}: {result.ErrorCode}.");
    }

    /// <summary>Today's local spend in USD from the budget snapshot.</summary>
    public async Task<decimal> LocalSpendUsdAsync(CancellationToken cancellationToken)
    {
        JsonElement data = (await SendAsync(HttpMethod.Get, "api/budget", null, cancellationToken)).Data;

        return data.GetProperty("localSpendUsd").GetDecimal();
    }

    public async Task<IReadOnlyDictionary<string, double>> ReadMetricsAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync("metrics", cancellationToken);

        string text = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The metrics scrape returned {(int)response.StatusCode}.");
        }

        return ParseMetrics(text);
    }

    /// <summary>The Prometheus text format reduced to <c>name{labels}</c> to value, which is all the qualification compares.</summary>
    internal static IReadOnlyDictionary<string, double> ParseMetrics(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Dictionary<string, double> metrics = new(StringComparer.Ordinal);

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int split = line.LastIndexOf(' ');

            if (split <= 0 || !double.TryParse(line[(split + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                continue;
            }

            metrics[line[..split]] = value;
        }

        return metrics;
    }

    internal static LongRestApiHealth ReadHealth(JsonElement element) =>
        new(
            ReadHealthStatus(element.GetProperty("status")),
            [
                .. element.GetProperty("components").EnumerateArray().Select(static component => new LongRestApiHealthComponent(
                    component.GetProperty("name").GetString()!,
                    ReadHealthStatus(component.GetProperty("status")),
                    ReadOptionalString(component, "detail"))),
            ]);

    private static string ReadHealthStatus(JsonElement status) =>
        status.ValueKind == JsonValueKind.Number
            ? ((HealthStatus)status.GetInt32()).ToString()
            : Enum.Parse<HealthStatus>(status.GetString()!, ignoreCase: true).ToString();

    internal static LongRestApiReceipt ReadReceipt(JsonElement element) =>
        new(
            element.GetProperty("receiptId").GetString()!,
            [.. element.GetProperty("targets").EnumerateArray().Select(static target => target.GetProperty("memoryId").GetString()!)],
            ReadOptionalString(element, "survivorMemoryId"),
            element.GetRawText());

    private static LongRestApiMemory ReadMemory(JsonElement element) =>
        new(
            element.GetProperty("id").GetString()!,
            element.GetProperty("content").GetString()!,
            element.TryGetProperty("sessionId", out JsonElement session) && session.ValueKind == JsonValueKind.String ? session.GetGuid() : null,
            element.GetProperty("createdAt").GetDateTimeOffset(),
            element.TryGetProperty("retiredAtUtc", out JsonElement retired) && retired.ValueKind != JsonValueKind.Null,
            element.TryGetProperty("pinnedAtUtc", out JsonElement pinned) && pinned.ValueKind != JsonValueKind.Null,
            ReadOptionalString(element, "consolidatedIntoMemoryId"),
            ReadOptionalString(element, "longRestReceiptId"));

    private static string? ReadOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static LongRestOperationSnapshot ReadOperation(JsonElement element)
    {
        JsonElement state = element.GetProperty("state");

        LongRestOperationState parsed = state.ValueKind == JsonValueKind.Number
            ? (LongRestOperationState)state.GetInt32()
            : Enum.Parse<LongRestOperationState>(state.GetString()!, ignoreCase: true);

        return new LongRestOperationSnapshot(
            element.GetProperty("id").GetGuid(),
            parsed,
            ReadOptionalString(element, "terminalErrorCode"),
            element.GetProperty("attemptCount").GetInt32(),
            ReadOptionalString(element, "publicSummary") ?? string.Empty,
            element.GetProperty("revision").GetInt64(),
            element.TryGetProperty("completedAt", out JsonElement completed) && completed.ValueKind == JsonValueKind.String
                ? completed.GetDateTimeOffset()
                : null);
    }

    private static void RequireApplied(ApiResult result, string verb)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Saga {verb} returned {(int)result.Status}: {result.ErrorCode}.");
        }

        JsonElement outcome = result.Data.GetProperty("outcome");

        bool applied = outcome.ValueKind == JsonValueKind.Number
            ? outcome.GetInt32() == 1
            : string.Equals(outcome.GetString(), "Applied", StringComparison.OrdinalIgnoreCase);

        if (!applied)
        {
            throw new InvalidOperationException($"Saga {verb} was not applied (outcome {outcome}).");
        }
    }

    private static string Json(params (string Name, string Value)[] properties)
    {
        using MemoryStream stream = new();

        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();

            foreach ((string name, string value) in properties)
            {
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<ApiResult> SendAsync(HttpMethod method, string path, string? json, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, path);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        JsonDocument? document = null;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return new ApiResult(response.StatusCode, default, $"non-JSON body ({body.Length} bytes)");
        }

        using (document)
        {
            JsonElement root = document.RootElement.Clone();

            bool success = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("isSuccess", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;

            string? code = null;

            if (!success && root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out JsonElement errorCode))
            {
                code = errorCode.GetString();
            }

            JsonElement data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out JsonElement payload)
                ? payload
                : default;

            return new ApiResult(response.StatusCode, data, code) { IsSuccessBody = success };
        }
    }

    internal sealed record ApiResult(HttpStatusCode Status, JsonElement Data, string? ErrorCode)
    {
        public bool IsSuccessBody { get; init; }

        public bool IsSuccess => (int)Status is >= 200 and < 300 && IsSuccessBody;
    }
}
