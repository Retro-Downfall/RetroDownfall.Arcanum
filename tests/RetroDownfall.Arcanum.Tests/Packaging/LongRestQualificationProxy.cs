using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>What a request the host sent to its provider endpoint was for.</summary>
internal enum LongRestProviderRequestKind
{
    Other = 0,

    Models = 1,

    Embedding = 2,

    /// <summary>A Long Rest hierarchy summary: a chat call with the summary schema.</summary>
    Summary = 3,

    /// <summary>A Long Rest adjudication: a chat call with the decision schema.</summary>
    Adjudication = 4,

    /// <summary>Any other chat call: an ordinary Session turn or the host's own memory extraction.</summary>
    Chat = 5,
}

/// <summary>Which provider entry's credential a request carried (never the credential itself).</summary>
internal enum LongRestRequestCredential
{
    None = 0,

    Chat = 1,

    Embedding = 2,

    Unknown = 3,
}

/// <summary>One request the host sent through the forwarder, with what became of it.</summary>
internal sealed class LongRestRecordedRequest
{
    public int Sequence { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public string Method { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public LongRestProviderRequestKind Kind { get; init; }

    public LongRestRequestCredential Credential { get; init; }

    public string? Model { get; init; }

    public bool Streaming { get; init; }

    /// <summary>The decoded message and input text the provider received, so a search is not defeated by JSON escaping.</summary>
    public string ProviderVisibleText { get; init; } = string.Empty;

    public int BodyBytes { get; init; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int? StatusCode { get; set; }

    /// <summary>The response reached the host: the whole body was written to the connection without the host going away first.</summary>
    public bool Delivered { get; set; }

    /// <summary>The host closed the connection before the response completed (it was killed, or it gave up).</summary>
    public bool ClientAborted { get; set; }

    public long? PromptTokens { get; set; }

    public long? CompletionTokens { get; set; }

    /// <summary>The part of <see cref="PromptTokens"/> the provider served from its prompt cache, which the host's pricing bills at the cached rate.</summary>
    public long? CachedPromptTokens { get; set; }

    /// <summary>The first choice's finish reason of a non-streamed chat response (<c>stop</c>, or <c>length</c> when the output cap cut it off).</summary>
    public string? FinishReason { get; set; }

    /// <summary>The first choice's message text of a non-streamed Long Rest chat response, bounded, so the answer's validity can be graded.</summary>
    public string? ResponseContent { get; set; }

    public string? Error { get; set; }

    public bool IsLongRestChat => Kind is LongRestProviderRequestKind.Summary or LongRestProviderRequestKind.Adjudication;
}

/// <summary>
/// A loopback forwarder in front of Ollama that the isolated host's provider entries point at. It exists to observe
/// what the host sends: which model id served each call, whether a retired or pinned memory ever left the process,
/// when a request is in flight (so the host can be killed with an attempt <c>Sent</c>), and what usage the provider
/// reported, which the host's own accounting is then compared with. It forwards every request unchanged to the one
/// literal-loopback endpoint the operator named, follows no redirect and uses no proxy.
/// </summary>
internal sealed class LongRestQualificationProxy : IAsyncDisposable
{
    private const int MaximumBodyBytes = 16 * 1024 * 1024;

    private const int MaximumResponseCaptureBytes = 8 * 1024 * 1024;

    private readonly WebApplication _application;

    private readonly HttpClient _upstream;

    private readonly Uri _upstreamEndpoint;

    private readonly object _sync = new();

    private readonly List<LongRestRecordedRequest> _requests = [];

    private int _sequence;

    private Func<LongRestRecordedRequest, bool>? _arrivalHook;

    private LongRestQualificationProxy(WebApplication application, HttpClient upstream, Uri upstreamEndpoint, Uri endpoint)
    {
        _application = application;

        _upstream = upstream;

        _upstreamEndpoint = upstreamEndpoint;

        Endpoint = endpoint;
    }

    /// <summary>The <c>/v1/</c> endpoint the host's provider entries are configured with.</summary>
    public Uri Endpoint { get; }

    public static async Task<LongRestQualificationProxy> StartAsync(Uri upstreamEndpoint)
    {
        ArgumentNullException.ThrowIfNull(upstreamEndpoint);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = "Testing",
            });

        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(
            static options =>
            {
                options.Listen(IPAddress.Loopback, 0);

                options.Limits.MaxRequestBodySize = MaximumBodyBytes;

                options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);

                options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(10);
            });

        WebApplication application = builder.Build();

        HttpClient upstream = new(LocalOllamaQualificationGuards.CreateLoopbackOnlyHttpHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        LongRestQualificationProxy? proxy = null;

        application.Run(context => proxy!.ForwardAsync(context));

        await application.StartAsync();

        IServer server = application.Services.GetRequiredService<IServer>();

        IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("The forwarder did not expose a listen address.");

        Uri listening = new(addresses.Addresses.Single());

        proxy = new LongRestQualificationProxy(
            application,
            upstream,
            upstreamEndpoint,
            new Uri($"http://127.0.0.1:{listening.Port}/v1/"));

        return proxy;
    }

    /// <summary>A position in the request log, so a phase can read only the requests made since it began.</summary>
    public int Position
    {
        get
        {
            lock (_sync)
            {
                return _requests.Count;
            }
        }
    }

    public IReadOnlyList<LongRestRecordedRequest> Since(int position)
    {
        lock (_sync)
        {
            return [.. _requests.Skip(position)];
        }
    }

    public IReadOnlyList<LongRestRecordedRequest> All => Since(0);

    /// <summary>
    /// Runs <paramref name="hook"/> once for each request after the forwarder has read and classified it and before it is
    /// forwarded; returning <see langword="true"/> disarms it. The hook runs on the request's own thread, so a hook that
    /// kills the host does so while that request is in flight.
    /// </summary>
    public void SetArrivalHook(Func<LongRestRecordedRequest, bool>? hook)
    {
        lock (_sync)
        {
            _arrivalHook = hook;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();

        await _application.DisposeAsync();

        _upstream.Dispose();
    }

    /// <summary>
    /// Classifies one provider request from its path and JSON body. Long Rest's chat calls carry a named structured-output
    /// schema, which is how a summary and an adjudication are told apart from the host's other chat calls.
    /// </summary>
    internal static LongRestProviderRequestKind Classify(string path, JsonElement? body)
    {
        ArgumentNullException.ThrowIfNull(path);

        string trimmed = path.TrimEnd('/');

        if (trimmed.EndsWith("/models", StringComparison.Ordinal))
        {
            return LongRestProviderRequestKind.Models;
        }

        if (trimmed.EndsWith("/embeddings", StringComparison.Ordinal))
        {
            return LongRestProviderRequestKind.Embedding;
        }

        if (!trimmed.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return LongRestProviderRequestKind.Other;
        }

        string? schema = SchemaName(body);

        return schema switch
        {
            LongRestPromptLayout.SummarySchemaName => LongRestProviderRequestKind.Summary,
            LongRestPromptLayout.AdjudicationSchemaName => LongRestProviderRequestKind.Adjudication,
            _ => LongRestProviderRequestKind.Chat,
        };
    }

    /// <summary>Every string the provider is shown by a chat or embedding request, decoded.</summary>
    internal static string ProviderVisibleText(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root)
        {
            return string.Empty;
        }

        StringBuilder text = new();

        if (root.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement message in messages.EnumerateArray())
            {
                if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out JsonElement content))
                {
                    AppendText(text, content);
                }
            }
        }

        if (root.TryGetProperty("input", out JsonElement input))
        {
            AppendText(text, input);
        }

        return text.ToString();
    }

    private static void AppendText(StringBuilder text, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                _ = text.Append(value.GetString()).Append('\n');

                break;

            case JsonValueKind.Array:
                foreach (JsonElement part in value.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out JsonElement partText))
                    {
                        AppendText(text, partText);
                    }
                    else
                    {
                        AppendText(text, part);
                    }
                }

                break;
        }
    }

    private static string? SchemaName(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("response_format", out JsonElement format)
            || format.ValueKind != JsonValueKind.Object
            || !format.TryGetProperty("json_schema", out JsonElement schema)
            || schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("name", out JsonElement name)
            || name.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return name.GetString();
    }

    private async Task ForwardAsync(HttpContext context)
    {
        byte[] body = await ReadBodyAsync(context);

        JsonElement? json = null;

        JsonDocument? document = null;

        try
        {
            if (body.Length > 0 && context.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                document = JsonDocument.Parse(body);

                json = document.RootElement;
            }
        }
        catch (JsonException)
        {
            json = null;
        }

        string path = context.Request.Path.Value ?? string.Empty;

        LongRestRecordedRequest record = new()
        {
            Sequence = Interlocked.Increment(ref _sequence),
            StartedAt = DateTimeOffset.UtcNow,
            Method = context.Request.Method,
            Path = path,
            Kind = Classify(path, json),
            Credential = ClassifyCredential(context.Request.Headers.Authorization.ToString()),
            Model = json is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("model", out JsonElement model)
                && model.ValueKind == JsonValueKind.String ? model.GetString() : null,
            Streaming = json is { ValueKind: JsonValueKind.Object } streamRoot && streamRoot.TryGetProperty("stream", out JsonElement stream)
                && stream.ValueKind == JsonValueKind.True,
            ProviderVisibleText = ProviderVisibleText(json),
            BodyBytes = body.Length,
        };

        document?.Dispose();

        lock (_sync)
        {
            _requests.Add(record);
        }

        Func<LongRestRecordedRequest, bool>? hook;

        lock (_sync)
        {
            hook = _arrivalHook;
        }

        if (hook is not null && hook(record))
        {
            lock (_sync)
            {
                if (ReferenceEquals(_arrivalHook, hook))
                {
                    _arrivalHook = null;
                }
            }
        }

        try
        {
            await ForwardUpstreamAsync(context, record, body);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            record.ClientAborted = true;
        }
        catch (Exception exception)
        {
            record.Error = exception.GetType().Name;

            record.ClientAborted = context.RequestAborted.IsCancellationRequested;

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
        finally
        {
            record.CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task ForwardUpstreamAsync(HttpContext context, LongRestRecordedRequest record, byte[] body)
    {
        string relative = (context.Request.Path.Value ?? string.Empty).TrimStart('/');

        // The configured endpoint ends in /v1/, and the host's requests are made relative to it, so the forwarded path is the
        // upstream endpoint's own /v1/ plus whatever follows it.
        string afterVersion = relative.StartsWith("v1/", StringComparison.Ordinal) ? relative[3..] : relative;

        Uri target = new(_upstreamEndpoint, afterVersion + context.Request.QueryString.Value);

        using HttpRequestMessage request = new(new HttpMethod(context.Request.Method), target);

        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);

            if (context.Request.ContentType is { } contentType)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in context.Request.Headers)
        {
            if (IsHopByHop(header.Key) || header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _ = request.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value);
        }

        using HttpResponseMessage response = await _upstream.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted);

        record.StatusCode = (int)response.StatusCode;

        context.Response.StatusCode = (int)response.StatusCode;

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(response.Content.Headers))
        {
            if (IsHopByHop(header.Key) || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        await using Stream upstream = await response.Content.ReadAsStreamAsync(context.RequestAborted);

        using MemoryStream capture = new();

        byte[] buffer = new byte[16 * 1024];

        while (true)
        {
            int read = await upstream.ReadAsync(buffer, context.RequestAborted);

            if (read == 0)
            {
                break;
            }

            if (capture.Length + read <= MaximumResponseCaptureBytes)
            {
                capture.Write(buffer, 0, read);
            }

            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);

            await context.Response.Body.FlushAsync(context.RequestAborted);
        }

        await context.Response.CompleteAsync();

        record.Delivered = true;

        byte[] captured = capture.ToArray();

        (record.PromptTokens, record.CompletionTokens) = ReadUsage(captured, record.Streaming);

        record.CachedPromptTokens = ReadCachedTokens(captured, record.Streaming);

        record.FinishReason = record.Streaming ? null : ReadFinishReason(captured);

        record.ResponseContent = record.Streaming || !record.IsLongRestChat ? null : ReadMessageContent(captured);
    }

    /// <summary>The first choice's message text of a whole chat response, bounded to 16,384 characters, or <see langword="null"/>.</summary>
    internal static string? ReadMessageContent(byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);

        try
        {
            using JsonDocument document = JsonDocument.Parse(response);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.String)
            {
                string text = content.GetString() ?? string.Empty;

                return text.Length <= 16_384 ? text : text[..16_384];
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The first choice's <c>finish_reason</c> of a whole chat response, or <see langword="null"/> when there is none.</summary>
    internal static string? ReadFinishReason(byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);

        try
        {
            using JsonDocument document = JsonDocument.Parse(response);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("finish_reason", out JsonElement reason)
                && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The provider's own usage figures from a whole JSON response or the last usage-bearing chunk of an event stream.</summary>
    internal static (long? Prompt, long? Completion) ReadUsage(byte[] response, bool streaming)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Length == 0)
        {
            return (null, null);
        }

        string text = Encoding.UTF8.GetString(response);

        if (!streaming)
        {
            return ReadUsageObject(text);
        }

        (long? Prompt, long? Completion) found = (null, null);

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();

            if (!trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string payload = trimmed[5..].Trim();

            if (payload.Length == 0 || payload == "[DONE]")
            {
                continue;
            }

            (long? Prompt, long? Completion) usage = ReadUsageObject(payload);

            if (usage.Prompt is not null || usage.Completion is not null)
            {
                found = usage;
            }
        }

        return found;
    }

    /// <summary>The provider's <c>prompt_tokens_details.cached_tokens</c> from a whole response or the last usage-bearing chunk of a stream.</summary>
    internal static long? ReadCachedTokens(byte[] response, bool streaming)
    {
        ArgumentNullException.ThrowIfNull(response);

        string text = Encoding.UTF8.GetString(response);

        long? found = null;

        foreach (string candidate in streaming
            ? text.Split('\n').Select(static line => line.Trim()).Where(static line => line.StartsWith("data:", StringComparison.Ordinal)).Select(static line => line[5..].Trim())
            : [text])
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(candidate);

                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("usage", out JsonElement usage)
                    && usage.ValueKind == JsonValueKind.Object
                    && usage.TryGetProperty("prompt_tokens_details", out JsonElement details)
                    && details.ValueKind == JsonValueKind.Object)
                {
                    found = ReadLong(details, "cached_tokens") ?? found;
                }
            }
            catch (JsonException)
            {
                // A line that is not JSON ([DONE], a blank, a comment) carries no usage.
            }
        }

        return found;
    }

    private static (long? Prompt, long? Completion) ReadUsageObject(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("usage", out JsonElement usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (ReadLong(usage, "prompt_tokens"), ReadLong(usage, "completion_tokens"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static long? ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : null;

    private static LongRestRequestCredential ClassifyCredential(string authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return LongRestRequestCredential.None;
        }

        string token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : authorization.Trim();

        return string.Equals(token, LongRestQualificationConfiguration.ChatCredentialPlaceholder, StringComparison.Ordinal)
            ? LongRestRequestCredential.Chat
            : string.Equals(token, LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder, StringComparison.Ordinal)
                ? LongRestRequestCredential.Embedding
                : LongRestRequestCredential.Unknown;
    }

    private static bool IsHopByHop(string header) =>
        header.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
        || header.Equals("TE", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Trailer", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        using MemoryStream body = new();

        await context.Request.Body.CopyToAsync(body, context.RequestAborted);

        return body.ToArray();
    }

    /// <summary>Total tokens a request reported, for the accounting comparison.</summary>
    internal static string Describe(LongRestRecordedRequest request) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"#{request.Sequence} {request.Kind} model={request.Model ?? "-"} status={request.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "-"} delivered={request.Delivered} aborted={request.ClientAborted}");
}
