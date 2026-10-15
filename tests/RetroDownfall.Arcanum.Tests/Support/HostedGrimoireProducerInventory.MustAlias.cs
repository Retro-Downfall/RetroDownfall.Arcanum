using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    // Lease Receiver Proof (CK-3). A TryBeginExternalEffectGroup call grants an Effect only when its receiver is a
    // must-alias of a work lease the traversal knows. The receiver must be an identifier of a parameter, or of a
    // never-reassigned, uncaptured local, and its one reaching definition must be one of: the out designation of the
    // guarded TryAcquireWorkLease call; a copy `x = admitted!` of that designation; or a formal every one of whose
    // bindings is itself a must-alias (the binding carries no inexact marker). Any join, conditional, coalesce, switch,
    // opaque return, loop-carried definition, holder read or origin-less definition is refused, and so is a variable that
    // anything writes, including by handing it out by reference. The rule only removes authority: the may-alias
    // AdmissionOrigins sets that decide lifetimes and escapes are untouched.
    private sealed partial class ProducerGraph
    {
        // A binding whose argument is not a must-alias keeps its may-alias origins and gains this one marker. The marker is
        // never an acquisition location, so every membership test over an origin set is unchanged; it only makes the
        // bound formal inexact, and it travels with the set into the state identity and through every further hop.
        private const string InexactLeaseOrigin = "<inexact-lease-alias>";

        private const string LeaseAcquisitionCallee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease";

        private const string LeaseGroupCallee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup";

        // The facts about a symbol that are a pure function of the syntax: whether anything in its scope writes it, and which acquisition
        // defines a local. None of them depends on how a caller bound the member, so none is keyed on a binding; a result that did
        // depend on the binding would be reused by a different binding state that merely labels its formals the same way.
        private readonly Dictionary<ISymbol, Dictionary<SyntaxNode, bool>> leaseFormalStability = new(SymbolEqualityComparer.Default);

        private readonly Dictionary<ISymbol, Dictionary<SyntaxNode, string?>> leaseLocalOrigins = new(SymbolEqualityComparer.Default);

        // The holder-era receiver witnesses (the per-unit recovery handoff, the ordinary scoped holder, the readiness source and
        // the scoped lease instance) each prove their own receiver exactly and are retired with the holder. A member that
        // carries one is not required to prove a must-alias receiver; the witness is. This is no licence for a counterfeit: the
        // LeaseReceiverProof fixtures pin that a fake or ternary receiver gets no Effect in each exempt context, and a receiver
        // that IS a must-alias is judged here whatever the member carries. Every other member is judged here in full.
        private static bool LeaseReceiverProofApplies(AuthoredMember member) =>
            member.RecoveryContext is not { Disposition: RecoveryDisposition.OrdinaryPerUnitExternalEffect }
            && member.OrdinaryScopedHolder is null
            && member.ReadinessSource is null
            && member.ScopedLeaseInstance is null;

        // True when the group's receiver is proven. The origin is the acquisition location the receiver is a must-alias of, or
        // null when a holder-era witness owns the proof. A receiver that is a must-alias is judged here whatever the member's
        // context; only a receiver that is not one is left to the holder-era witness, and only in a member that carries one.
        private bool LeaseReceiverProven(AuthoredMember member, InvocationExpressionSyntax group, out string? origin)
        {
            origin = AdmissionReceiver(member, group) is { } receiver ? MustAliasLeaseOrigin(member, receiver) : null;

            return origin is not null || !LeaseReceiverProofApplies(member);
        }

        // The group must also be the lease the traversal holds Work for. A traversal that holds none (the lease was disposed, handed
        // away or bound into a holder before the group) leaves the group to be judged by its receiver alone, exactly as the Lease
        // Spine does for a paid door whose lease departed: the group stands, and every send under it needs its own Work, which
        // HOSTED_SITE_WORK_FRONTIER_MISSING reports at the send. What the group must not do is meet a Work the same member starts
        // afterwards, because a send under both would pair the Effect of one lease with the Work of another.
        private bool LeaseReceiverIsLiveWork(AuthoredMember member, InvocationExpressionSyntax group, string? work)
        {
            if (!LeaseReceiverProven(member, group, out string? origin))
            {
                return false;
            }

            if (origin is null)
            {
                return true;
            }

            if (work is not null)
            {
                return WorkOrigin(work) == origin;
            }

            return !member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(call => call.SpanStart > group.SpanStart && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && Normalize(method) == LeaseAcquisitionCallee);
        }

        // The shell's recovery group is begun on a lease the shell itself acquired, never on one it was handed or acquired twice.
        private bool LeaseReceiverIsOwnAcquisition(AuthoredMember member, InvocationExpressionSyntax group, string? work)
        {
            if (!LeaseReceiverIsLiveWork(member, group, work))
            {
                return false;
            }

            if (!LeaseReceiverProven(member, group, out string? origin) || origin is null)
            {
                return true;
            }

            return member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(call => Location(call) == origin && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && Normalize(method) == LeaseAcquisitionCallee);
        }

        private static string? WorkOrigin(string work)
        {
            int marker = work.LastIndexOf("@work:", StringComparison.Ordinal);

            return marker < 0 ? null : work[(marker + "@work:".Length)..];
        }

        private HostedProducerInventoryDiagnostic LeaseReceiverMismatch(string operationId, SyntaxNode group) => new(
            "HOSTED_ADMISSION_RECEIVER_MISMATCH",
            RootOperation(operationId) + "/call@" + Location(group),
            LeaseGroupCallee + "; The external-effect group's receiver is not a must-alias of the work lease the traversal holds, so the group grants no Effect.");

        // The origin set a callee's formal is bound to. An argument that is not a must-alias of a single origin keeps its
        // may-alias origins and is marked inexact, so the formal can never satisfy the receiver proof.
        private IReadOnlySet<string> BoundAdmissionOrigins(AuthoredMember caller, ExpressionSyntax argument)
        {
            IReadOnlySet<string> origins = AdmissionOrigins(caller, argument, new HashSet<ISymbol>(SymbolEqualityComparer.Default));

            if (origins.Count == 0
                || origins.Contains(InexactLeaseOrigin)
                || (origins.Count == 1 && MustAliasLeaseOrigin(caller, argument) is { } exact && origins.Contains(exact)))
            {
                return origins;
            }

            return new HashSet<string>(origins, StringComparer.Ordinal) { InexactLeaseOrigin };
        }

        private string? MustAliasLeaseOrigin(AuthoredMember member, ExpressionSyntax expression)
        {
            if (expression.SyntaxTree != member.Syntax.SyntaxTree
                || StripLeaseValue(expression) is not IdentifierNameSyntax identifier)
            {
                return null;
            }

            return member.Model.GetSymbolInfo(identifier).Symbol switch
            {
                IParameterSymbol parameter => FormalLeaseOrigin(member, parameter),
                ILocalSymbol local => Memoised(leaseLocalOrigins, local, member.Syntax, () => LocalLeaseOrigin(member, local)),
                _ => null,
            };
        }

        private static T Memoised<T>(Dictionary<ISymbol, Dictionary<SyntaxNode, T>> cache, ISymbol symbol, SyntaxNode scope, Func<T> compute)
        {
            if (!cache.TryGetValue(symbol, out Dictionary<SyntaxNode, T>? scopes))
            {
                cache[symbol] = scopes = [];
            }

            if (!scopes.TryGetValue(scope, out T? value))
            {
                scopes[scope] = value = compute();
            }

            return value;
        }

        private static ExpressionSyntax StripLeaseValue(ExpressionSyntax expression) => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => StripLeaseValue(parenthesized.Expression),
            PostfixUnaryExpressionSyntax suppression when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression) =>
                StripLeaseValue(suppression.Operand),
            _ => expression,
        };

        // A formal of this member, or of the method a lambda or local function of it is nested in (a nested body sees the enclosing
        // bindings), that is never written anywhere in its declaring method, nested callables included, and whose bindings are one
        // acquisition and nothing inexact. A formal that is never written holds the bound value however a closure captures it.
        private string? FormalLeaseOrigin(AuthoredMember member, IParameterSymbol parameter)
        {
            // The binding is read from this state's own map every time; it is the one thing a cache must not stand in for.
            if (parameter.RefKind != RefKind.None
                || member.AdmissionBindings is null
                || !member.AdmissionBindings.TryGetValue(parameter, out IReadOnlySet<string>? bound)
                || bound.Count != 1
                || bound.Contains(InexactLeaseOrigin)
                || LeaseFormalScope(member, parameter) is not { } scope
                || !Memoised(leaseFormalStability, parameter, scope, () => LeaseSymbolIsStable(member, scope, parameter, definition: null, uncaptured: false)))
            {
                return null;
            }

            return bound.Single();
        }

        // The syntax that declares the formal's method, when this member is that method or nested inside it.
        private static SyntaxNode? LeaseFormalScope(AuthoredMember member, IParameterSymbol parameter)
        {
            if (SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol.OriginalDefinition, member.Symbol.OriginalDefinition))
            {
                return member.Syntax;
            }

            return parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.LocalFunction or MethodKind.AnonymousFunction } owner
                && owner.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is { } declaration
                && declaration.SyntaxTree == member.Syntax.SyntaxTree
                && declaration.Span.Contains(member.Syntax.Span)
                    ? declaration
                    : null;
        }

        private string? LocalLeaseOrigin(AuthoredMember member, ILocalSymbol local)
        {
            if (local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is not { } declaration
                || declaration.SyntaxTree != member.Syntax.SyntaxTree
                || !member.Syntax.Span.Contains(declaration.Span))
            {
                return null;
            }

            if (declaration is SingleVariableDesignationSyntax designation)
            {
                return LeaseAcquisitionOf(member, designation) is { } acquisition
                    && LeaseSymbolIsStable(member, member.Syntax, local, acquisition, uncaptured: true)
                        ? Location(acquisition)
                        : null;
            }

            // A copy of the designation: `x = admitted!`, the production spelling of the lease the worker holds.
            if (declaration is VariableDeclaratorSyntax { Initializer.Value: { } value }
                && StripLeaseValue(value) is IdentifierNameSyntax source
                && member.Model.GetSymbolInfo(source).Symbol is ILocalSymbol origin
                && origin.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is SingleVariableDesignationSyntax acquired
                && acquired.SyntaxTree == member.Syntax.SyntaxTree
                && member.Syntax.Span.Contains(acquired.Span)
                && LeaseAcquisitionOf(member, acquired) is { } call
                && LeaseSymbolIsStable(member, member.Syntax, local, definition: null, uncaptured: true)
                && LeaseSymbolIsStable(member, member.Syntax, origin, call, uncaptured: true))
            {
                return Location(call);
            }

            return null;
        }

        // The guarded TryAcquireWorkLease call whose out argument declares this designation, on a gate the Single Gate Census trusts.
        // A lease from a gate beside the reviewed one is the origin of nothing.
        private InvocationExpressionSyntax? LeaseAcquisitionOf(AuthoredMember member, SingleVariableDesignationSyntax designation) =>
            AdmissionGateTrusted
            && designation.Parent is DeclarationExpressionSyntax
            {
                Parent: ArgumentSyntax
                {
                    RefKindKeyword.RawKind: (int)SyntaxKind.OutKeyword,
                    Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } arguments,
                } argument,
            }
            && arguments.Arguments.Last() == argument
            && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method
            && Normalize(method) == LeaseAcquisitionCallee
            && IsGuardedAdmission(call)
                ? call
                : null;

        // Never written anywhere under the root, nested callables included, except by the acquisition that defines it; and,
        // when required, never mentioned in a nested lambda or local function at all. A write includes handing the variable out
        // by reference, spelled or implicit: an out, ref or in argument, a ref local or an address can all overwrite it.
        private static bool LeaseSymbolIsStable(AuthoredMember member, SyntaxNode root, ISymbol symbol, SyntaxNode? definition, bool uncaptured)
        {
            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                if (definition is not null && definition.Span.Contains(node.Span))
                {
                    continue;
                }

                if (SyntaxNodeWritesSymbol(member.Model, node, symbol) || NodeWritesSymbolThroughReference(member.Model, node, symbol))
                {
                    return false;
                }

                if (uncaptured
                    && node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax
                    && SyntaxReferencesSymbol(member.Model, node, symbol))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
