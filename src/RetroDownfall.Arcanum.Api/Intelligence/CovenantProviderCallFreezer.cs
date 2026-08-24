using System.Collections.Immutable;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Everything one provider attempt is about to send, in the shape the admission receipt can sign.
/// </summary>
public sealed record CovenantProviderCallDescriptor(
    string ProviderIdentity,
    string ModelIdentity,
    CovenantProviderDispatchMode DispatchMode,
    string TokenizerProfile,
    ulong ContextWindowIdentity,
    ulong CompressionGeneration,
    string SystemPrompt,
    SystemPromptAttributionMap? Attribution,
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions? Options);

/// <summary>
/// Freezes the exact bytes of one provider attempt into a <see cref="ProviderCallEnvelope"/>.
/// </summary>
/// <remarks>
/// The receipt an operator later reads says "this content was admitted to <em>that</em> call". That
/// sentence is only true if the envelope binds what actually went out, so this translation is
/// deliberately total and deliberately failing: a message part or an option this build does not know
/// how to freeze produces a refusal, never a silently narrower envelope. An envelope that quietly
/// omits a content kind would let a payload leave under evidence that does not describe it.
///
/// <para>Nothing here runs on a clean dispatch. A call carrying no Covenant-derived content is never
/// frozen at all, which is what keeps the disabled path free of this work.</para>
/// </remarks>
public static class CovenantProviderCallFreezer
{

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Result<ProviderCallEnvelope> TryFreeze(
        CovenantProviderCallDescriptor descriptor,
        ProviderCallSensitivity sensitivity,
        ProviderCallMaterializationSnapshot materialization)
    {

        ArgumentNullException.ThrowIfNull(descriptor);

        ArgumentNullException.ThrowIfNull(sensitivity);

        ArgumentNullException.ThrowIfNull(materialization);

        Result<FrozenProviderOptions> options = FreezeOptions(descriptor.Options);

        if (options.IsFailure)
        {

            return Result<ProviderCallEnvelope>.Failure(options.Error);

        }

        Result<ImmutableArray<ProviderMessageEnvelope>> messages = FreezeMessages(descriptor.Messages);

        if (messages.IsFailure)
        {

            return Result<ProviderCallEnvelope>.Failure(messages.Error);

        }

        Result<ImmutableArray<ProviderToolDefinitionEnvelope>> tools = FreezeTools(descriptor.Options);

        if (tools.IsFailure)
        {

            return Result<ProviderCallEnvelope>.Failure(tools.Error);

        }

        byte[] systemPromptBytes;

        try
        {

            systemPromptBytes = StrictUtf8.GetBytes(descriptor.SystemPrompt ?? string.Empty);

        }
        catch (EncoderFallbackException)
        {

            return Result<ProviderCallEnvelope>.Failure(new Error(
                ErrorCodes.Covenant.InvalidContent,
                "The rendered system prompt is not strict UTF-8 and cannot be frozen."));

        }

        try
        {

            return Result<ProviderCallEnvelope>.Success(new ProviderCallEnvelope(
                descriptor.ProviderIdentity,
                descriptor.ModelIdentity,
                descriptor.DispatchMode,
                descriptor.TokenizerProfile,
                descriptor.ContextWindowIdentity,
                descriptor.CompressionGeneration,
                sensitivity,
                options.Value,
                systemPromptBytes,
                FreezeSpans(descriptor.Attribution, descriptor.SystemPrompt ?? string.Empty),
                materialization,
                messages.Value,
                tools.Value,
                null));

        }
        catch (ArgumentException exception)
        {

            return Result<ProviderCallEnvelope>.Failure(new Error(
                ErrorCodes.Covenant.InvalidContent,
                $"The provider call could not be frozen: {exception.Message}"));

        }

    }

    /// <summary>
    /// Projects the rendered prompt's attribution partition onto the frozen call.
    /// </summary>
    /// <remarks>
    /// Only the Covenant spans travel. The rest of the partition describes ordinary context whose
    /// placement is already implied by the system-prompt bytes themselves, while the Covenant spans are
    /// what a later reader needs in order to say which region of the prompt the admitted sections
    /// occupied without re-parsing headings out of attacker-influenced text.
    /// </remarks>
    private static ImmutableArray<ProviderPromptSpan> FreezeSpans(
        SystemPromptAttributionMap? attribution,
        string prompt)
    {

        if (attribution is not { HasCovenantContent: true }
            || !string.Equals(attribution.Prompt, prompt, StringComparison.Ordinal))
        {

            return [];

        }

        ImmutableArray<ProviderPromptSpan>.Builder spans = ImmutableArray.CreateBuilder<ProviderPromptSpan>();

        foreach (SystemPromptAttributionSpan span in attribution.Spans)
        {

            if (span.Attribution is not (CovenantPromptAttribution.CovenantConfirmed
                or CovenantPromptAttribution.CovenantProposed))
            {

                continue;

            }

            ReadOnlySpan<char> text = prompt.AsSpan(span.Utf16Start, span.Utf16Length);

            spans.Add(new ProviderPromptSpan(
                span.Attribution,
                (uint)span.Utf16Start,
                (uint)span.Utf16Length,
                new CovenantDigest(SHA256.HashData(StrictUtf8.GetBytes(text.ToString())))));

        }

        return spans.ToImmutable();

    }

    private static Result<ImmutableArray<ProviderMessageEnvelope>> FreezeMessages(IReadOnlyList<ChatMessage> messages)
    {

        if (messages is null || messages.Count == 0)
        {

            return Result<ImmutableArray<ProviderMessageEnvelope>>.Success([]);

        }

        ImmutableArray<ProviderMessageEnvelope>.Builder frozen =
            ImmutableArray.CreateBuilder<ProviderMessageEnvelope>(messages.Count);

        foreach (ChatMessage message in messages)
        {

            Result<CovenantProviderRole> role = FreezeRole(message.Role);

            if (role.IsFailure)
            {

                return Result<ImmutableArray<ProviderMessageEnvelope>>.Failure(role.Error);

            }

            ImmutableArray<ProviderContentPartEnvelope>.Builder parts =
                ImmutableArray.CreateBuilder<ProviderContentPartEnvelope>();

            foreach (AIContent content in message.Contents)
            {

                Result<ProviderContentPartEnvelope> part = FreezePart(content);

                if (part.IsFailure)
                {

                    return Result<ImmutableArray<ProviderMessageEnvelope>>.Failure(part.Error);

                }

                parts.Add(part.Value);

            }

            frozen.Add(new ProviderMessageEnvelope(
                role.Value,
                message.MessageId,
                message.AuthorName,
                parts.ToImmutable()));

        }

        return Result<ImmutableArray<ProviderMessageEnvelope>>.Success(frozen.ToImmutable());

    }

    private static Result<CovenantProviderRole> FreezeRole(ChatRole role) =>
        role == ChatRole.System ? Result<CovenantProviderRole>.Success(CovenantProviderRole.System)
        : role == ChatRole.User ? Result<CovenantProviderRole>.Success(CovenantProviderRole.User)
        : role == ChatRole.Assistant ? Result<CovenantProviderRole>.Success(CovenantProviderRole.Assistant)
        : role == ChatRole.Tool ? Result<CovenantProviderRole>.Success(CovenantProviderRole.Tool)
        : Result<CovenantProviderRole>.Failure(new Error(
            ErrorCodes.Covenant.InvalidContent,
            "A provider message carries a role this build cannot freeze."));

    private static Result<ProviderContentPartEnvelope> FreezePart(AIContent content) =>
        content switch
        {
            TextReasoningContent reasoning =>
                Result<ProviderContentPartEnvelope>.Success(
                    ProviderContentPartEnvelope.TextReasoning(reasoning.Text ?? string.Empty)),

            TextContent text =>
                Result<ProviderContentPartEnvelope>.Success(
                    ProviderContentPartEnvelope.Text(text.Text ?? string.Empty)),

            DataContent data =>
                Result<ProviderContentPartEnvelope>.Success(
                    ProviderContentPartEnvelope.Binary(
                        data.MediaType,
                        null,
                        null,
                        data.Data.Span)),

            UriContent uri =>
                Result<ProviderContentPartEnvelope>.Success(
                    ProviderContentPartEnvelope.Uri(
                        uri.Uri.AbsoluteUri,
                        uri.MediaType,
                        null)),

            FunctionCallContent call => FreezeToolCall(call),

            FunctionResultContent result => FreezeToolResult(result),

            _ => Result<ProviderContentPartEnvelope>.Failure(new Error(
                ErrorCodes.Covenant.InvalidContent,
                $"A provider message carries {content.GetType().Name}, which this build cannot freeze.")),
        };

    private static Result<ProviderContentPartEnvelope> FreezeToolCall(FunctionCallContent call)
    {

        Result<byte[]> arguments = CanonicalizeArguments(call.Arguments);

        return arguments.IsFailure
            ? Result<ProviderContentPartEnvelope>.Failure(arguments.Error)
            : Result<ProviderContentPartEnvelope>.Success(
                ProviderContentPartEnvelope.ToolCall(call.CallId, call.Name, arguments.Value));

    }

    private static Result<ProviderContentPartEnvelope> FreezeToolResult(FunctionResultContent result)
    {

        string text = result.Result switch
        {
            null => string.Empty,
            string value => value,
            JsonElement element => element.GetRawText(),
            _ => result.Result.ToString() ?? string.Empty,
        };

        return Result<ProviderContentPartEnvelope>.Success(
            ProviderContentPartEnvelope.ToolResult(result.CallId, StrictUtf8.GetBytes(text)));

    }

    /// <summary>
    /// Canonicalizes one tool call's arguments, which is what makes a replayed call recognizable.
    /// </summary>
    /// <remarks>
    /// Two providers may serialize the same arguments with different key order and different number
    /// formatting. Freezing the raw text would make an identical call look like a different one on the
    /// next attempt, so the bytes are canonicalized before they are bound.
    /// </remarks>
    private static Result<byte[]> CanonicalizeArguments(IDictionary<string, object?>? arguments)
    {

        if (arguments is null || arguments.Count == 0)
        {

            return Result<byte[]>.Success(ArcanumCanonicalJsonV1.Canonicalize("{}"u8));

        }

        try
        {

            using MemoryStream buffer = new();

            using (Utf8JsonWriter writer = new(buffer))
            {

                writer.WriteStartObject();

                foreach (KeyValuePair<string, object?> argument in arguments)
                {

                    writer.WritePropertyName(argument.Key);

                    WriteArgumentValue(writer, argument.Value);

                }

                writer.WriteEndObject();

            }

            return Result<byte[]>.Success(ArcanumCanonicalJsonV1.Canonicalize(buffer.ToArray()));

        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
        {

            return Result<byte[]>.Failure(new Error(
                ErrorCodes.Covenant.InvalidContent,
                "A tool call's arguments could not be canonicalized for the frozen provider call."));

        }

    }

    private static void WriteArgumentValue(Utf8JsonWriter writer, object? value)
    {

        switch (value)
        {

            case null:

                writer.WriteNullValue();

                break;

            case string text:

                writer.WriteStringValue(text);

                break;

            case bool flag:

                writer.WriteBooleanValue(flag);

                break;

            case JsonElement element:

                element.WriteTo(writer);

                break;

            case int or long or short or byte or uint or ulong or ushort or sbyte:

                writer.WriteNumberValue(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));

                break;

            case double or float or decimal:

                writer.WriteNumberValue(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));

                break;

            default:

                writer.WriteStringValue(value.ToString());

                break;

        }

    }

    private static Result<ImmutableArray<ProviderToolDefinitionEnvelope>> FreezeTools(ChatOptions? options)
    {

        if (options?.Tools is not { Count: > 0 } tools)
        {

            return Result<ImmutableArray<ProviderToolDefinitionEnvelope>>.Success([]);

        }

        ImmutableArray<ProviderToolDefinitionEnvelope>.Builder frozen =
            ImmutableArray.CreateBuilder<ProviderToolDefinitionEnvelope>(tools.Count);

        // Ordered by name so that a provider surface assembled in a different enumeration order still
        // freezes to the same call. The set is what was offered; the order it happened to be built in
        // is not part of what the model saw.
        foreach (AITool tool in tools.OrderBy(static value => value.Name, StringComparer.Ordinal))
        {

            string description = tool.Description ?? string.Empty;

            byte[] schema;

            try
            {

                schema = ArcanumCanonicalJsonV1.Canonicalize(
                    tool is AIFunction function
                        ? function.JsonSchema.GetRawText()
                        : "{}");

            }
            catch (ArgumentException)
            {

                return Result<ImmutableArray<ProviderToolDefinitionEnvelope>>.Failure(new Error(
                    ErrorCodes.Covenant.InvalidContent,
                    $"The tool '{tool.Name}' has a schema that cannot be canonicalized for a frozen provider call."));

            }

            frozen.Add(new ProviderToolDefinitionEnvelope(
                tool.Name,
                description,
                new CovenantDigest(SHA256.HashData(StrictUtf8.GetBytes(description))),
                schema,
                new CovenantDigest(SHA256.HashData(schema)),
                default,
                null,

                // Risk classification belongs to the Ward, which resolves it per call against the live
                // policy. The envelope records the ordinary identity because it is freezing what was
                // advertised, not what a later invocation of one of these tools would be allowed to do.
                CovenantToolRiskIdentity.Ordinary));

        }

        return Result<ImmutableArray<ProviderToolDefinitionEnvelope>>.Success(frozen.ToImmutable());

    }

    private static Result<FrozenProviderOptions> FreezeOptions(ChatOptions? options)
    {

        try
        {

            return Result<FrozenProviderOptions>.Success(FrozenProviderOptions.Create(new ProviderOptionsDigestInput(
                options?.MaxOutputTokens is > 0 and { } max ? (ulong)max : null,
                options?.Temperature,
                options?.TopP,
                options?.FrequencyPenalty,
                options?.PresencePenalty,
                options?.Seed,
                null,
                options?.StopSequences is { Count: > 0 } stop ? [.. stop] : [],
                FreezeToolChoice(options),
                null,
                CovenantTriStateBoolean.Absent,
                ProviderResponseFormat.Text,
                null,
                null,
                null,
                CovenantTriStateBoolean.Absent,
                null,
                null,
                null,
                CovenantReasoningWireDialect.Standard,
                default)));

        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {

            return Result<FrozenProviderOptions>.Failure(new Error(
                ErrorCodes.Covenant.InvalidContent,
                "The provider options for this attempt could not be frozen."));

        }

    }

    private static ProviderToolChoice FreezeToolChoice(ChatOptions? options) =>
        options?.Tools is { Count: > 0 } ? ProviderToolChoice.Auto : ProviderToolChoice.None;

}
