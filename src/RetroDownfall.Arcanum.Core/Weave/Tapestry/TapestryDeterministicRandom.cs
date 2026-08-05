using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry;

/// <summary>
/// The Tapestry's documented fixed PRNG: SplitMix64 (Steele/Lea/Flood, 2014). Chosen because it is
/// eight lines of pure integer arithmetic with no platform, JIT, or floating-point dependence, so the
/// same seed yields the same sequence on every runtime Arcanum ships on — unlike
/// <see cref="System.Random"/>, whose shared/seeded algorithms are explicitly not a stability
/// contract across .NET versions.
///
/// Used only for seeded K-Means++ initialization (see <see cref="SphericalKMeans"/>). It is
/// <b>not</b> a cryptographic primitive and must never be used for one.
/// </summary>
public sealed class TapestryDeterministicRandom(ulong seed)
{

    private ulong _state = seed;

    /// <summary>Advances the generator and returns the raw 64-bit output.</summary>
    public ulong NextUInt64()
    {

        _state = unchecked(_state + 0x9E3779B97F4A7C15UL);

        ulong z = _state;

        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);

        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);

        return z ^ (z >> 31);

    }

    /// <summary>
    /// A value in <c>[0, 1)</c>. The top 53 bits are scaled by 2^-53, the standard
    /// exactly-representable double conversion — no rounding can produce <c>1.0</c>.
    /// </summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

}

/// <summary>
/// Derives a Tapestry clustering seed from the versioned clustering-algorithm id plus the tree scope
/// and layer — never process-random state, never a wall clock. Rebuilding the same scope with the
/// same algorithm version therefore reproduces the same K-Means++ initialization, which is the
/// precondition for the reproducible-membership contract documented in DESIGN §21.11.
/// </summary>
public static class TapestrySeed
{

    /// <summary>
    /// SHA-256 over a canonical, unambiguously delimited descriptor, folded to 64 bits. SHA-256 is
    /// used purely as a stable, well-specified mixing function (its cryptographic strength is
    /// irrelevant here); <see cref="string.GetHashCode()"/> would be wrong because it is randomized
    /// per process.
    /// </summary>
    public static ulong Derive(
        string algorithmVersion,
        TapestryScopeKind scopeKind,
        string scopeId,
        int layer)
    {

        ArgumentNullException.ThrowIfNull(algorithmVersion);

        ArgumentNullException.ThrowIfNull(scopeId);

        // U+001F (unit separator) cannot appear in an algorithm version, scope kind, or normalized
        // scope id, so no two distinct descriptors can collide by concatenation.
        string descriptor = string.Join(
            '\u001f',
            algorithmVersion,
            scopeKind.ToString(),
            scopeId,
            layer.ToString(CultureInfo.InvariantCulture));

        Span<byte> digest = stackalloc byte[32];

        _ = SHA256.HashData(Encoding.UTF8.GetBytes(descriptor), digest);

        ulong seed = 0;

        for (int index = 0; index < 8; index++)
        {

            seed |= (ulong)digest[index] << (index * 8);

        }

        return seed;

    }

}
