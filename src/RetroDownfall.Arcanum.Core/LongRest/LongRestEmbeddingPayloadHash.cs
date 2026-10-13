using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>Canonical single-input embedding request; live work and destination remain separate fences.</summary>
public static class LongRestEmbeddingPayloadHash
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Result<CovenantDigest> Create(string providerIdentity, string modelIdentity, int dimensions, string input)
    {
        try
        {
            if (dimensions <= 0 || !Valid(providerIdentity, 1024) || !Valid(modelIdentity, 1024)
                || !Valid(input, LongRestDiscoveryLimits.ProviderInputUtf8Bytes))
            {
                return Invalid();
            }

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            hash.AppendData("Arcanum.LongRest.EmbeddingPayload.v1\0"u8);

            Integer(1);

            Text(providerIdentity);

            Text(modelIdentity);

            Integer(dimensions);

            Integer(1);

            Text(input);

            return new CovenantDigest(hash.GetHashAndReset());

            void Integer(int value)
            {
                Span<byte> bytes = stackalloc byte[4];

                BinaryPrimitives.WriteInt32BigEndian(bytes, value);

                hash.AppendData(bytes);
            }

            void Text(string value)
            {
                byte[] bytes = StrictUtf8.GetBytes(value);

                Integer(bytes.Length);

                hash.AppendData(bytes);
            }
        }
        catch (EncoderFallbackException)
        {
            return Invalid();
        }
    }

    private static bool Valid(string value, int bound) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= bound && StrictUtf8.GetByteCount(value) <= bound;

    private static Result<CovenantDigest> Invalid() => new Error(ErrorCodes.LongRest.InvalidRequest,
        "An embedding request requires exact bounded UTF-8 identities, positive dimensions and one bounded input.");
}
