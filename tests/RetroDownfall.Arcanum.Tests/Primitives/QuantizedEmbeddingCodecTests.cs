using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Primitives;

public sealed class QuantizedEmbeddingCodecTests
{
    [Fact]
    public void UpperBound_ValidRepresentationSwappedBetweenOwners_RequestsAuthoritativeFallback()
    {
        float[] vector = [1f, 0.25f, -0.3f];

        byte[] representation = Assert.IsType<byte[]>(QuantizedEmbeddingCodec.Encode(vector, "owner-a"u8));

        QuantizedEmbeddingCodec.PreparedQuery query = QuantizedEmbeddingCodec.PrepareQuery(vector);

        Assert.True(QuantizedEmbeddingCodec.TryGetUpperBound(representation, query, out _, "owner-a"u8));

        Assert.False(QuantizedEmbeddingCodec.TryGetUpperBound(representation, query, out _, "owner-b"u8));

        Assert.False(QuantizedEmbeddingCodec.TryGetUpperBound(representation, query, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(65)]
    [InlineData(768)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void UpperBound_EnclosesExhaustiveFloatScore_ForRandomAndAlignedVectors(int dimensions)
    {
        Random random = new(173 + dimensions);

        for (int sample = 0; sample < 100; sample++)
        {
            float[] candidate = MakeVector(random, dimensions);

            float[] query = sample % 3 == 0 ? candidate : MakeVector(random, dimensions);

            if (sample % 5 == 0)
            {
                query = candidate.Select(value => -value).ToArray();
            }

            byte[] representation = Assert.IsType<byte[]>(QuantizedEmbeddingCodec.Encode(candidate));

            Assert.True(QuantizedEmbeddingCodec.TryGetUpperBound(representation, QuantizedEmbeddingCodec.PrepareQuery(query), out float upper));

            Assert.True(EmbeddingBlobCodec.TryCosineSimilarity(query, EmbeddingBlobCodec.NormSquared(query), candidate, out float exact));

            Assert.True(upper >= exact, $"Upper {upper:R} excludes exhaustive {exact:R}, dimensions {dimensions}, sample {sample}.");

            Assert.True(upper - exact < 0.05f);
        }
    }

    [Fact]
    public void UpperBound_CorruptionAtEveryByte_RequestsAuthoritativeFallback()
    {
        float[] vector = MakeVector(new Random(42), 768);

        byte[] representation = Assert.IsType<byte[]>(QuantizedEmbeddingCodec.Encode(vector));

        QuantizedEmbeddingCodec.PreparedQuery query = QuantizedEmbeddingCodec.PrepareQuery(vector);

        for (int index = 0; index < representation.Length; index++)
        {
            representation[index] ^= 1;

            Assert.False(QuantizedEmbeddingCodec.TryGetUpperBound(representation, query, out _));

            representation[index] ^= 1;
        }

        Assert.False(QuantizedEmbeddingCodec.TryGetUpperBound(representation.AsSpan(1), query, out _));

        Assert.False(QuantizedEmbeddingCodec.TryGetUpperBound(representation, QuantizedEmbeddingCodec.PrepareQuery([1f]), out _));

        Assert.False(QuantizedEmbeddingCodec.TryGetUpperBound(representation, default, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    public void UpperBound_UnsupportedSignedByteInBatchOrTail_RequestsAuthoritativeFallback(int component)
    {
        float[] vector = MakeVector(new Random(173), 33);

        byte[] representation = Assert.IsType<byte[]>(QuantizedEmbeddingCodec.Encode(vector));

        representation[56 + component] = unchecked((byte)sbyte.MinValue);

        // A self-consistent checksum cannot make an out-of-format integer safe for the
        // prepared allowance, which deliberately relies on every magnitude being <=127.
        _ = SHA256.HashData(representation.AsSpan(0, representation.Length - 32), representation.AsSpan(representation.Length - 32));

        Assert.False(QuantizedEmbeddingCodec.TryGetUpperBound(representation, QuantizedEmbeddingCodec.PrepareQuery(vector), out _));
    }

    [Fact]
    public void ExceptionalVectors_RequestAuthoritativeFallback()
    {
        float[][] vectors = [[], [0f], [float.NaN], [float.PositiveInfinity], [float.Epsilon], [float.MaxValue], [1e-30f, 1f]];

        foreach (float[] vector in vectors)
        {
            Assert.Null(QuantizedEmbeddingCodec.Encode(vector));

            Assert.False(QuantizedEmbeddingCodec.PrepareQuery(vector).IsSafe);
        }
    }

    [Theory]
    [InlineData(-50)]
    [InlineData(50)]
    public void UpperBound_EnclosesExhaustiveScore_AtSafeMagnitudeExtremes(int exponent)
    {
        float magnitude = MathF.ScaleB(1f, exponent);

        float[] candidate = Enumerable.Range(0, 768).Select(index => index % 2 == 0 ? magnitude : -magnitude).ToArray();

        float[] query = Enumerable.Repeat(magnitude, 768).ToArray();

        byte[] representation = Assert.IsType<byte[]>(QuantizedEmbeddingCodec.Encode(candidate));

        Assert.True(QuantizedEmbeddingCodec.TryGetUpperBound(representation, QuantizedEmbeddingCodec.PrepareQuery(query), out float upper));

        Assert.True(upper >= EmbeddingBlobCodec.CosineSimilarity(query, candidate));
    }

    [Fact]
    public void UpperBound_ResidualProtectsAComponentRoundedAwayByQuantization()
    {
        float[] candidate = [1f, 0.003f];

        float[] query = [0f, 1f];

        byte[] representation = Assert.IsType<byte[]>(QuantizedEmbeddingCodec.Encode(candidate));

        Assert.True(QuantizedEmbeddingCodec.TryGetUpperBound(representation, QuantizedEmbeddingCodec.PrepareQuery(query), out float upper));

        // Quantization rounds the second component to zero. Without its residual envelope, this
        // actual above-threshold match would disappear even though the float-rounding margin remains.
        Assert.True(EmbeddingBlobCodec.CosineSimilarity(query, candidate) > 0.0029f);

        Assert.True(upper > 0.0029f);

        Assert.True(upper < 0.004f);
    }

    [Fact]
    public void UpperBound_AllocatesNoManagedMemoryPerCandidate()
    {
        float[] vector = MakeVector(new Random(42), 768);

        byte[] representation = Assert.IsType<byte[]>(QuantizedEmbeddingCodec.Encode(vector));

        QuantizedEmbeddingCodec.PreparedQuery query = QuantizedEmbeddingCodec.PrepareQuery(vector);

        for (int index = 0; index < 100; index++)
        {
            _ = QuantizedEmbeddingCodec.TryGetUpperBound(representation, query, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < 1_000; index++)
        {
            _ = QuantizedEmbeddingCodec.TryGetUpperBound(representation, query, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void PreparedQuery_RetainsAtMostOneFloatVectorOfManagedStorage()
    {
        float[] vector = MakeVector(new Random(173), 4096);

        _ = QuantizedEmbeddingCodec.PrepareQuery(vector);

        long before = GC.GetAllocatedBytesForCurrentThread();

        QuantizedEmbeddingCodec.PreparedQuery query = QuantizedEmbeddingCodec.PrepareQuery(vector);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(query.IsSafe);

        Assert.InRange(allocated, 0, vector.Length * sizeof(float) + 64);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(768)]
    [InlineData(65536)]
    public void UpperBound_EnclosesCancellationAcrossBatchesWithMixedMagnitudeSigns(int dimensions)
    {
        float[] candidate = new float[dimensions];

        float[] vector = new float[dimensions];

        for (int index = 0; index < dimensions; index++)
        {
            float magnitude = MathF.ScaleB(1f, index % 3 == 0 ? -50 : index % 3 == 1 ? 0 : 50);

            candidate[index] = (index % 4 < 2 ? 1f : -1f) * magnitude;

            vector[index] = (index % 2 == 0 ? 1f : -1f) * magnitude;
        }

        byte[] representation = Assert.IsType<byte[]>(QuantizedEmbeddingCodec.Encode(candidate));

        QuantizedEmbeddingCodec.PreparedQuery query = QuantizedEmbeddingCodec.PrepareQuery(vector);

        Assert.True(QuantizedEmbeddingCodec.TryGetUpperBound(representation, query, out float upper));

        Assert.True(EmbeddingBlobCodec.TryCosineSimilarity(vector, EmbeddingBlobCodec.NormSquared(vector), candidate, out float exact));

        Assert.True(upper >= exact, $"Upper {upper:R} excludes exhaustive {exact:R}, dimensions {dimensions}.");

        Assert.True(upper - exact < 0.05f);
    }

    private static float[] MakeVector(Random random, int dimensions) =>
        Enumerable.Range(0, dimensions).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
}
