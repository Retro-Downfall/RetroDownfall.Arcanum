using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// Binds two already-computed Covenant digests into one.
/// </summary>
/// <remarks>
/// Not a new canonical domain, and deliberately not one. Every Covenant canonical preimage begins
/// with a domain tag and length-prefixes its variable fields, so a bare pair of fixed 32-byte digests
/// cannot be confused with any of them, and two fixed-width operands need no length prefix to be
/// unambiguous. Used where a value has to identify "these two facts together" without earning a place
/// in the pinned policy-v1 domain set (§10.14).
/// </remarks>
public static class CovenantDigestPair
{

    public static CovenantDigest Combine(CovenantDigest first, CovenantDigest second)
    {

        CovenantValidation.RequireDigest(first, nameof(first));

        CovenantValidation.RequireDigest(second, nameof(second));

        Span<byte> preimage = stackalloc byte[CovenantLimits.DigestBytes * 2];

        first.Span.CopyTo(preimage);

        second.Span.CopyTo(preimage[CovenantLimits.DigestBytes..]);

        return new CovenantDigest(SHA256.HashData(preimage));

    }

}
