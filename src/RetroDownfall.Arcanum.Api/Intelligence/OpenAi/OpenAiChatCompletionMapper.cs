using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

internal static class OpenAiChatCompletionMapper
{
    private const string ImageReferencePrefix = "[image: ";

    private const string ImageReferenceSuffix = "]";

    private const string DataUriScheme = "data:";

    private const string ElidedPayloadMarker = "<elided>";

    private const int MaxDataUriHeaderChars = 128;

    internal static PingRequest ToPingRequest(OpenAiChatRequest request, bool forwardClientTools = false)
    {
        List<OpenAiChatMessage> msgs = request.Messages!;

        List<CoreChatMessage> stateless = new(msgs.Count);

        foreach (OpenAiChatMessage m in msgs)
        {
            stateless.Add(ToCoreChatMessage(m));
        }

        int? resolvedMaxOutput = request.MaxCompletionTokens ?? request.MaxTokens;

        IReadOnlyList<string>? stop = ExtractStop(request.Stop);

        string? responseFormat = ExtractResponseFormatType(request.ResponseFormat);

        JsonElement? responseFormatJsonSchema = ExtractResponseFormatJsonSchema(request.ResponseFormat);

        return new PingRequest(
            Prompt: string.Empty,
            Model: request.Model,
            WorkingDirectory: string.Empty,
            ContextSnapshot: null,
            SessionId: null,
            DisableMcpTools: false,
            CliTerminalFormatting: false,
            UnattendedMode: true,
            AttachedFiles: null,
            ChronosyncDelta: null,
            StatelessMessages: stateless,
            Temperature: request.Temperature,
            TopP: request.TopP,
            MaxOutputTokens: resolvedMaxOutput,
            Stop: stop,
            Seed: request.Seed,
            ResponseFormat: responseFormat,
            ResponseFormatJsonSchema: responseFormatJsonSchema,
            PresencePenalty: request.PresencePenalty,
            FrequencyPenalty: request.FrequencyPenalty,
            User: request.User,
            ParallelToolCalls: request.ParallelToolCalls,
            ClientTools: forwardClientTools ? request.Tools : null,
            ClientToolChoice: forwardClientTools ? request.ToolChoice : null,
            ForwardClientTools: forwardClientTools,
            Reasoning: ToReasoningOptions(request));
    }

    internal static CoreChatMessage ToCoreChatMessage(OpenAiChatMessage m)
    {
        (string flatText, IReadOnlyList<CoreContentPart>? parts) = ResolveContent(m.Content);

        IReadOnlyList<CoreToolCall>? toolCalls = null;

        if (m.ToolCalls is { Length: > 0 } src)
        {
            List<CoreToolCall> list = new(src.Length);

            foreach (OpenAiToolCall tc in src)
            {
                if (tc?.Function is null)
                {
                    continue;
                }

                list.Add(new CoreToolCall(
                    Id: tc.Id ?? string.Empty,
                    Name: tc.Function.Name ?? string.Empty,
                    ArgumentsJson: tc.Function.Arguments ?? string.Empty));
            }

            if (list.Count > 0)
            {
                toolCalls = list;
            }
        }

        return new CoreChatMessage(
            Role: m.Role,
            Content: flatText,
            Name: m.Name,
            ToolCallId: m.ToolCallId,
            ToolCalls: toolCalls,
            ContentParts: parts);
    }

    internal static (string FlattenedText, IReadOnlyList<CoreContentPart>? Parts) ResolveContent(OpenAiMessageContent? content)
    {
        if (content is null)
        {
            return (string.Empty, null);
        }

        if (content.Parts is not { Length: > 0 } srcParts)
        {
            return (content.Text ?? string.Empty, null);
        }

        StringBuilder textBuilder = new();

        List<CoreContentPart> coreParts = new(srcParts.Length);

        foreach (OpenAiContentPart part in srcParts)
        {
            if (part is null)
            {
                continue;
            }

            if (string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase))
            {
                string text = part.Text ?? string.Empty;

                if (textBuilder.Length > 0 && text.Length > 0)
                {
                    textBuilder.Append('\n');
                }

                textBuilder.Append(text);

                coreParts.Add(new CoreContentPart("text", text, null, null));

                continue;
            }

            if (string.Equals(part.Type, "image_url", StringComparison.OrdinalIgnoreCase)
                && part.ImageUrl is { } imageUrl
                && !string.IsNullOrWhiteSpace(imageUrl.Url))
            {
                if (textBuilder.Length > 0)
                {
                    textBuilder.Append('\n');
                }

                textBuilder.Append(ImageReferencePrefix);

                AppendImageReference(textBuilder, imageUrl.Url);

                textBuilder.Append(ImageReferenceSuffix);

                coreParts.Add(new CoreContentPart("image_url", null, imageUrl.Url, imageUrl.Detail));
            }
        }

        return (textBuilder.ToString(), coreParts);
    }

    /// <summary>
    /// Appends the flattened rendering of an <c>image_url</c> part. Remote URLs are written verbatim;
    /// a <c>data:</c> URI keeps only its media-type header and elides the base64 payload, so the
    /// rendering stays bounded no matter how large the inline image is. The full URI is preserved
    /// verbatim on the <see cref="CoreContentPart"/>, which is the copy that actually reaches the
    /// provider — the flattened text exists only for storage/logging and the text-only fallback
    /// consumers (guardrails scanning, the RAG probe), none of which can do anything with base64.
    /// Inlining the payload here would charge every inline image twice against
    /// <c>MaxEntryContentBytes</c> (once as <c>Content</c>, once as the part), pushing the effective
    /// inline-image ceiling well below the configured <c>Scrying:MaxImageBytes</c>, and would pour a
    /// megabyte of encoding into transcripts and embeddings.
    /// </summary>
    private static void AppendImageReference(StringBuilder builder, string url)
    {
        if (!url.StartsWith(DataUriScheme, StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(url);

            return;
        }

        int payloadStart = url.IndexOf(',');

        if (payloadStart < 0 || payloadStart > MaxDataUriHeaderChars)
        {
            builder.Append(DataUriScheme).Append(ElidedPayloadMarker);

            return;
        }

        builder.Append(url.AsSpan(0, payloadStart + 1)).Append(ElidedPayloadMarker);
    }

    private static IReadOnlyList<string>? ExtractStop(JsonElement? stop)
    {
        if (stop is not { } element)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;

            case JsonValueKind.String:
                string? single = element.GetString();

                return string.IsNullOrEmpty(single) ? null : [single];

            case JsonValueKind.Array:
                List<string> list = new(element.GetArrayLength());

                foreach (JsonElement child in element.EnumerateArray())
                {
                    if (child.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? s = child.GetString();

                    if (!string.IsNullOrEmpty(s))
                    {
                        list.Add(s);
                    }
                }

                return list.Count == 0 ? null : list;

            default:
                return null;
        }
    }

    private static string? ExtractResponseFormatType(OpenAiResponseFormat? responseFormat)
    {
        if (responseFormat is null || string.IsNullOrWhiteSpace(responseFormat.Type))
        {
            return null;
        }

        return responseFormat.Type.Trim().ToLowerInvariant() switch
        {
            "json_object" => "json_object",
            "json_schema" => "json_schema",
            "text" => "text",
            _ => responseFormat.Type.Trim(),
        };
    }

    private static JsonElement? ExtractResponseFormatJsonSchema(OpenAiResponseFormat? responseFormat)
    {
        if (responseFormat?.JsonSchema is not { } jsonSchema
            || jsonSchema.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return jsonSchema.Clone();
    }

    private static ReasoningRequestOptions? ToReasoningOptions(OpenAiChatRequest request)
    {
        if (request.ReasoningEffort is null
            && request.ReasoningBudget is null
            && request.ReasoningOutput is null)
        {
            return null;
        }

        return new ReasoningRequestOptions(
            MapReasoningEffort(request.ReasoningEffort),
            request.ReasoningBudget,
            request.ReasoningOutput);
    }

    private static ReasoningEffortLevel? MapReasoningEffort(OpenAiReasoningEffort? effort) =>
        effort switch
        {
            null => null,
            OpenAiReasoningEffort.None => ReasoningEffortLevel.None,
            OpenAiReasoningEffort.Minimal => ReasoningEffortLevel.Minimal,
            OpenAiReasoningEffort.Low => ReasoningEffortLevel.Low,
            OpenAiReasoningEffort.Medium => ReasoningEffortLevel.Medium,
            OpenAiReasoningEffort.High => ReasoningEffortLevel.High,
            OpenAiReasoningEffort.XHigh => ReasoningEffortLevel.ExtraHigh,
            _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unknown OpenAI reasoning effort."),
        };
}
