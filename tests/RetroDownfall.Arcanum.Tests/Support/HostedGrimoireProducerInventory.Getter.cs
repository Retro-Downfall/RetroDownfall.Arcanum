using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private readonly Dictionary<GraphMemberIdentity, bool> fieldReadGetterProofs = [];

        private bool IsFieldReadOrDisposedGetter(AuthoredMember member)
        {
            if (member.Symbol is not
                {
                    MethodKind: MethodKind.PropertyGet, IsStatic: false, IsVirtual: false, IsOverride: false,
                    ReturnsByRef: false, ReturnsByRefReadonly: false, Parameters.Length: 0,
                }
                || member.Syntax is not BinaryExpressionSyntax { Right: ThrowExpressionSyntax failure } coalesce
                || !coalesce.IsKind(SyntaxKind.CoalesceExpression))
            {
                return false;
            }

            GraphMemberIdentity identity = MemberIdentity(member);

            if (!fieldReadGetterProofs.TryGetValue(identity, out bool proven))
            {
                // This is a terminal effect proof only. The caller still binds the receiver and reads
                // the getter's original field source when resolving a value, cleanup, or admission.
                proven = member.Model.GetOperation(coalesce) is ICoalesceOperation { ValueConversion.IsIdentity: true }
                    && member.Model.GetOperation(coalesce.Left) is IFieldReferenceOperation
                    {
                        Field: { IsStatic: false, IsVolatile: false, Type.IsReferenceType: true } field,
                        Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance },
                    }
                    && SymbolEqualityComparer.Default.Equals(field.ContainingType, member.Symbol.ContainingType)
                    && SymbolEqualityComparer.Default.Equals(field.Type, member.Symbol.ReturnType)
                    && member.Model.GetOperation(failure.Expression) is IObjectCreationOperation
                    {
                        Constructor: { Parameters: [{ Type.SpecialType: SpecialType.System_String }] } constructor,
                        Initializer: null, Arguments: [{ Value.ConstantValue: { HasValue: true, Value: string } }],
                    }
                    && IsExactFrameworkType(constructor.ContainingType, "System.ObjectDisposedException",
                        typeof(ObjectDisposedException).Assembly.GetName());

                fieldReadGetterProofs.Add(identity, proven);
            }

            return proven;
        }
    }
}
