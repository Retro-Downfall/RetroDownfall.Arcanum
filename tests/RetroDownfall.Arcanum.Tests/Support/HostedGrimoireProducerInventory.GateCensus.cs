using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

namespace RetroDownfall.Arcanum.Tests.Support;

// The Single Gate Census. A Work origin is an acquisition on the reviewed admission gate: the one type that implements the
// gate contract, registered once in the container and resolved by the contract. Every acquisition the analysis follows is a call
// through the contract's slot, and a call through a slot reaches whichever implementation the receiver holds, so the analysis
// grants a Work origin only while the analysed source declares no implementation of the contract other than the reviewed one. A
// second implementation is a gate that can mint a lease the real gate never admitted, and a lease it mints is the proof of
// admission every later send relies on; while one exists, every acquisition is refused with HOSTED_ADMISSION_GATE_UNREVIEWED and
// grants no Work. The census is a property of the analysed compilations alone, so it is computed once per discovery (see
// InventoryFacts). The production pin (Build/SingleGateCensusTests) holds the other half: that the production source has exactly
// the reviewed implementation, that it is the one the container resolves, and that the lease and the effect group are
// implemented only by the nested types of that gate.
internal static partial class HostedGrimoireProducerInventory
{
    internal const string AdmissionGateContract = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate";

    internal const string ReviewedAdmissionGate = "RetroDownfall.Arcanum.Infrastructure.Data.GrimoireConnectionAdmissionGate";

    internal const string GateAcquisitionCallee = AdmissionGateContract + ".TryAcquireWorkLease";

    internal const string GateImplementationUnreviewedCode = "HOSTED_ADMISSION_GATE_UNREVIEWED";

    // Every finding that makes the gate census unclean, in a stable order: a concrete type of the analysed source that implements
    // the gate contract (directly, through a base class or through an interface that extends it) and is not the reviewed gate, and
    // a reviewed gate declared more than once. An abstract type is no finding of its own: every instance is of a concrete subtype,
    // which is a finding.
    internal static string[] FindUnreviewedGateImplementations(IReadOnlyList<CSharpCompilation> compilations)
    {
        List<string> findings = [];

        int reviewed = 0;

        foreach (CSharpCompilation compilation in compilations)
        {
            foreach (INamedTypeSymbol type in SourceTypes(compilation.Assembly.GlobalNamespace))
            {
                if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)
                    || type.IsAbstract
                    || !type.AllInterfaces.Any(static contract => TypeKey(contract) == AdmissionGateContract))
                {
                    continue;
                }

                if (TypeKey(type) == ReviewedAdmissionGate && !type.IsGenericType)
                {
                    reviewed++;

                    continue;
                }

                findings.Add($"'{type.ToDisplayString()}' of '{compilation.AssemblyName}' implements the admission gate and is not the reviewed gate.");
            }
        }

        if (reviewed > 1)
        {
            findings.Add($"The reviewed admission gate '{ReviewedAdmissionGate}' is declared {reviewed} times.");
        }

        findings.Sort(StringComparer.Ordinal);

        return [.. findings];
    }

    private sealed partial class ProducerGraph
    {
        // True while the analysed source declares no implementation of the gate contract but the reviewed one.
        private bool AdmissionGateTrusted => inventoryFacts.UnreviewedGateImplementations.Length == 0;

        // The acquisition of a Work origin through a gate the census does not trust: nothing it returns is an admitted lease.
        private void RefuseUnreviewedGate(SyntaxNode node)
        {
            RefuseTraversalCompletion();

            diagnostics.Add(new(
                GateImplementationUnreviewedCode,
                Location(node),
                string.Join(" ", inventoryFacts.UnreviewedGateImplementations)));
        }
    }
}
