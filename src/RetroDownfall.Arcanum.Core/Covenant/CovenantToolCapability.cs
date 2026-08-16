using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The exact internal tool names the Covenant mutation surface owns.
/// </summary>
/// <remarks>
/// Named here rather than restated at each seam. Classification, Ward policy, tool filtering, and the
/// handler registry all have to agree on these two strings, and a second literal is a second place a
/// rename can silently stop matching.
/// </remarks>
public static class CovenantToolNames
{

    public const string ProposeCovenant = "propose_covenant";

    public const string RetireCovenant = "retire_covenant";

    /// <summary>Whether <paramref name="toolName"/> is one of the two Covenant mutation tools.</summary>
    /// <remarks>
    /// Ordinal and exact. A case-insensitive or prefix match would let a differently cased name reach
    /// an ordinary tool path while still resolving to a Covenant handler.
    /// </remarks>
    public static bool IsCovenantMutationTool(string? toolName) =>
        string.Equals(toolName, ProposeCovenant, StringComparison.Ordinal)
        || string.Equals(toolName, RetireCovenant, StringComparison.Ordinal);

}

/// <summary>
/// The linearizable lifecycle of one registered Covenant tool capability.
/// </summary>
/// <remarks>
/// <see cref="Closing"/> exists so a use that suspended across an <c>await</c> has a state to fail
/// against. Without it, disposal would either have to block the whole handler or let a resumed
/// continuation act on a capability whose turn had already gone.
/// </remarks>
public enum CovenantToolCapabilityState : byte
{

    Registered = 1,

    Taken = 2,

    Closing = 3,

    Disposed = 4,

}

/// <summary>
/// The random 128-bit secret that binds one capability to one MCP request.
/// </summary>
/// <remarks>
/// The connection-and-request-id pair alone is guessable and reusable: request ids repeat across a
/// long-lived connection, so a delayed or replayed call could otherwise reach the registration a
/// later request installed under the same id. The nonce makes that reuse detectable, which is what
/// closes the ABA window (§10.14).
/// </remarks>
public readonly struct CovenantToolCapabilityNonce : IEquatable<CovenantToolCapabilityNonce>
{

    private const int Bytes = 16;

    private readonly byte[]? _value;

    private CovenantToolCapabilityNonce(byte[] value) =>
        _value = value;

    /// <summary>Mints one fresh capability nonce from the cryptographic random source.</summary>
    public static CovenantToolCapabilityNonce Create() =>
        new(RandomNumberGenerator.GetBytes(Bytes));

    /// <summary>Whether this value carries real random material rather than being defaulted.</summary>
    public bool IsValid => _value is { Length: Bytes };

    /// <summary>
    /// The non-secret digest a disclosure receipt records in place of the nonce itself.
    /// </summary>
    public CovenantDigest ToDigest() =>
        _value is { Length: Bytes } value
            ? new CovenantDigest(SHA256.HashData(value))
            : throw new InvalidOperationException("An uninitialized capability nonce has no digest.");

    /// <summary>
    /// Fixed-time comparison. An early-exit compare would leak the nonce one byte at a time to a
    /// caller that can retry.
    /// </summary>
    public bool Equals(CovenantToolCapabilityNonce other) =>
        _value is { Length: Bytes } mine
        && other._value is { Length: Bytes } theirs
        && CryptographicOperations.FixedTimeEquals(mine, theirs);

    public override bool Equals(object? obj) =>
        obj is CovenantToolCapabilityNonce other && Equals(other);

    /// <summary>
    /// Deliberately weak and prefix-free: a nonce is never a dictionary key here, and a hash derived
    /// from the secret is a smaller version of the secret.
    /// </summary>
    public override int GetHashCode() =>
        IsValid ? 1 : 0;

    /// <summary>Never prints the material. A capability that appears in a log is a capability.</summary>
    public override string ToString() =>
        IsValid ? "[covenant capability nonce]" : "[uninitialized capability nonce]";

    public static bool operator ==(CovenantToolCapabilityNonce left, CovenantToolCapabilityNonce right) =>
        left.Equals(right);

    public static bool operator !=(CovenantToolCapabilityNonce left, CovenantToolCapabilityNonce right) =>
        !left.Equals(right);

}
