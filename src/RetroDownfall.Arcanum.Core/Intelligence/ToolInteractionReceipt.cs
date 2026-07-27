using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Stable logical identity for one tool invocation. The caller owns these ordinals:
/// provider call IDs may repeat, but a round/call pair may not repeat within an invocation.
/// </summary>
public sealed record ToolInvocationIdentity(
    string InvocationId,
    string? ProviderToolCallId,
    int ToolRoundOrdinal,
    int CallOrdinal,
    string ToolName);

/// <summary>
/// Versioned receipt and deterministic primary keys for its generic call/result Grimoire rows.
/// </summary>
public readonly record struct ToolInteractionReceipt(
    Guid Id,
    Guid CallEntryId,
    Guid ResultEntryId);

/// <summary>
/// Deterministic UUIDv8 derivation with explicit domain separation.
/// <para>
/// <c>receipt-format-v1</c> hashes the stable invocation ID, normalized provider call ID,
/// round ordinal, call ordinal, and normalized tool name. It deliberately excludes arguments,
/// patch text, and result content.
/// </para>
/// <para>
/// <c>entry-format-v1</c> separately derives the call and result <c>Entry.Id</c> values from
/// the receipt ID and row kind. Fields are UTF-8 with big-endian length prefixes; ordinals are
/// signed 32-bit big-endian non-negative integers.
/// </para>
/// </summary>
public static class ToolInteractionReceiptDerivation
{

    public const string ReceiptFormatDomain =
        "RetroDownfall.Arcanum/receipt-format-v1";

    public const string EntryFormatDomain =
        "RetroDownfall.Arcanum/entry-format-v1";

    public static ToolInteractionReceipt Derive(ToolInvocationIdentity identity)
    {

        ArgumentNullException.ThrowIfNull(identity);

        string invocationId = NormalizeRequired(identity.InvocationId, nameof(identity.InvocationId));

        string providerToolCallId = NormalizeOptional(identity.ProviderToolCallId);

        string toolName = NormalizeRequired(identity.ToolName, nameof(identity.ToolName))
            .ToLowerInvariant();

        if (identity.ToolRoundOrdinal < 0)
        {

            throw new ArgumentOutOfRangeException(
                nameof(identity.ToolRoundOrdinal),
                "Tool round ordinal must be non-negative.");

        }

        if (identity.CallOrdinal < 0)
        {

            throw new ArgumentOutOfRangeException(
                nameof(identity.CallOrdinal),
                "Call ordinal must be non-negative.");

        }

        Guid receiptId = DeriveGuid(
            writer =>
            {

                writer.WriteString(ReceiptFormatDomain);

                writer.WriteString(invocationId);

                writer.WriteString(providerToolCallId);

                writer.WriteInt32(identity.ToolRoundOrdinal);

                writer.WriteInt32(identity.CallOrdinal);

                writer.WriteString(toolName);

            });

        Guid callEntryId = DeriveEntryId(receiptId, "call");

        Guid resultEntryId = DeriveEntryId(receiptId, "result");

        return new ToolInteractionReceipt(
            receiptId,
            callEntryId,
            resultEntryId);

    }

    public static Guid DeriveCallEntryId(Guid receiptId) =>
        DeriveEntryId(receiptId, "call");

    public static Guid DeriveResultEntryId(Guid receiptId) =>
        DeriveEntryId(receiptId, "result");

    private static Guid DeriveEntryId(Guid receiptId, string rowKind) =>
        DeriveGuid(
            writer =>
            {

                writer.WriteString(EntryFormatDomain);

                writer.WriteString(receiptId.ToString("D"));

                writer.WriteString(rowKind);

            });

    private static Guid DeriveGuid(Action<CanonicalHashWriter> append)
    {

        using CanonicalHashWriter writer = new();

        append(writer);

        byte[] hash = SHA256.HashData(writer.WrittenSpan);

        Span<byte> uuidBytes = hash.AsSpan(0, 16);

        uuidBytes[6] = (byte)((uuidBytes[6] & 0x0F) | 0x80);

        uuidBytes[8] = (byte)((uuidBytes[8] & 0x3F) | 0x80);

        return new Guid(uuidBytes, bigEndian: true);

    }

    private static string NormalizeRequired(string value, string parameterName)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        return value.Trim().Normalize(NormalizationForm.FormC);

    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Normalize(NormalizationForm.FormC);

    private sealed class CanonicalHashWriter : IDisposable
    {

        private readonly MemoryStream _stream = new();

        internal ReadOnlySpan<byte> WrittenSpan =>
            _stream.GetBuffer().AsSpan(0, checked((int)_stream.Length));

        internal void WriteString(string value)
        {

            byte[] bytes = Encoding.UTF8.GetBytes(value);

            WriteInt32(bytes.Length);

            _stream.Write(bytes);

        }

        internal void WriteInt32(int value)
        {

            Span<byte> bytes = stackalloc byte[sizeof(int)];

            BinaryPrimitives.WriteInt32BigEndian(bytes, value);

            _stream.Write(bytes);

        }

        public void Dispose()
        {

            _stream.Dispose();

        }

    }

}
