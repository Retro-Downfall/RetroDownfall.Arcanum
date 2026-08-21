using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Projects the active <see cref="CovenantAuthoritySnapshot"/> from the process-wide runtime generation.
/// </summary>
/// <remarks>
/// The composite holder swaps keys, authority, and availability together, so this facade cannot
/// publish or withdraw one member independently. A retired runtime generation deliberately projects
/// null here even though the exact recovery owner may still use retained counters inside the holder.
/// </remarks>
internal sealed class CovenantAuthoritySnapshotProvider(
    CovenantRuntimeGenerationProvider runtime) : ICovenantAuthoritySnapshotProvider
{

    private readonly CovenantRuntimeGenerationProvider _runtime =
        runtime ?? throw new ArgumentNullException(nameof(runtime));

    public CovenantAuthoritySnapshot? Current => _runtime.Current.ActiveAuthority;

}
