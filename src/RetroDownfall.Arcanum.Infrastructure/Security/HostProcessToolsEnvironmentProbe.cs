using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Reads the edition, escape-hatch opt-in, and Covenant-residence facts from trusted process state.
/// </summary>
/// <remarks>
/// Every value comes from configuration this process already resolved or from a latch this process
/// set itself. None of them is reachable from a request, a tool argument, or a deserialized payload,
/// which is what keeps the transition's preconditions from being something a caller can assert
/// (§10.12).
/// </remarks>
internal sealed class HostProcessToolsEnvironmentProbe(IOptions<ArcanumSettings>? settings)
    : IHostProcessToolsEnvironmentProbe
{

    /// <summary>
    /// The configured edition, or the hardened default when this container never bound options.
    /// </summary>
    /// <remarks>
    /// Optional because the narrow CLI and schema-only containers legitimately compose without the
    /// options pipeline, and this probe runs during their bootstrap. Defaulting to
    /// <see cref="ArcanumEdition.Local"/> is the safe direction: an unbound container can only ever
    /// resolve to the edition that forbids the escape hatch, never to the one that allows it.
    /// </remarks>
    public HostProcessToolsTransitionEnvironment Read() =>
        new(
            ArcanumEnvironment.ResolveEdition(settings?.Value.Edition ?? ArcanumEdition.Local),
            ArcanumEnvironment.IsAllowHostProcessToolsEnabled(),
            CovenantProcessResidence.HasOpened);

}
