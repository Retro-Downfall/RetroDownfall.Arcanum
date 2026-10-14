using Microsoft.CodeAnalysis;

using System.Reflection;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        private readonly Dictionary<ITypeSymbol, bool> handleIncapableTypes = new(SymbolEqualityComparer.Default);

        // Handle-Incapable Values. A value that is computed from a call or expression that mentions a lease is "derived from
        // the lease", and the origin walk follows it so that a disposable built from it counts as a possible carrier of the
        // admission. A value whose static type is closed pure data has nowhere to keep a lease or an external-effect group, so
        // following it can only turn a harmless parameter, record or string into a spurious carrier: every command built from
        // the text of an accepted paid result, and every call on it, became a retained handle passed to an opaque callee.
        //
        // A type is closed pure data when no instance of it, whatever its runtime type, can hold or reach a reference to
        // anything but more closed pure data:
        //   - an exact CoreLib scalar, string, enumeration, Guid or other reviewed value leaf;
        //   - a one-dimensional array, a Nullable, a ValueTuple or an ImmutableArray of closed pure data;
        //   - a struct or a sealed class declared in analysed source, whose base is exactly object or ValueType, that
        //     implements neither IDisposable nor IAsyncDisposable, and whose every instance field (backing fields of
        //     properties included) is closed pure data. A constructed generic is judged on its substituted fields.
        // Everything else is not closed: interfaces, object, dynamic, delegates, type parameters, pointers, ref structs, an
        // unsealed class (a subclass may add a handle field), any type with an authored base, and any type declared outside
        // analysed source, whose non-public fields are not visible and are therefore not reviewed. A lookalike of a
        // framework type is judged by its own fields, never by its name. A type that reaches itself is not closed.
        private bool IsHandleIncapableValueType(ITypeSymbol? type, Compilation consuming)
        {
            if (type is null) return false;

            CleanupProvenanceContext proof = new();

            HashSet<ITypeSymbol> path = new(SymbolEqualityComparer.Default);

            // A verdict reached after the work budget ran out is only a refusal, never a fact about the type, so it is not kept.
            bool exhausted = false;

            return Incapable(type);

            bool Incapable(ITypeSymbol current)
            {
                if (handleIncapableTypes.TryGetValue(current, out bool known)) return known;

                if (proof.RemainingWork-- <= 0)
                {
                    exhausted = true;

                    return false;
                }

                if (current is not (INamedTypeSymbol or IArrayTypeSymbol)) return false;

                if (!path.Add(current)) return false;

                try
                {
                    bool closed = Judge(current);

                    if (closed || !exhausted) handleIncapableTypes[current] = closed;

                    return closed;
                }
                finally
                {
                    path.Remove(current);
                }
            }

            bool Judge(ITypeSymbol current)
            {
                if (current is IArrayTypeSymbol array) return array.Rank == 1 && Incapable(array.ElementType);

                INamedTypeSymbol named = (INamedTypeSymbol)current;

                if (named.IsAnonymousType) return false;

                if (IsClosedValueLeaf(named, consuming, proof)) return true;

                AssemblyName core = typeof(object).Assembly.GetName();

                if (named.IsGenericType && !named.IsUnboundGenericType
                    && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                    && IsExactFrameworkType(named.OriginalDefinition, "System.Nullable`1", core))
                {
                    return named.TypeArguments is [ITypeSymbol value] && Incapable(value);
                }

                if (named.IsTupleType)
                {
                    return named.TupleElements.Length is >= 2 and <= 7
                        && IsExactFrameworkType(named.OriginalDefinition, "System.ValueTuple`" + named.TupleElements.Length, core)
                        && named.TupleElements.All(element => Incapable(element.Type));
                }

                if (named.IsGenericType && !named.IsUnboundGenericType
                    && IsExactFrameworkType(named.OriginalDefinition, "System.Collections.Immutable.ImmutableArray`1",
                        typeof(System.Collections.Immutable.ImmutableArray<>).Assembly.GetName()))
                {
                    return named.TypeArguments is [ITypeSymbol item] && Incapable(item);
                }

                // Only a type declared in analysed source exposes every field it has.
                if (named.DeclaringSyntaxReferences.IsEmpty
                    || named.IsUnboundGenericType
                    || AuthoredCompilationIdentity(named, consuming) is null) return false;

                bool structure = named.TypeKind == TypeKind.Struct && !named.IsRefLikeType;

                if (!structure && !(named.TypeKind == TypeKind.Class && named.IsSealed && !named.IsStatic)) return false;

                if (!IsExactFrameworkType(named.BaseType, structure ? "System.ValueType" : "System.Object", core)
                    || named.AllInterfaces.Any(static contract => TypeKey(contract) is "System.IDisposable" or "System.IAsyncDisposable")) return false;

                foreach (IFieldSymbol field in named.GetMembers().OfType<IFieldSymbol>())
                {
                    if (field.IsStatic || field.IsConst) continue;

                    if (!Incapable(field.Type)) return false;
                }

                return true;
            }
        }

        // A reference to a local or parameter of closed pure data contributes no origin: see IsHandleIncapableValueType.
        private bool HoldsHandleIncapableValue(ISymbol symbol, Compilation consuming) => symbol switch
        {
            ILocalSymbol local => IsHandleIncapableValueType(local.Type, consuming),
            IParameterSymbol parameter => IsHandleIncapableValueType(parameter.Type, consuming),
            _ => false,
        };
    }
}
