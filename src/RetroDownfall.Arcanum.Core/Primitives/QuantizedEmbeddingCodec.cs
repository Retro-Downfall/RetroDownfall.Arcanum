using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// A disposable int8 search companion. It supplies an upper bound only; final scores must always
/// come from <see cref="EmbeddingBlobCodec.TryCosineSimilarity"/> over the authoritative float BLOB.
/// </summary>
public static class QuantizedEmbeddingCodec
{
    private const int HeaderBytes = 56;

    private const int DigestBytes = 32;

    public const int MaximumDimensions = 65_536;

    public const int RepresentationOverheadBytes = HeaderBytes + DigestBytes;

    private const uint Format = 0x31514341; // ACQ1, little endian.

    private const double FloatUnitRoundoff = 1d / (1 << 24);

    private const double DoubleUnitRoundoff = 1d / (1L << 53);

    private const double MinimumMagnitude = 1d / (1L << 50);

    private const double MaximumMagnitude = 1L << 50;

    public readonly struct PreparedQuery
    {
        internal PreparedQuery(float[] vector, double normLower, double normUpper, double dotError, double referenceError)
        {
            Vector = vector;

            NormLower = normLower;

            NormUpper = normUpper;

            DotError = dotError;

            ReferenceError = referenceError;
        }

        internal float[]? Vector { get; }

        internal double NormLower { get; }

        internal double NormUpper { get; }

        internal double DotError { get; }

        internal double ReferenceError { get; }

        public bool IsSafe => Vector is not null;
    }

    public static byte[]? Encode(ReadOnlySpan<float> vector, ReadOnlySpan<byte> ownerIdentity = default)
    {
        if (!TryNormInterval(vector, out double normLower, out double normUpper, out double maximum))
        {
            return null;
        }

        byte[] representation = new byte[HeaderBytes + vector.Length + DigestBytes];

        Span<byte> payload = representation.AsSpan(0, HeaderBytes + vector.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(payload, Format);

        BinaryPrimitives.WriteInt32LittleEndian(payload[4..], vector.Length);

        _ = SHA256.HashData(ownerIdentity, payload.Slice(24, DigestBytes));

        double scale = maximum / normUpper / 127d;

        BinaryPrimitives.WriteDoubleLittleEndian(payload[8..], scale);

        double residualSquared = 0;

        for (int index = 0; index < vector.Length; index++)
        {
            sbyte quantized = (sbyte)Math.Clamp(Math.Round(vector[index] / normUpper / scale), -127d, 127d);

            payload[HeaderBytes + index] = unchecked((byte)quantized);

            NormalizedInterval(vector[index], normLower, normUpper, out double lower, out double upper);

            double reconstructed = scale * quantized;

            double residual = Math.Max(Up(upper - Down(reconstructed)), Up(Up(reconstructed) - lower));

            residualSquared = Up(residualSquared + Up(residual * residual));
        }

        BinaryPrimitives.WriteDoubleLittleEndian(payload[16..], Up(Math.Sqrt(residualSquared)));

        _ = SHA256.HashData(payload, representation.AsSpan(payload.Length));

        return representation;
    }

    public static PreparedQuery PrepareQuery(ReadOnlySpan<float> vector)
    {
        if (!TryNormInterval(vector, out double normLower, out double normUpper, out _))
        {
            return default;
        }

        double absoluteSum = 0;

        for (int index = 0; index < vector.Length; index++)
        {
            absoluteSum = Up(absoluteSum + Math.Abs((double)vector[index]));
        }

        // Each SIMD dot has at most 2w binary32 operations, followed by at most d+4
        // binary64 accumulations across blocks and the scalar tail. Their composed error is
        // gf+gd+gf*gd times the absolute exact sum, bounded by 127*sum(abs(query)).
        // The raw query avoids per-coordinate normalization: every admitted component is a
        // multiple of 2^-73, so integer products and all float partial sums have no underflow;
        // even the full absolute sum is below d*127*2^50 < 2^73, precluding overflow.
        // Scalar-tail products are exact in binary64 (at most 24+7 significand bits).
        double floatError = Vector.IsHardwareAccelerated ? Gamma(2d * Vector<float>.Count, FloatUnitRoundoff) : 0;

        double doubleError = Gamma(vector.Length + 4d, DoubleUnitRoundoff);

        double combinedError = Up(Up(floatError + doubleError) + Up(floatError * doubleError));

        double dotError = Up(Up(127d * absoluteSum) * combinedError);

        // The reference is NOT exact-real cosine: Vector<float>.Dot rounds both products and
        // horizontal sums to binary32 before accumulating blocks in binary64. Regardless of SIMD
        // width/reduction order, at most 2d operations affect a dot/norm. With u=2^-24 and
        // gamma_n=nu/(1-nu), their absolute/relative error is bounded by delta=gamma_(2d+32).
        // The 32-operation allowance covers the binary64 accumulation, sqrt/divide, final float
        // conversion, and gradual-underflow absolute error: d*2^-150/(||a||||b||) <= d*2^-50
        // under our magnitude guard, far smaller than the allowance. No product or partial sum
        // can overflow: d<=2^16 and |component|<=2^50 imply sums <=2^116.
        // If |Dhat-D|<=delta ||a||||b|| and norm errors <=delta, the denominator ratio lies in
        // [1-delta,1+delta]; hence |reference-cos(real)| <= 2delta/(1-delta).
        double operations = 2d * vector.Length + 32d;

        double delta = Gamma(operations, FloatUnitRoundoff);

        double referenceError = Up(Up(2d * delta) / Down(1d - delta));

        return new(vector.ToArray(), normLower, normUpper, dotError, referenceError);
    }

    public static bool TryGetUpperBound(ReadOnlySpan<byte> representation, PreparedQuery query, out float upper, ReadOnlySpan<byte> ownerIdentity = default)
    {
        upper = 0;

        if (!query.IsSafe || representation.Length < HeaderBytes + DigestBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(representation) != Format)
        {
            return false;
        }

        int dimensions = BinaryPrimitives.ReadInt32LittleEndian(representation[4..]);

        if (dimensions != query.Vector!.Length || representation.Length != HeaderBytes + dimensions + DigestBytes)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = representation[..^DigestBytes];

        Span<byte> digest = stackalloc byte[DigestBytes];

        _ = SHA256.HashData(payload, digest);

        if (!CryptographicOperations.FixedTimeEquals(digest, representation[^DigestBytes..]))
        {
            return false;
        }

        _ = SHA256.HashData(ownerIdentity, digest);

        if (!CryptographicOperations.FixedTimeEquals(digest, payload.Slice(24, DigestBytes)))
        {
            return false;
        }

        double scale = BinaryPrimitives.ReadDoubleLittleEndian(payload[8..]);

        double residual = BinaryPrimitives.ReadDoubleLittleEndian(payload[16..]);

        if (!double.IsFinite(scale) || scale <= 0 || scale > 1d
            || !double.IsFinite(residual) || residual < 0 || residual > 4d)
        {
            return false;
        }

        ReadOnlySpan<float> vector = query.Vector;

        ReadOnlySpan<sbyte> quantized = MemoryMarshal.Cast<byte, sbyte>(payload[HeaderBytes..]);

        double dot = 0;

        int index = 0;

        if (Vector.IsHardwareAccelerated)
        {
            int width = Vector<float>.Count;

            for (; index <= dimensions - Vector<sbyte>.Count; index += Vector<sbyte>.Count)
            {
                Vector<sbyte> packed = new(quantized.Slice(index, Vector<sbyte>.Count));

                if (Vector.EqualsAny(packed, new Vector<sbyte>(sbyte.MinValue)))
                {
                    return false;
                }

                Vector.Widen(packed, out Vector<short> low, out Vector<short> high);

                Vector.Widen(low, out Vector<int> first, out Vector<int> second);

                Vector.Widen(high, out Vector<int> third, out Vector<int> fourth);

                dot += Vector.Dot(new Vector<float>(vector.Slice(index, width)), Vector.ConvertToSingle(first));

                dot += Vector.Dot(new Vector<float>(vector.Slice(index + width, width)), Vector.ConvertToSingle(second));

                dot += Vector.Dot(new Vector<float>(vector.Slice(index + 2 * width, width)), Vector.ConvertToSingle(third));

                dot += Vector.Dot(new Vector<float>(vector.Slice(index + 3 * width, width)), Vector.ConvertToSingle(fourth));
            }
        }

        for (; index < dimensions; index++)
        {
            if (quantized[index] == sbyte.MinValue)
            {
                return false;
            }

            dot += (double)vector[index] * quantized[index];
        }

        // Let u=a/||a||, v=b/||b||, r=scale*int8, and E>=||v-r||. Cauchy-Schwarz
        // gives u.v <= u.r + E. The prepared allowance encloses the raw SIMD dot;
        // dividing its upper endpoint uses the upper norm for a negative numerator and the
        // lower norm otherwise. The positive scale preserves that bound for either sign.
        // Finally E and the independent reference-scorer allowance enclose the shipped score.
        double rawUpper = Up(dot + query.DotError);

        double dotUpper = Up(Up(rawUpper / (rawUpper < 0 ? query.NormUpper : query.NormLower)) * scale);

        double bound = Up(Up(dotUpper + residual) + query.ReferenceError);

        float rounded = (float)bound;

        upper = rounded < bound ? float.BitIncrement(rounded) : rounded;

        // The reference can round slightly above one. Clamping this bound to one is unsafe.
        return float.IsFinite(upper);
    }

    private static bool TryNormInterval(ReadOnlySpan<float> vector, out double lower, out double upper, out double maximum)
    {
        lower = 0;

        upper = 0;

        maximum = 0;

        if (vector.Length == 0 || vector.Length > MaximumDimensions)
        {
            return false;
        }

        foreach (float value in vector)
        {
            double magnitude = Math.Abs((double)value);

            if (!double.IsFinite(magnitude) || magnitude > MaximumMagnitude
                || (magnitude != 0 && magnitude < MinimumMagnitude))
            {
                return false;
            }

            maximum = Math.Max(maximum, magnitude);

            // Binary32 squared is exact in binary64 (at most 48 significand bits).
            double squared = (double)value * value;

            lower = Math.Max(0, Down(lower + squared));

            upper = Up(upper + squared);
        }

        if (maximum == 0)
        {
            return false;
        }

        lower = Down(Math.Sqrt(lower));

        upper = Up(Math.Sqrt(upper));

        return lower > 0;
    }

    private static void NormalizedInterval(float value, double normLower, double normUpper, out double lower, out double upper)
    {
        lower = Down(value / (value < 0 ? normLower : normUpper));

        upper = Up(value / (value < 0 ? normUpper : normLower));
    }

    private static double Up(double value) => Math.BitIncrement(value);

    private static double Down(double value) => Math.BitDecrement(value);

    private static double Gamma(double operations, double unitRoundoff)
    {
        double product = Up(operations * unitRoundoff);

        return Up(product / Down(1d - product));
    }
}
