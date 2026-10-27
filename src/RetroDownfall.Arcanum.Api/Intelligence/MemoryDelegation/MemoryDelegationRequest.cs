using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
using Microsoft.Net.Http.Headers;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;

public sealed record MemoryDelegationRequest(PingRequest Parent, MemoryDelegationGrant Grant);

internal static class MemoryDelegationRequestJson
{
    // Matches the ordinary native parent transport ceiling, including attachments and the bounded grant.
    internal const int RequestBytes = 16 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static readonly ArcanumJsonContext Closed = new(new JsonSerializerOptions(ArcanumJsonContext.Default.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    });

    internal static async Task<(MemoryDelegationRequest? Body, IResult? Error)> ReadAsync(HttpContext http)
    {
        CovenantRequestFeatures.MarkProtectedResponse(http);

        MemoryDelegationCallerOrigin? origin = MemoryDelegationCallerOrigin.Capture(http);

        if (!http.Request.HasJsonContentType()
            || !MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var media))
            return (null, Invalid(origin, MemoryDelegationBodyFailure.ContentType));

        if (media.Charset.HasValue && !media.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            return (null, Invalid(origin, MemoryDelegationBodyFailure.Charset));

        if (http.Request.ContentLength is > RequestBytes and { } declared)
            return (null, Invalid(origin, MemoryDelegationBodyFailure.DeclaredTooLarge, declared));

        try
        {
            using MemoryStream buffer = new();

            byte[] chunk = new byte[16 * 1024];

            while (buffer.Length <= RequestBytes)
            {
                int read = await http.Request.Body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, RequestBytes + 1L - buffer.Length)), http.RequestAborted).ConfigureAwait(false);

                if (read == 0) break;

                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length > RequestBytes) return (null, Invalid(origin, MemoryDelegationBodyFailure.ReadTooLarge, buffer.Length));

            ReadOnlySpan<byte> bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);

            try
            {
                _ = Utf8.GetCharCount(bytes);
            }
            catch (DecoderFallbackException)
            {
                return (null, Invalid(origin, MemoryDelegationBodyFailure.Utf8));
            }

            try
            {
                var body = JsonSerializer.Deserialize(bytes, Closed.MemoryDelegationRequest);

                return body is null ? (null, Invalid(origin, MemoryDelegationBodyFailure.Shape)) : (body, null);
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            {
                return (null, Invalid(origin, MemoryDelegationBodyFailure.Shape));
            }
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, Invalid(origin, MemoryDelegationBodyFailure.TransportTooLarge));
        }
        catch (Exception error) when (error is NotSupportedException or IOException or BadHttpRequestException or JsonException or ArgumentException)
        {
            return (null, Invalid(origin, null));
        }
    }

    private static IResult Invalid(MemoryDelegationCallerOrigin? origin, MemoryDelegationBodyFailure? reason, long? measured = null) =>
        MemoryDelegationEndpoints.Failure(new(ErrorCodes.MemoryDelegation.InvalidGrant, MemoryDelegationCallerDiagnostic.ProtectedMessage),
            origin is not null && reason is { } failure ? MemoryDelegationCallerDiagnostic.ForBody(origin, failure, measured) : null);
}

/// <summary>Only the existing authenticated feature and issuer, captured before awaited body I/O.</summary>
internal sealed class MemoryDelegationCallerOrigin(CovenantAuthorityFeature original, IOperatorAuthorityContextIssuer issuer)
{
    private readonly CovenantAuthorityFeature _original = original ?? throw new ArgumentNullException(nameof(original));

    private readonly IOperatorAuthorityContextIssuer _issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));

    internal static MemoryDelegationCallerOrigin? Capture(HttpContext http)
    {
        var original = CovenantRequestFeatures.Authority(http);

        if (original is null) return null;

        var issuer = http.RequestServices.GetService<IOperatorAuthorityContextIssuer>();

        return issuer is null ? null : new(original, issuer);
    }

    internal Result Revalidate(HttpContext http)
    {
        http.RequestAborted.ThrowIfCancellationRequested();

        return ReferenceEquals(CovenantRequestFeatures.Authority(http), _original)
            && _original.Requirement == CovenantAuthorityRequirement.ProtectedRead
            && _original.Context.Satisfies(CovenantAuthorityRequirement.ProtectedRead)
            && _original.AuthorityEpoch == _original.Context.AuthorityEpoch
            && _issuer.Revalidate(_original.Context).IsSuccess
                ? Result.Success()
                : new Error(ErrorCodes.MemoryDelegation.StaleSource, MemoryDelegationCallerDiagnostic.ProtectedMessage);
    }
}

internal enum MemoryDelegationBodyFailure
{
    ContentType = 1,
    Charset,
    DeclaredTooLarge,
    ReadTooLarge,
    TransportTooLarge,
    Utf8,
    Shape,
}

/// <summary>Local caller-error provenance, never a wire handle or a pass-through exception/message.</summary>
internal sealed class MemoryDelegationCallerDiagnostic(
    MemoryDelegationCallerOrigin origin,
    MemoryDelegationBodyFailure? body,
    PingRequestFailureDetails? preflight,
    long? measuredBytes)
{
    internal const string ProtectedMessage = "The memory delegation request could not be completed.";

    internal MemoryDelegationCallerOrigin Origin { get; } = origin;

    internal static MemoryDelegationCallerDiagnostic ForBody(MemoryDelegationCallerOrigin origin, MemoryDelegationBodyFailure reason, long? measured = null) =>
        new(origin, reason, null, measured);

    internal static MemoryDelegationCallerDiagnostic ForPreflight(MemoryDelegationCallerOrigin origin, PingRequestFailureDetails details) =>
        new(origin, null, details, null);

    internal bool IsLocalRequestBoundFailure => body is null && preflight?.Reason is
        PingRequestFailureReason.PromptTooLong or PingRequestFailureReason.AdditionalPromptTooLong
        or PingRequestFailureReason.TooManyMessages or PingRequestFailureReason.TooManyParts
        or PingRequestFailureReason.MessageTooLong;

    internal string? PublicMessage(string code)
    {
        if (body is { } reason && preflight is null && code == ErrorCodes.MemoryDelegation.InvalidGrant)
        {
            return reason switch
            {
                MemoryDelegationBodyFailure.ContentType => "Send the delegation request with Content-Type: application/json.",
                MemoryDelegationBodyFailure.Charset => "Use UTF-8 (charset=utf-8) for the delegation request.",
                MemoryDelegationBodyFailure.Utf8 => "Encode the delegation request as strict UTF-8.",
                MemoryDelegationBodyFailure.TransportTooLarge => $"The transport rejected the delegation request body as too large. Reduce or split the parent inputs; the delegation endpoint accepts at most {MemoryDelegationRequestJson.RequestBytes} bytes.",
                MemoryDelegationBodyFailure.Shape => "Use the declared parent and grant fields; omit unknown members and provide values with their documented JSON types.",
                MemoryDelegationBodyFailure.DeclaredTooLarge when measuredBytes is > MemoryDelegationRequestJson.RequestBytes => FormattableString.Invariant(
                    $"Declared delegation request body: {measuredBytes} bytes; limit {MemoryDelegationRequestJson.RequestBytes}. Reduce or split the parent inputs and send a bounded request."),
                MemoryDelegationBodyFailure.ReadTooLarge when measuredBytes is > MemoryDelegationRequestJson.RequestBytes => FormattableString.Invariant(
                    $"Delegation request body exceeds the limit {MemoryDelegationRequestJson.RequestBytes} bytes (at least {measuredBytes} bytes read). Reduce or split the parent inputs and send a bounded request."),
                _ => null,
            };
        }

        if (body is not null || preflight is null || preflight.ExpectedCode != code) return null;

        bool indexed = preflight.Index is >= 0;

        bool exceeded = indexed && preflight.Measured is { } measured && preflight.Limit is > 0 and { } limit && measured > limit;

        return preflight.Reason switch
        {
            PingRequestFailureReason.AttachmentMissing when indexed => FormattableString.Invariant($"parent.attachedFiles[{preflight.Index}] must be an object with relativePath and content."),
            PingRequestFailureReason.AttachmentPathMissing when indexed => FormattableString.Invariant($"parent.attachedFiles[{preflight.Index}].relativePath must be non-empty."),
            PingRequestFailureReason.AttachmentPathTooLong when exceeded => FormattableString.Invariant($"parent.attachedFiles[{preflight.Index}].relativePath has {preflight.Measured} characters; limit {preflight.Limit}. Shorten the relative path and retry."),
            PingRequestFailureReason.AttachmentTooLarge when exceeded => FormattableString.Invariant($"parent.attachedFiles[{preflight.Index}].content has {preflight.Measured} UTF-8 bytes; limit {preflight.Limit}. No attachment work was saved. Split the content into smaller chunks and retry."),
            PingRequestFailureReason.AttachmentsTotalTooLarge when exceeded => FormattableString.Invariant($"parent.attachedFiles content totals {preflight.Measured} UTF-8 bytes through index {preflight.Index}; limit {preflight.Limit}. Send the remaining files in a later turn or reference persisted Session attachments."),
            PingRequestFailureReason.PromptTooLong when preflight.Limit is > 0 => FormattableString.Invariant($"parent.prompt exceeds limit {preflight.Limit} characters. Shorten the prompt and retry."),
            PingRequestFailureReason.AdditionalPromptTooLong when preflight.Limit is > 0 => FormattableString.Invariant($"parent.additionalSystemPrompt exceeds limit {preflight.Limit} characters. Shorten it and retry."),
            PingRequestFailureReason.TooManyMessages when preflight.Limit is > 0 => FormattableString.Invariant($"parent.statelessMessages exceeds limit {preflight.Limit} messages. Send fewer messages or use a Session."),
            PingRequestFailureReason.TooManyParts when preflight.Limit is > 0 => FormattableString.Invariant($"parent.statelessMessages contentParts exceeds limit {preflight.Limit} parts per message. Send fewer content parts."),
            PingRequestFailureReason.MessageTooLong when preflight.Limit is > 0 => FormattableString.Invariant($"parent.statelessMessages content exceeds limit {preflight.Limit} UTF-8 bytes per message. Split the content into smaller messages."),
            PingRequestFailureReason.ReasoningEffort => "parent.reasoning.effort must be a supported named value.",
            PingRequestFailureReason.ReasoningOutput => "parent.reasoning.output must be a supported named value.",
            PingRequestFailureReason.ReasoningExclusive => "parent.reasoning must specify effort or budgetTokens, not both.",
            PingRequestFailureReason.ReasoningBudget => "parent.reasoning.budgetTokens must be within 1-2097152 tokens.",
            PingRequestFailureReason.Temperature => "parent.temperature must be a finite number from 0 through 2.",
            PingRequestFailureReason.TopP => "parent.topP must be a finite number from 0 through 1.",
            PingRequestFailureReason.PresencePenalty => "parent.presencePenalty must be a finite number from -2 through 2.",
            PingRequestFailureReason.FrequencyPenalty => "parent.frequencyPenalty must be a finite number from -2 through 2.",
            PingRequestFailureReason.OutputTokens => "parent.maxOutputTokens must be a positive integer.",
            PingRequestFailureReason.ResponseFormat => "parent.responseFormat must be text, json_object, or json_schema.",
            PingRequestFailureReason.ResponseSchema => "parent.responseFormatJsonSchema must be a supported JSON object schema, optionally wrapped with a nonempty name and Boolean strict value.",
            PingRequestFailureReason.ClientToolsSchema or PingRequestFailureReason.ClientToolsDisabled
                or PingRequestFailureReason.ClientToolsCount or PingRequestFailureReason.ClientToolsChoice =>
                "Memory delegation requires native tools. Omit parent.clientTools and parent.clientToolChoice, and set parent.forwardClientTools=false.",
            PingRequestFailureReason.ImagesDisabled => "parent image content requires the Scrying feature to be enabled.",
            PingRequestFailureReason.ImageData => "parent image content must contain a supported absolute HTTP(S) URL or base64 image data URL.",
            PingRequestFailureReason.ImageCount => "parent image content exceeds the image-count limit. Send fewer images.",
            PingRequestFailureReason.ImageMime => "parent image content must use an allowed image MIME type.",
            PingRequestFailureReason.ImageSize => "parent image content exceeds the image-byte limit. Reduce the image size.",
            PingRequestFailureReason.VisionUnsupported => "parent.model must support vision for image content. Select a vision-capable model.",
            _ => null,
        };
    }
}
