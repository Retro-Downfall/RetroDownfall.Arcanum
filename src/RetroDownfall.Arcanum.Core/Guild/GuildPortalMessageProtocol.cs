using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GuildPortalMessageRequest(string TaskId, string ContextId, Guid MessageId, Guid StreamId, long Sequence,
    string MimeType, string Body, string IntentDigest)
{
    public GuildPortalMessageReceiptQuery ReceiptQuery() => new(TaskId, ContextId, MessageId, IntentDigest);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GuildPortalMessageReceiptQuery(string TaskId, string ContextId, Guid MessageId, string IntentDigest);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GuildPortalMessageDeliveryReceipt(string TaskId, string ContextId, Guid MessageId, string IntentDigest,
    Guid ReceiptId, GuildDeliveryState DeliveryState, long Revision, Guid? ApplicationReceiptId);

public static class GuildPortalMessageProtocol
{
    public const string Capability = "urn:retrodownfall:arcanum:guild-portal-message:v1";

    public const string RouteSuffix = "/guild/v1/messages";

    public const string ReceiptRouteSuffix = RouteSuffix + "/receipt";

    public const int MaximumIdentifierBytes = 4096;

    public const int MaximumBodyBytes = 65_536;

    // JSON may encode every UTF-8 byte of these values as a six-character escape.
    public const int MaximumRequestBytes = 6 * (2 * MaximumIdentifierBytes + MaximumBodyBytes) + 1024;

    public const int MaximumReceiptQueryBytes = 6 * (2 * MaximumIdentifierBytes) + 1024;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool ValidReceiptQuery(GuildPortalMessageReceiptQuery? query) =>
        query is not null && ValidIdentifier(query.TaskId) && ValidIdentifier(query.ContextId)
        && query.MessageId != Guid.Empty && ValidDigest(query.IntentDigest);

    public static bool ValidDigest(string? digest) => digest is { Length: 64 }
        && digest.All(static value => value is >= '0' and <= '9' or >= 'A' and <= 'F');

    public static Result<string> ComputeIntentDigest(GuildPortalMessageRequest? request)
    {
        if (request is null || !ValidIdentifier(request.TaskId) || !ValidIdentifier(request.ContextId)
            || request.MessageId == Guid.Empty || request.StreamId == Guid.Empty || request.Sequence < 1
            || request.MimeType is not ("text/plain" or "application/json") || request.Body is null)
            return Invalid;

        try
        {
            if (request.Body.Length > MaximumBodyBytes || Utf8.GetByteCount(request.Body) > MaximumBodyBytes)
                return new Error(ErrorCodes.Guild.PayloadTooLarge, "The informational message exceeds the Guild body limit.");

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            hash.AppendData("arcanum.guild.portal.message.v1\n"u8);

            Append(hash, request.TaskId);

            Append(hash, request.ContextId);

            Append(hash, request.MessageId.ToString("N"));

            Append(hash, request.StreamId.ToString("N"));

            Append(hash, request.Sequence.ToString(CultureInfo.InvariantCulture));

            Append(hash, request.MimeType);

            Append(hash, request.Body);

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (EncoderFallbackException)
        {
            return Invalid;
        }
    }

    private static bool ValidIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumIdentifierBytes) return false;

        try { return Utf8.GetByteCount(value) <= MaximumIdentifierBytes; }
        catch (EncoderFallbackException) { return false; }
    }

    private static void Append(IncrementalHash hash, string field)
    {
        byte[] bytes = Utf8.GetBytes(field);

        Span<byte> length = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);

        hash.AppendData(length);

        hash.AppendData(bytes);
    }

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "The informational message identity or body is invalid.");
}
