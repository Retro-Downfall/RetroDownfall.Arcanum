using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// DelegatingHandler for llama.cpp server endpoints. Performs two opt-in augmentations on
/// <c>application/json</c> (non-streaming) request bodies:
/// <list type="bullet">
/// <item>Structured output: when <c>Arcanum:StructuredOutput:UseProviderConstrainedDecoding</c> is enabled
/// and the request carries <c>response_format: json_schema</c>, the JSON Schema is converted to a GBNF
/// grammar and injected as the <c>grammar</c> parameter.</item>
/// <item>Prompt caching: when <c>Arcanum:Cache:Enabled</c> is enabled and the estimated prompt token
/// count meets <c>MinCacheableTokens</c>, <c>cache_prompt: true</c> is injected. Token estimation uses
/// <see cref="InferenceTokenizerResolver"/> when available and cheap; otherwise a 4-chars-per-token heuristic.</item>
/// </list>
/// Streaming (<c>text/event-stream</c>) requests pass through unchanged.
/// </summary>
public sealed class LlamaCppRequestAugmentingHandler : DelegatingHandler
{

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    private readonly ILogger<LlamaCppRequestAugmentingHandler> _logger;

    private readonly InferenceTokenizerResolver? _tokenizerResolver;

    public LlamaCppRequestAugmentingHandler(
        IOptionsMonitor<ArcanumSettings> settings,
        ILogger<LlamaCppRequestAugmentingHandler> logger,
        InferenceTokenizerResolver? tokenizerResolver = null)
    {

        _settings = settings;

        _logger = logger;

        _tokenizerResolver = tokenizerResolver;

    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {

        if (!IsJsonRequest(request))
        {

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        }

        ArcanumSettings current = _settings.CurrentValue;

        StructuredOutputSettings structuredOutput = current.StructuredOutput;

        CacheSettings cache = current.Cache;

        bool structuredOutputActive = structuredOutput.Enabled && structuredOutput.UseProviderConstrainedDecoding;

        bool cacheActive = cache.Enabled;

        if (!structuredOutputActive && !cacheActive)
        {

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        }

        HttpContent? originalContent = request.Content;

        if (originalContent is null)
        {

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        }

        byte[] bodyBytes = await originalContent.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (bodyBytes.Length == 0)
        {

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        }

        JsonDocument? document = TryParseJson(bodyBytes);

        if (document is null)
        {

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        }

        try
        {

            string? grammar = null;

            if (structuredOutputActive
                && TryGetJsonSchema(document, out JsonElement schemaElement))
            {

                int schemaMaxDepth = ArcanumSettingClamps.JsonSchemaMaxDepth(structuredOutput.SchemaMaxDepth);

                using JsonDocument schemaDocument = JsonDocument.Parse(schemaElement.GetRawText());

                Result<JsonSchemaDefinition> parseResult = JsonSchemaHelper.Parse(schemaDocument, schemaMaxDepth);

                if (parseResult.IsSuccess)
                {

                    Result<string> grammarResult = JsonSchemaHelper.ToGbnf(parseResult.Value, schemaMaxDepth);

                    if (grammarResult.IsSuccess)
                    {

                        grammar = grammarResult.Value;

                    }
                    else
                    {

                        _logger.LogWarning(
                            "Could not convert JSON Schema to GBNF grammar: {Error}. Falling back to provider unconstrained decoding.",
                            grammarResult.Error.Message);

                    }

                }
                else
                {

                    _logger.LogWarning(
                        "Could not parse JSON Schema for GBNF grammar generation: {Error}. Falling back to provider unconstrained decoding.",
                        parseResult.Error.Message);

                }

            }

            bool injectCachePrompt = cacheActive && ShouldCachePrompt(document, cache);

            if (grammar is null && !injectCachePrompt)
            {

                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            }

            byte[] modifiedBody = AugmentBody(bodyBytes, grammar, injectCachePrompt);

            request.Content = new ByteArrayContent(modifiedBody)
            {

                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }

            };

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            document.Dispose();

        }

    }

    private bool ShouldCachePrompt(JsonDocument document, CacheSettings cache)
    {

        int minCacheable = ArcanumSettingClamps.CacheMinCacheableTokens(cache.MinCacheableTokens);

        string? promptText = ExtractPromptText(document);

        if (string.IsNullOrEmpty(promptText))
        {

            return false;

        }

        int estimatedTokens = EstimateTokenCount(promptText);

        return estimatedTokens >= minCacheable;

    }

    private int EstimateTokenCount(string promptText)
    {

        if (_tokenizerResolver is not null)
        {

            try
            {

                Tokenizer tokenizer = _tokenizerResolver.ResolveTokenizer(_settings.CurrentValue.Intelligence.TokenizerEncoding);

                return tokenizer.CountTokens(promptText);

            }
            catch (Exception ex)
            {

                _logger.LogDebug(ex, "Tokenizer-based prompt token estimation failed; falling back to 4-chars-per-token heuristic.");

            }

        }

        return Math.Max(1, promptText.Length / 4);

    }

    private static string? ExtractPromptText(JsonDocument document)
    {

        if (!document.RootElement.TryGetProperty("messages", out JsonElement messagesElement)
            || messagesElement.ValueKind != JsonValueKind.Array)
        {

            return null;

        }

        StringBuilder builder = new();

        foreach (JsonElement message in messagesElement.EnumerateArray())
        {

            if (message.ValueKind != JsonValueKind.Object)
            {

                continue;

            }

            if (message.TryGetProperty("content", out JsonElement contentElement))
            {

                if (contentElement.ValueKind == JsonValueKind.String)
                {

                    builder.Append(contentElement.GetString()).Append(' ');

                }
                else if (contentElement.ValueKind == JsonValueKind.Array)
                {

                    foreach (JsonElement part in contentElement.EnumerateArray())
                    {

                        if (part.ValueKind == JsonValueKind.Object
                            && part.TryGetProperty("text", out JsonElement textElement)
                            && textElement.ValueKind == JsonValueKind.String)
                        {

                            builder.Append(textElement.GetString()).Append(' ');

                        }

                    }

                }

            }

        }

        return builder.Length == 0 ? null : builder.ToString();

    }

    private static bool IsJsonRequest(HttpRequestMessage request)
    {

        if (request.Content?.Headers.ContentType?.MediaType is not "application/json")
        {

            return false;

        }

        string? accept = request.Headers.Accept.ToString();

        return !accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

    }

    private static JsonDocument? TryParseJson(byte[] bytes)
    {

        try
        {

            return JsonDocument.Parse(bytes);

        }
        catch (JsonException)
        {

            return null;

        }

    }

    private static bool TryGetJsonSchema(JsonDocument document, out JsonElement schemaElement)
    {

        schemaElement = default;

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {

            return false;

        }

        if (!document.RootElement.TryGetProperty("response_format", out JsonElement responseFormatElement)
            || responseFormatElement.ValueKind != JsonValueKind.Object)
        {

            return false;

        }

        if (!responseFormatElement.TryGetProperty("type", out JsonElement typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || !string.Equals(typeElement.GetString(), "json_schema", StringComparison.OrdinalIgnoreCase))
        {

            return false;

        }

        if (!responseFormatElement.TryGetProperty("json_schema", out JsonElement schemaWrapper)
            || schemaWrapper.ValueKind != JsonValueKind.Object)
        {

            return false;

        }

        if (schemaWrapper.TryGetProperty("schema", out JsonElement innerSchema)
            && innerSchema.ValueKind == JsonValueKind.Object)
        {

            schemaElement = innerSchema;

            return true;

        }

        schemaElement = schemaWrapper;

        return schemaElement.ValueKind == JsonValueKind.Object;

    }

    private static byte[] AugmentBody(ReadOnlySpan<byte> originalBody, string? grammar, bool injectCachePrompt)
    {

        using MemoryStream output = new();

        using Utf8JsonWriter writer = new(output);

        writer.WriteStartObject();

        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(originalBody));

        bool wroteGrammar = false;

        bool wroteCachePrompt = false;

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {

            if (grammar is not null && property.NameEquals("grammar"))
            {

                writer.WritePropertyName("grammar");

                writer.WriteStringValue(grammar);

                wroteGrammar = true;

                continue;

            }

            if (injectCachePrompt && property.NameEquals("cache_prompt"))
            {

                writer.WritePropertyName("cache_prompt");

                writer.WriteBooleanValue(true);

                wroteCachePrompt = true;

                continue;

            }

            property.WriteTo(writer);

        }

        if (grammar is not null && !wroteGrammar)
        {

            writer.WriteString("grammar", grammar);

        }

        if (injectCachePrompt && !wroteCachePrompt)
        {

            writer.WriteBoolean("cache_prompt", true);

        }

        writer.WriteEndObject();

        writer.Flush();

        return output.ToArray();

    }

}
