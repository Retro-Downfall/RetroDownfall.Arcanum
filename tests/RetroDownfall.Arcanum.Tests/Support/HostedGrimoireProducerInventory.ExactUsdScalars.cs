using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Exact fixed-width framework operations used by authoritative USD accumulation. This
        // profile proves no authored operands, conversions, target accessors or foreign operators.
        private bool IsReviewedExactInt128AccountingOperation(
            IMethodSymbol method,
            SyntaxNode? node,
            SemanticModel? model,
            AuthoredMember? context)
        {
            if (context is null || node is null || model is null
                || !method.IsStatic || method.Arity != 0 || method.ReturnsByRef || method.ReturnsByRefReadonly
                || !IsAccountingInt128(method.ContainingType)
                || method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
            {
                return false;
            }

            if (method is
                {
                    MethodKind: MethodKind.Conversion, Name: "op_Explicit", Parameters.Length: 1,
                    ReturnType.SpecialType: SpecialType.System_UInt32
                }
                && IsAccountingInt128(method.Parameters[0].Type))
            {
                IConversionOperation? conversion = UserConversionsAt(context, node)
                    .FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.OperatorMethod, method));

                return conversion is not null
                    && conversion.Type?.SpecialType == SpecialType.System_UInt32
                    && IsAccountingInt128(conversion.Operand.Type)
                    && OperandUsesOnlyBuiltInConversions(conversion.Operand);
            }

            if (method.MethodKind != MethodKind.UserDefinedOperator) return false;

            if (method is { Name: "op_UnaryNegation", Parameters.Length: 1 }
                && IsAccountingInt128(method.ReturnType)
                && IsAccountingInt128(method.Parameters[0].Type))
            {
                return model.GetOperation(node) is IUnaryOperation { IsLifted: false, OperatorMethod: { } selected } unary
                    && SymbolEqualityComparer.Default.Equals(selected, method)
                    && OperandUsesOnlyBuiltInConversions(unary.Operand);
            }

            if (method.Parameters.Length != 2 || !IsAccountingInt128(method.Parameters[0].Type)) return false;

            if (method.Name is "op_Multiply" or "op_Division"
                && IsAccountingInt128(method.ReturnType)
                && IsAccountingInt128(method.Parameters[1].Type)
                && model.GetOperation(node) is ICompoundAssignmentOperation
                { IsLifted: false, OperatorMethod: { } compoundMethod } compound)
            {
                return SymbolEqualityComparer.Default.Equals(compoundMethod, method)
                    && compound.InConversion.IsIdentity && compound.OutConversion.IsIdentity
                    && OperandUsesOnlyBuiltInConversions(compound.Target)
                    && OperandUsesOnlyBuiltInConversions(compound.Value);
            }

            bool comparison = method.Name is "op_GreaterThan" or "op_LessThan" or "op_Equality";

            bool shift = method.Name is "op_LeftShift" or "op_RightShift";

            bool integer = method.Name is "op_Modulus" or "op_BitwiseAnd" or "op_BitwiseOr" || shift;

            if ((!comparison && !integer)
                || (comparison ? method.ReturnType.SpecialType != SpecialType.System_Boolean : !IsAccountingInt128(method.ReturnType))
                || (shift ? method.Parameters[1].Type.SpecialType != SpecialType.System_Int32 : !IsAccountingInt128(method.Parameters[1].Type)))
            {
                return false;
            }

            return model.GetOperation(node) is IBinaryOperation { IsLifted: false, OperatorMethod: { } binaryMethod } binary
                && SymbolEqualityComparer.Default.Equals(binaryMethod, method)
                && OperandUsesOnlyBuiltInConversions(binary.LeftOperand)
                && OperandUsesOnlyBuiltInConversions(binary.RightOperand);
        }

        private static bool IsReviewedExactInt128Limit(IPropertySymbol property, bool mutation) =>
            !mutation
            && property is
            {
                Name: "MaxValue" or "MinValue", IsStatic: true, IsIndexer: false,
                Parameters.Length: 0, SetMethod: null,
                GetMethod: { IsStatic: true, IsVirtual: false, ReturnsByRef: false, ReturnsByRefReadonly: false }
            }
            && IsAccountingInt128(property.ContainingType)
            && IsAccountingInt128(property.Type);

        private static bool IsAccountingInt128(ITypeSymbol? type) =>
            type is not null && IsExactFrameworkType(type, "System.Int128", typeof(Int128).Assembly.GetName());
    }
}
