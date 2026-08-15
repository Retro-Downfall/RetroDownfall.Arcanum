namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// Read-only access to the authority facts of the running installation.
/// </summary>
/// <remarks>
/// Read-only on purpose. Authority is established once, under the installation lock, by the startup
/// path that owns the core install transaction; every other component consumes it. A publishing
/// method on this interface would let any injected caller assert an epoch it did not earn.
///
/// <para><see cref="Current"/> is <see langword="null"/> until authority has actually committed.
/// A consumer that finds null has learned something true and useful - authority is not established
/// yet - rather than being handed a default-shaped placeholder it would mistake for a clean
/// installation.</para>
/// </remarks>
public interface ICovenantAuthoritySnapshotProvider
{

    /// <summary>
    /// The committed authority snapshot, or <see langword="null"/> before one exists.
    /// </summary>
    CovenantAuthoritySnapshot? Current { get; }

}
