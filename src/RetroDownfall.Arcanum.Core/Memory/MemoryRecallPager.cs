using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>Deterministic bounded pages over an explicitly bounded, freshly authorized candidate snapshot.</summary>
public static class MemoryRecallPager
{
    public const int MaximumCandidatesPerSource = 100;

    public const int MaximumCandidateBytes = 65_536;

    private static readonly byte[] CursorKey = RandomNumberGenerator.GetBytes(32);

    private static readonly Error Invalid = new("MemoryRecall.InvalidRequest", "The recall request is invalid.");

    private static readonly Error Stale = new("MemoryRecall.InvalidCursor", "The recall continuation is invalid or stale.");

    private static readonly Error Oversized = new("MemoryRecall.OutputLimit", "The recall page cannot fit the response budget.");

    public static Result Validate(MemoryRecallRequest request) =>
        request is not null
        && !string.IsNullOrWhiteSpace(request.Query)
        && request.Query.Length <= 512
        && Enum.IsDefined(request.Scope)
        && request.Limit is >= 1 and <= 20
        && (request.Cursor is null || request.Cursor.Length is > 0 and <= 2048)
            ? Result.Success()
            : Invalid;

    public static Result<MemoryRecallPage> CreatePage(
        MemoryRecallRequest request,
        CanonicalCampaignContext canonical,
        MemoryScope memoryScope,
        IReadOnlyList<MemoryRecallFragment> candidates,
        int outputCapBytes,
        bool candidateLimitReached = false)
    {
        if (Validate(request).IsFailure || candidates is null || candidates.Count > 360 || outputCapBytes is < 64 or > 67_108_864)
        {
            return Invalid;
        }

        try
        {
            if (memoryScope.Kind == MemoryScopeKind.Campaign && memoryScope.CampaignId != canonical.CampaignId)
            {
                return Invalid;
            }

            foreach (MemoryRecallFragment fragment in candidates)
            {
                if (fragment.Source is not (MemoryRecallScope.Saga or MemoryRecallScope.Lexicon or MemoryRecallScope.Covenant)
                    || string.IsNullOrWhiteSpace(fragment.Id) || fragment.Id.Length > 512
                    || fragment.Content is null || Encoding.UTF8.GetByteCount(fragment.Content) > MaximumCandidateBytes
                    || fragment.Provenance is null || Encoding.UTF8.GetByteCount(fragment.Provenance) > 2048
                    || fragment.Similarity is { } score && !float.IsFinite(score))
                {
                    return Invalid;
                }
            }

            MemoryRecallFragment[] rows = candidates
                .Where(row => request.Scope == MemoryRecallScope.All || row.Source == request.Scope)
                .OrderBy(static row => row.Source)
                .ThenByDescending(static row => row.Similarity)
                .ThenBy(static row => row.Id, StringComparer.Ordinal)
                .ToArray();

            if (rows.GroupBy(static row => (row.Source, row.Id)).Any(static group => group.Count() > 1)
                || rows.GroupBy(static row => row.Source).Any(static group => group.Count() > (group.Key == MemoryRecallScope.Covenant ? 160 : MaximumCandidatesPerSource)))
            {
                return Invalid;
            }

            byte[] binding = Fingerprint(request, canonical, memoryScope, rows, candidateLimitReached);

            int offset = 0;

            if (request.Cursor is { } cursor && !TryReadCursor(cursor, binding, rows.Length, out offset))
            {
                return Stale;
            }

            List<MemoryRecallFragment> items = new(request.Limit);

            MemoryRecallPage page = new(items, null, candidateLimitReached);

            for (int index = offset; index < rows.Length && items.Count < request.Limit; index++)
            {
                items.Add(rows[index]);

                string? next = index + 1 < rows.Length ? WriteCursor(index + 1, binding) : null;

                MemoryRecallPage candidate = new(items, next, candidateLimitReached);

                if (!Fits(candidate, outputCapBytes))
                {
                    items.RemoveAt(items.Count - 1);

                    if (items.Count == 0)
                    {
                        return Oversized;
                    }

                    break;
                }

                page = candidate;
            }

            return Fits(page, outputCapBytes) ? page : Oversized;
        }
        catch (InvalidOperationException)
        {
            return Invalid;
        }
    }

    private static bool Fits(MemoryRecallPage page, int budget)
    {
        using ByteCountingStream stream = new();

        JsonSerializer.Serialize(stream, page, MemoryRecallJsonContext.Default.MemoryRecallPage);

        return stream.Length <= budget;
    }

    private static byte[] Fingerprint(MemoryRecallRequest request, CanonicalCampaignContext canonical,
        MemoryScope scope, IReadOnlyList<MemoryRecallFragment> rows, bool candidateLimitReached)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        void Add(string? text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);

            Span<byte> length = stackalloc byte[4];

            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);

            hash.AppendData(length);

            hash.AppendData(bytes);
        }

        Add(request.Query);

        Add(request.Scope.ToString());

        Add(request.Limit.ToString(CultureInfo.InvariantCulture));

        Add(canonical.CampaignId?.ToString("D"));

        Add(canonical.CampaignAvailabilityGeneration?.ToString(CultureInfo.InvariantCulture));

        Add(canonical.PathIdentityPolicyVersion?.ToString(CultureInfo.InvariantCulture));

        Add(canonical.PathIdentityRevision?.ToString(CultureInfo.InvariantCulture));

        Add(canonical.RootIdentityDigest is { } digest ? Convert.ToHexString(digest.Bytes) : null);

        Add(scope.Kind.ToString());

        Add(scope.CampaignId?.ToString("D"));

        Add(candidateLimitReached ? "1" : "0");

        foreach (MemoryRecallFragment row in rows)
        {
            Add(row.Source.ToString());

            Add(row.Id);

            Add(row.Content);

            Add(row.Similarity?.ToString("R", CultureInfo.InvariantCulture));

            Add(row.CampaignId?.ToString("D"));

            Add(row.VersionId?.ToString("D"));

            Add(row.Provenance);
        }

        return hash.GetHashAndReset();
    }

    private static string WriteCursor(int offset, byte[] binding)
    {
        byte[] payload = new byte[68];

        BinaryPrimitives.WriteInt32LittleEndian(payload, offset);

        binding.CopyTo(payload, 4);

        HMACSHA256.HashData(CursorKey, payload.AsSpan(0, 36)).CopyTo(payload, 36);

        return Convert.ToBase64String(payload);
    }

    private static bool TryReadCursor(string cursor, byte[] binding, int count, out int offset)
    {
        offset = 0;

        Span<byte> payload = stackalloc byte[68];

        if (!Convert.TryFromBase64String(cursor, payload, out int written) || written != payload.Length
            || !CryptographicOperations.FixedTimeEquals(payload.Slice(4, 32), binding)
            || !CryptographicOperations.FixedTimeEquals(payload[36..], HMACSHA256.HashData(CursorKey, payload[..36])))
        {
            return false;
        }

        offset = BinaryPrimitives.ReadInt32LittleEndian(payload);

        return offset > 0 && offset < count;
    }

    private sealed class ByteCountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => _length += count;

        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MemoryRecallPage))]
internal partial class MemoryRecallJsonContext : JsonSerializerContext;
