using System.Text.Json;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

/// <summary>Caps every success and error body before the SDK allocates or parses it.</summary>
public sealed class MemoryEvaluationBoundedResponseHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        try
        {
            const int limit = MemoryEvaluationLimits.ProviderResponseBytes;

            HttpContent original = response.Content;

            if (original.Headers.ContentLength > limit) throw new HttpRequestException("The evaluation response exceeds its byte limit.");

            await using Stream stream = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            using MemoryStream buffered = new();

            byte[] chunk = new byte[8192];

            while (true)
            {
                int count = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, limit + 1 - (int)buffered.Length)), cancellationToken).ConfigureAwait(false);

                if (count == 0) break;

                buffered.Write(chunk, 0, count);

                if (buffered.Length > limit) throw new HttpRequestException("The evaluation response exceeds its byte limit.");
            }

            byte[] bytes = buffered.ToArray();

            if (response.IsSuccessStatusCode) ValidateRawResponse(bytes);

            ByteArrayContent bounded = new(bytes);

            foreach (var header in original.Headers) bounded.Headers.TryAddWithoutValidation(header.Key, header.Value);

            response.Content = bounded;

            original.Dispose();

            return response;
        }
        catch
        {
            response.Dispose();

            throw;
        }
    }

    private static void ValidateRawResponse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);

        JsonElement root = document.RootElement;

        JsonElement usage = Property(root, "usage");

        long? input = Count(Property(usage, "prompt_tokens"));

        long? output = Count(Property(usage, "completion_tokens"));

        long? cached = OptionalCount(Property(usage, "prompt_tokens_details"), "cached_tokens");

        long? reasoning = OptionalCount(Property(usage, "completion_tokens_details"), "reasoning_tokens");

        bool known = input.HasValue && output.HasValue && cached.HasValue && reasoning.HasValue
            && cached <= input && reasoning <= output;

        MemoryEvaluationUsage measured = new(input, output, null, known ? cached : null, known ? reasoning : null);

        JsonElement choices = Property(root, "choices");

        bool single = choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() == 1;

        JsonElement choice = single ? choices[0] : default;

        JsonElement message = Property(choice, "message");

        bool textOnly = Text(Property(message, "role")) == "assistant" && IsTextContent(Property(message, "content"))
            && !Present(Property(message, "refusal")) && !Present(Property(message, "function_call"))
            && !Present(Property(message, "audio")) && !Present(Property(message, "video"))
            && !Present(Property(message, "images")) && !Present(Property(message, "image"))
            && IsEmptyOrAbsent(Property(message, "tool_calls"));

        if (!known || !single || !textOnly)
        {
            throw new MemoryEvaluationRejectedResponseException(
                known ? MemoryEvaluationReason.InvalidOutput : MemoryEvaluationReason.UsageFailure, measured,
                Text(Property(root, "model")), single && Text(Property(choice, "finish_reason")) == "stop", known);
        }
    }

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) ? property : default;

    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Count(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long count) && count is >= 0 and <= int.MaxValue ? count : null;

    private static long? OptionalCount(JsonElement details, string name) =>
        Property(details, name) is { ValueKind: JsonValueKind.Undefined } ? 0 : Count(Property(details, name));

    private static bool Present(JsonElement value) => value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static bool IsEmptyOrAbsent(JsonElement value) =>
        !Present(value) || (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0);

    private static bool IsTextContent(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return true;

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) return false;

        foreach (JsonElement part in value.EnumerateArray())
        {
            if (Text(Property(part, "type")) != "text" || Property(part, "text").ValueKind != JsonValueKind.String) return false;
        }

        return true;
    }
}

// Carries only bounded dispatch facts through the SDK transport; no provider text or exception detail.
internal sealed class MemoryEvaluationRejectedResponseException(
    MemoryEvaluationReason reason, MemoryEvaluationUsage usage, string? model, bool complete, bool usageKnown)
    : Exception("The evaluation response failed the raw response contract.")
{
    internal MemoryEvaluationReason Reason { get; } = reason;

    internal MemoryEvaluationUsage Usage { get; } = usage;

    internal string? Model { get; } = model;

    internal bool Complete { get; } = complete;

    internal bool UsageKnown { get; } = usageKnown;
}
