using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal readonly record struct ToolLoopProgressEntry(
    string ToolName,
    string ArgumentsJson,
    string ResultText);

/// <summary>
/// Detects a completed tool round that produced exactly the same calls and results as any of the
/// last <see cref="RecentRoundWindow"/> rounds. Comparing against a window rather than only the
/// adjacent round catches an oscillation (A, B, A, B …) that a confused model can otherwise sustain
/// forever — the shared tool loop has no round, duration, or model-call ceiling (DESIGN §2.1), so
/// this is the only structural stop. The window is a fixed-size ring of digests, so productive
/// loops still run unbounded on O(1) memory with no round counter.
/// </summary>
internal sealed class ToolLoopProgressDetector
{

    /// <summary>
    /// How many completed rounds the current round is compared against. Small and fixed so memory
    /// stays constant; wide enough to catch the two- and three-round oscillations models fall into.
    /// </summary>
    private const int RecentRoundWindow = 8;

    private readonly byte[]?[] _recentFingerprints = new byte[]?[RecentRoundWindow];

    private int _nextSlot;

    /// <summary>
    /// Hex digest of the round signature that most recently recurred, for trace/diagnostic capture
    /// of the <c>no_progress</c> stop (DESIGN §10.7.3). <see langword="null"/> until one recurs.
    /// </summary>
    public string? RepeatedSignature { get; private set; }

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

        bool repeated = false;

        foreach (byte[]? previous in _recentFingerprints)
        {

            if (previous is not null
                && CryptographicOperations.FixedTimeEquals(previous, fingerprint))
            {

                repeated = true;

                break;

            }

        }

        _recentFingerprints[_nextSlot] = fingerprint;

        _nextSlot = (_nextSlot + 1) % RecentRoundWindow;

        if (repeated)
        {

            RepeatedSignature = Convert.ToHexString(fingerprint.AsSpan(0, 8));

        }

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
