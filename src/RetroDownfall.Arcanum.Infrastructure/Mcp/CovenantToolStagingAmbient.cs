using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Everything a Covenant tool call needs to be authorized, as the turn knows it.
/// </summary>
/// <remarks>
/// Not a capability. This is the raw material a capability is minted from, and it is deliberately
/// separate: a capability is bound to one tool name and one request identity, and the turn does not
/// know either until a model actually asks for a tool. Publishing a capability instead would mean
/// minting one per turn and hoping it matched whatever arrived.
///
/// <para>Carries the registry rather than reaching for a service locator, because the binding runs on
/// the in-process MCP server's own task where no request scope exists.</para>
/// </remarks>
internal sealed record CovenantToolStagingContext(
    ICovenantMutationCollector Collector,
    CanonicalCampaignContext Campaign,
    CovenantAdmissionReceipt ProducingAdmission,
    ProviderCallMaterializationSnapshot Materialization,
    ICovenantTurnHeadProbe HeadProbe,
    CovenantToolCapabilityRegistry Registry,
    CancellationToken TurnCancellation);

/// <summary>
/// Carries one turn's staging material across the in-process MCP boundary.
/// </summary>
/// <remarks>
/// The same shape as the sibling tool ambients, and for the same reason: the in-process server runs
/// the handler on its own task, so a value the turn holds on its stack is not visible there. The
/// bridge is per-request and explicit — an <c>AsyncLocal</c> a child task captured would otherwise let
/// one turn's staging authorize another turn's tool call.
/// </remarks>
internal static class CovenantToolStagingAmbient
{

    private static readonly AsyncLocal<CovenantToolStagingContext?> Ambient = new();

    internal static CovenantToolStagingContext? Current
    {

        get => Ambient.Value;

        set => Ambient.Value = value;

    }

    /// <summary>Publishes staging material for the duration of one dispatch, then restores.</summary>
    internal static IDisposable Push(CovenantToolStagingContext? context) => new Scope(context);

    private sealed class Scope : IDisposable
    {

        private readonly CovenantToolStagingContext? _previous;

        internal Scope(CovenantToolStagingContext? context)
        {

            _previous = Ambient.Value;

            Ambient.Value = context;

        }

        public void Dispose() => Ambient.Value = _previous;

    }

}
