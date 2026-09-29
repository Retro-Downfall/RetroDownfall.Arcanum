namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>What the process knows about the installation's erasure key.</summary>
public enum MemoryErasureKeyState
{
    /// <summary>Nothing has asked the credential store yet.</summary>
    Unresolved = 1,

    /// <summary>The credential store proved the account empty.</summary>
    Absent = 2,

    Present = 3,

    /// <summary>The credential store could not answer, or a create did not read back as written.</summary>
    Unavailable = 4,

    /// <summary>The account holds something other than canonical key material. It is never overwritten.</summary>
    Malformed = 5,
}

/// <summary>Whether a caller accepts the latched answer or asks the credential store again.</summary>
public enum MemoryErasureKeyProbe
{
    /// <summary>For automatic callers: the store is asked at most once per process.</summary>
    UseLatched = 1,

    /// <summary>For operator calls: anything short of <see cref="MemoryErasureKeyState.Present"/> is asked again.</summary>
    Reprobe = 2,
}

/// <summary>
/// The answer to an open. <see cref="Key"/> is set only when <see cref="State"/> is
/// <see cref="MemoryErasureKeyState.Present"/>, and is a fresh copy the caller disposes.
/// </summary>
public sealed record MemoryErasureKeyOpenResult(MemoryErasureKeyState State, MemoryErasureKey? Key);

/// <summary>
/// The latched state and, when present, the key's identifier. Reading it never touches the credential
/// store.
/// </summary>
public sealed record MemoryErasureKeyLatch(MemoryErasureKeyState State, byte[]? KeyId);

/// <summary>
/// Reads the installation's erasure key without ever creating one.
/// </summary>
/// <remarks>
/// One process-wide latch backs every call. <see cref="Latch"/> and <see cref="TryCopyLatched"/> never
/// perform credential I/O and never wait for a probe another caller has in flight. Every key returned
/// is a private copy.
/// </remarks>
public interface IMemoryErasureKeyProvider
{
    MemoryErasureKeyLatch Latch { get; }

    MemoryErasureKeyOpenResult OpenExisting(MemoryErasureKeyProbe probe);

    MemoryErasureKey? TryCopyLatched();
}
