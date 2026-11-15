using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private readonly Dictionary<(Compilation Compilation, InvocationExpressionSyntax Admission), GrimoireWorkKind?> knownAcquisitionWorkKinds = [];

        private GrimoireWorkKind? KnownAcquisitionWorkKind(AuthoredMember member, InvocationExpressionSyntax admission)
        {
            var key = (member.Model.Compilation, admission);

            if (knownAcquisitionWorkKinds.TryGetValue(key, out GrimoireWorkKind? known)) return known;

            GrimoireWorkKind? result = null;

            if (admission.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } expression
                && member.Model.GetSymbolInfo(expression).Symbol is IFieldSymbol { IsStatic: true, HasConstantValue: true } field
                && field.ContainingType.TypeKind == TypeKind.Enum
                && IsExactAuthoredProtocolType(field.ContainingType,
                    "RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind", member.Model.Compilation)
                && Enum.TryParse(field.Name, out GrimoireWorkKind parsed)
                && Enum.IsDefined(parsed)
                && field.ConstantValue is not null
                && Convert.ToDecimal(field.ConstantValue, System.Globalization.CultureInfo.InvariantCulture) == (byte)parsed)
            {
                result = parsed;
            }

            knownAcquisitionWorkKinds.Add(key, result);

            return result;
        }

        private GrimoireWorkKind? DirectRetainedWorkKind(AuthoredMember member, string origin)
        {
            InvocationExpressionSyntax? admission = AdmissionSyntax(member).AdmissionCalls
                .FirstOrDefault(call => Location(call) == origin);

            return admission is null ? null : KnownAcquisitionWorkKind(member, admission);
        }
    }
}
