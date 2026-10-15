using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Static Handle Stores. The origin walk follows a handle through locals, formals and the values built from them, and a
        // handle written to storage it cannot follow leaves that environment at the store, which ends the admission there. For
        // an instance field that is the whole story here: the store is not refused, and the Authority Carrier Ban refuses the
        // field itself. Static storage is different. It is process-wide ambient state, never a home for a lease or an
        // external-effect group, and it is what lets a closed value (Handle-Incapable Values) reach a handle after all: a
        // stash written here can be read back through a key, an index or a conversion, by code that mentions neither the
        // handle nor anything derived from it.
        //
        // A store of an admission carrier into static storage is therefore a retained handle with no proven lifetime
        // continuation, exactly like one passed to an opaque callee, and is refused at the store. Static storage is a static
        // field or property, an element of an array or an indexer reached from one, and a member of an object reached from
        // one. A store into a local, a parameter, a field of this or of any other object that is not reached from static
        // storage is not refused here, and neither is a value that cannot carry a handle.
        private bool StoresAdmissionCarrierInStaticStorage(AuthoredMember member, AssignmentExpressionSyntax assignment, out string root)
        {
            root = "";

            SemanticModel model = assignment.SyntaxTree == member.Model.SyntaxTree ? member.Model : member.Model.Compilation.GetSemanticModel(assignment.SyntaxTree);

            IEnumerable<IOperation> written = model.GetOperation(assignment) switch
            {
                IDeconstructionAssignmentOperation deconstruction => StoredTargets(deconstruction.Target),
                IAssignmentOperation simple => [simple.Target],
                _ => [],
            };

            foreach (IOperation target in written)
            {
                if (StaticStorageRoot(target) is not { } storage) continue;

                // The structural test comes first: the origin walk runs only for a write into static storage.
                if (!MayCarryAdmissionHandle(member, assignment.Right)) return false;

                root = TypeKey(storage.ContainingType) + "." + storage.Name;

                return true;
            }

            return false;
        }

        private static IEnumerable<IOperation> StoredTargets(IOperation target) => target switch
        {
            ITupleOperation tuple => tuple.Elements.SelectMany(StoredTargets),
            _ => [target],
        };

        // The static member that the written location is reached from, or null when it is not reached from one. Only the
        // receiver chain is followed: a field, property, indexer or array element is static or hangs off something that is.
        private static ISymbol? StaticStorageRoot(IOperation? location)
        {
            for (int depth = 0; location is not null && depth < 64; depth++)
            {
                switch (location)
                {
                    case IFieldReferenceOperation field:
                        if (field.Field.IsStatic) return field.Field;

                        location = field.Instance;

                        break;

                    case IPropertyReferenceOperation property:
                        if (property.Property.IsStatic) return property.Property;

                        location = property.Instance;

                        break;

                    case IArrayElementReferenceOperation element:
                        location = element.ArrayReference;

                        break;

                    case IConversionOperation conversion:
                        location = conversion.Operand;

                        break;

                    default:
                        return null;
                }
            }

            return null;
        }
    }
}
