using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal readonly record struct ToolLoopProgressEntry(
    string ToolName,
    string ArgumentsJson,
    string ResultText);

/// <summary>
/// Detects a completed tool round that produced exactly the same calls and results as the
/// immediately preceding round. It retains one fixed-size fingerprint, so productive loops can
/// continue without a round counter or growing history.
/// </summary>
internal sealed class ToolLoopProgressDetector
{

    private byte[]? _previousFingerprint;

    public bool ObserveCompletedRound(
        IReadOnlyList<ToolLoopProgressEntry> entries)
    {

        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {

            throw new ArgumentException(
                "A completed tool round must contain at least one entry.",
                nameof(entries));

        }

        byte[] fingerprint = ComputeFingerprint(entries);

        bool repeated = _previousFingerprint is not null
            && CryptographicOperations.FixedTimeEquals(
                _previousFingerprint,
                fingerprint);

        _previousFingerprint = fingerprint;

        return repeated;

    }

    private static byte[] ComputeFingerprint(
        IReadOnlyList<ToolLoopProgressEntry> entries)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);

        AppendInt32(hash, entries.Count);

        foreach (ToolLoopProgressEntry entry in entries)
        {

            AppendString(hash, entry.ToolName);

            AppendString(hash, entry.ArgumentsJson);

            AppendString(hash, entry.ResultText);

        }

        return hash.GetHashAndReset();

    }

    private static void AppendString(
        IncrementalHash hash,
        string value)
    {

        byte[] bytes = Encoding.UTF8.GetBytes(value);

        AppendInt32(hash, bytes.Length);

        hash.AppendData(bytes);

    }

    private static void AppendInt32(
        IncrementalHash hash,
        int value)
    {

        Span<byte> bytes = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);

        hash.AppendData(bytes);

    }

}
