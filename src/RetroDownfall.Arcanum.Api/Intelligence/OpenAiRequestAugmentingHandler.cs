using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// DelegatingHandler for OpenAI-compatible providers. When structured-output constrained decoding is
/// enabled and the request uses <c>response_format: json_schema</c>, this handler injects
/// <c>strict: true</c> into the <c>json_schema</c> wrapper. If the provider rejects it with a 400
/// mentioning <c>strict</c>, the handler makes one retry without the flag.
/// Only <c>application/json</c> request bodies are parsed; streaming requests are passed through unchanged.
/// </summary>
public sealed class OpenAiRequestAugmentingHandler : DelegatingHandler
{

    private const int MaxResponseInspectionBytes = 64 * 1024;

    private readonly ILogger<OpenAiRequestAugmentingHandler> _logger;

    public OpenAiRequestAugmentingHandler(
        ILogger<OpenAiRequestAugmentingHandler> logger)
    {
        _logger = logger;

    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {

        if (!IsJsonRequest(request))
        {

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        }

        StructuredOutputSettings structuredOutput = ArcanumRuntimeDefaults.StructuredOutput;

        if (!structuredOutput.Enabled || !structuredOutput.UseProviderConstrainedDecoding)
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

            if (!TryGetJsonSchemaResponseFormat(document, out JsonElement jsonSchemaWrapper))
            {

                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            }

            byte[] modifiedBody = InjectStrictFlag(bodyBytes, jsonSchemaWrapper);

            originalContent.Dispose();

            request.Content = new ByteArrayContent(modifiedBody)
            {

                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }

            };

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest
                && await ResponseMentionsStrictAsync(response, cancellationToken).ConfigureAwait(false))
            {

                _logger.LogWarning(
                    "Provider rejected strict structured-output request; retrying once without strict: true.");

                MediaTypeHeaderValue? contentType = request.Content?.Headers.ContentType;

                request.Content?.Dispose();

                request.Content = new ByteArrayContent(bodyBytes);

                if (contentType is not null)
                {

                    request.Content.Headers.ContentType = contentType;

                }

                response.Dispose();

                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            }

            return response;

        }
        finally
        {

            document.Dispose();

        }

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

    private static bool TryGetJsonSchemaResponseFormat(JsonDocument document, out JsonElement jsonSchemaWrapper)
    {

        jsonSchemaWrapper = default;

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

        jsonSchemaWrapper = schemaWrapper;

        return true;

    }

    private static byte[] InjectStrictFlag(ReadOnlySpan<byte> originalBody, JsonElement jsonSchemaWrapper)
    {

        using MemoryStream output = new();

        using Utf8JsonWriter writer = new(output);

        writer.WriteStartObject();

        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(originalBody));

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {

            if (property.NameEquals("response_format"))
            {

                writer.WritePropertyName("response_format");

                writer.WriteStartObject();

                foreach (JsonProperty responseFormatProperty in property.Value.EnumerateObject())
                {

                    if (responseFormatProperty.NameEquals("json_schema"))
                    {

                        writer.WritePropertyName("json_schema");

                        writer.WriteStartObject();

                        writer.WriteBoolean("strict", true);

                        foreach (JsonProperty schemaProperty in responseFormatProperty.Value.EnumerateObject())
                        {

                            if (schemaProperty.NameEquals("strict"))
                            {

                                continue;

                            }

                            schemaProperty.WriteTo(writer);

                        }

                        writer.WriteEndObject();

                    }
                    else
                    {

                        responseFormatProperty.WriteTo(writer);

                    }

                }

                writer.WriteEndObject();

            }
            else
            {

                property.WriteTo(writer);

            }

        }

        writer.WriteEndObject();

        writer.Flush();

        return output.ToArray();

    }

    private static async Task<bool> ResponseMentionsStrictAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {

        try
        {

            // Not `await using`: the stream belongs to a response that is handed back to the caller
            // whenever the body turns out not to be about `strict`. Disposing it there destroys the
            // provider's real error — the only place the reason for the 400 lives.
            Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            byte[] prefix = new byte[MaxResponseInspectionBytes];

            int totalRead = 0;

            while (totalRead < prefix.Length)
            {

                int read = await stream
                    .ReadAsync(prefix.AsMemory(totalRead, prefix.Length - totalRead), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {

                    break;

                }

                totalRead += read;

            }

            // Put the consumed prefix back in front of whatever is left, so the caller reads the
            // whole body exactly as the provider sent it.
            HttpContent replayable = new StreamContent(
                new PrefixedStream(prefix.AsMemory(0, totalRead), stream));

            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            {

                _ = replayable.Headers.TryAddWithoutValidation(header.Key, header.Value);

            }

            response.Content = replayable;

            string body = Encoding.UTF8.GetString(prefix, 0, totalRead);

            return body.Contains("strict", StringComparison.OrdinalIgnoreCase);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch
        {

            return false;

        }

    }

    /// <summary>
    /// Replays an already-read prefix ahead of the rest of a one-shot response stream, so bounding
    /// the retry inspection never costs the caller any of the body.
    /// </summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
    {

        private int _prefixPosition;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {

            if (_prefixPosition < prefix.Length)
            {

                int taken = Math.Min(buffer.Length, prefix.Length - _prefixPosition);

                prefix.Span.Slice(_prefixPosition, taken).CopyTo(buffer);

                _prefixPosition += taken;

                return taken;

            }

            return rest.Read(buffer);

        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {

            if (_prefixPosition < prefix.Length)
            {

                int taken = Math.Min(buffer.Length, prefix.Length - _prefixPosition);

                prefix.Slice(_prefixPosition, taken).CopyTo(buffer);

                _prefixPosition += taken;

                return taken;

            }

            return await rest.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {

            if (disposing)
            {

                rest.Dispose();

            }

            base.Dispose(disposing);

        }

    }

}
