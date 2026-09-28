using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Memory;

public static class MemoryReviewLimits
{
    public const int MaxPageSize = 50;

    public const int MaxBulkOperations = 50;

    public const int MaxTokenCharacters = 256;

    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryReviewStore>))]
public enum MemoryReviewStore : byte
{
    Covenant = 1,

    Saga = 2,

    Lexicon = 3,
}

public readonly record struct MemoryReviewDigest
{
    public const int Size = 32;

    private readonly byte[]? _bytes;

    public MemoryReviewDigest(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A memory-review digest must contain exactly {Size} bytes.", nameof(bytes));
        }

        _bytes = bytes.ToArray();
    }

    public byte[] Bytes => _bytes?.ToArray() ?? [];

    public bool IsValid => _bytes is { Length: Size };

    public void CopyTo(Span<byte> destination)
    {
        if (!IsValid || destination.Length < Size)
        {
            throw new ArgumentException("A valid memory-review digest and a 32-byte destination are required.", nameof(destination));
        }

        _bytes.AsSpan().CopyTo(destination);
    }

    public bool Equals(MemoryReviewDigest other) =>
        (_bytes ?? []).AsSpan().SequenceEqual(other._bytes ?? []);

    public override int GetHashCode()
    {
        HashCode hash = new();

        foreach (byte value in _bytes ?? [])
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

public sealed record MemoryReviewCursorTokenFacts(
    MemoryReviewStore Store,
    MemoryReviewDigest CanonicalScopeDigest,
    Guid MarkerGeneration,
    ulong MarkerRevision,
    ulong FrozenLowerEventSequence,
    ulong FrozenUpperEventSequence,
    ulong KeysetEventSequence,
    MemoryReviewDigest KeysetVersionIdentity);

public sealed record MemoryReviewObservationTokenFacts(
    MemoryReviewStore Store,
    MemoryReviewDigest CanonicalScopeDigest,
    Guid MarkerGeneration,
    ulong MarkerRevision,
    ulong FrozenLowerEventSequence,
    ulong FrozenUpperEventSequence,
    ulong EventSequence,
    MemoryReviewDigest VersionIdentity);

public sealed record MemoryReviewPreparedPlanTokenFacts(
    MemoryReviewStore Store,
    MemoryReviewDigest CanonicalScopeDigest,
    Guid MarkerGeneration,
    ulong MarkerRevision,
    ulong FrozenLowerEventSequence,
    ulong FrozenUpperEventSequence,
    MemoryReviewDigest OrderedRequestDigest);
