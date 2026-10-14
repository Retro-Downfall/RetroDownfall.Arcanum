using System.Globalization;

using Microsoft.CodeAnalysis;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Constant Value Identity. A bound value whose normalized source is a compile-time constant stands for its value
        // wherever the constant was written, because every consumer of such a source evaluates it (a branch condition, a
        // selected overload, a finite enumeration) and none reads where it came from: a constant has no side effects, depends
        // on no caller binding, and folds to the same value from any call site. Two environments that differ only in the
        // position of a constant therefore describe the same evaluation, and the identity of the environment names the
        // constant's value and type instead of the position and the caller. A source that is not a constant keeps the
        // position, the syntax fingerprint and the caller's projected environment, exactly as before, and so does a
        // constant whose value this method cannot spell exactly.
        private static bool TryConstantValueIdentity(BoundValueSource source, out string identity)
        {
            identity = "";

            SemanticModel model = source.Caller.Model;

            if (source.Expression.SyntaxTree != model.SyntaxTree)
            {
                return false;
            }

            Optional<object?> constant = model.GetConstantValue(source.Expression);

            if (!constant.HasValue)
            {
                return false;
            }

            string? value = constant.Value switch
            {
                null => "null",
                string text => "string:" + text,
                char character => "char:" + ((int)character).ToString(CultureInfo.InvariantCulture),
                bool flag => flag ? "bool:true" : "bool:false",
                float single => "float:" + single.ToString("R", CultureInfo.InvariantCulture),
                double number => "double:" + number.ToString("R", CultureInfo.InvariantCulture),
                sbyte or byte or short or ushort or int or uint or long or ulong or decimal =>
                    constant.Value.GetType().Name + ":" + Convert.ToString(constant.Value, CultureInfo.InvariantCulture),
                _ => null,
            };

            if (value is null)
            {
                return false;
            }

            TypeInfo type = model.GetTypeInfo(source.Expression);

            identity = value
                + "|"
                + (type.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "none")
                + "|"
                + (type.ConvertedType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "none");

            return true;
        }

        // Foreign Parameter Projection. A parameter is in scope only in the method that declares it and in the lambdas and
        // local functions declared inside that method. A member that cannot be nested in a method (an accessor, a
        // constructor, an operator, a conversion, an explicit interface implementation) therefore can neither name a
        // parameter of an ordinary method nor run code that does, so a binding for such a parameter is dead in its
        // environment. It reaches the member only because an instance call passes its caller's whole context on to the
        // receiver's members, and keeping it multiplies the member's states by every distinct binding of every caller
        // parameter above it. A lambda or a local function keeps every binding, since it may be nested in the owner.
        private static bool MemberCannotBeNestedInAMethod(AuthoredMember member) =>
            member.Symbol.MethodKind is not (MethodKind.LocalFunction or MethodKind.AnonymousFunction);
    }
}
