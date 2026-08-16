namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// A one-way latch recording that this process has reached Covenant material.
/// </summary>
/// <remarks>
/// The offline host-tools transition may only run in a process that has never opened Covenant.
/// Decrypted canonical pages, pooled handles, prompt strings, and managed heap copies all outlive
/// the call that produced them, and the whole point of the transition is that arbitrary
/// same-identity code is about to become able to inspect this process. Asking "did we ever open it?"
/// is the only question that is safe to answer, because "did we close it?" cannot be proven
/// (§10.12).
///
/// <para>Process-wide static rather than a registered service, deliberately: a container-scoped
/// latch would answer <see langword="false"/> in a second container built inside the same process,
/// which is exactly the shape an offline command takes.</para>
/// </remarks>
internal static class CovenantProcessResidence
{

    private static int _opened;

    /// <summary>Whether any Covenant canonical connection or key derivation has happened here.</summary>
    internal static bool HasOpened => Volatile.Read(ref _opened) != 0;

    /// <summary>Latches the process as having held Covenant material. Never reversible.</summary>
    internal static void MarkOpened() => Volatile.Write(ref _opened, 1);

}
