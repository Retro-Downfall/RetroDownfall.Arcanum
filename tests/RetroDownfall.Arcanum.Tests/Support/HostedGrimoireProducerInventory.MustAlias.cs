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
    // opaque return, loop-carried definition, holder read or origin-less definition is refused. The rule only removes
    // authority: the may-alias AdmissionOrigins sets that decide lifetimes and escapes are untouched.
    private sealed partial class ProducerGraph
    {
        // A binding whose argument is not a must-alias keeps its may-alias origins and gains this one marker. The marker is
        // never an acquisition location, so every membership test over an origin set is unchanged; it only makes the
        // bound formal inexact, and it travels with the set into the state identity and through every further hop.
        private const string InexactLeaseOrigin = "<inexact-lease-alias>";

        private const string LeaseAcquisitionCallee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease";

        private const string LeaseGroupCallee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup";

        private readonly Dictionary<(GraphMemberIdentity Member, string Bindings, int Start, int Length), string?> mustAliasLeaseOrigins = [];

        // The effect identities of groups the receiver proof refused at their own site. A group refused where it is begun is
        // never the Effect of a later site that would have inherited it.
        private readonly HashSet<string> refusedLeaseGroups = new(StringComparer.Ordinal);

        // The holder-era receiver witnesses (the per-unit recovery handoff, the ordinary scoped holder, the readiness source and
        // the scoped lease instance) each prove their own receiver exactly and are retired with the holder. Every other member
        // is judged here.
        private static bool LeaseReceiverProofApplies(AuthoredMember member) =>
            member.RecoveryContext is not { Disposition: RecoveryDisposition.OrdinaryPerUnitExternalEffect }
            && member.OrdinaryScopedHolder is null
            && member.ReadinessSource is null
            && member.ScopedLeaseInstance is null;

        // True when the group's receiver is proven. The origin is the acquisition location the receiver is a must-alias of, or
        // null when a holder-era witness owns the proof.
        private bool LeaseReceiverProven(AuthoredMember member, InvocationExpressionSyntax group, out string? origin)
        {
            origin = null;

            if (!LeaseReceiverProofApplies(member))
            {
                return true;
            }

            origin = AdmissionReceiver(member, group) is { } receiver ? MustAliasLeaseOrigin(member, receiver) : null;

            return origin is not null;
        }

        // The group must also be the lease the traversal holds Work for, when it holds any.
        private bool LeaseReceiverIsLiveWork(AuthoredMember member, InvocationExpressionSyntax group, string? work)
        {
            if (!LeaseReceiverProven(member, group, out string? origin))
            {
                return false;
            }

            return origin is null || work is null || WorkOrigin(work) == origin;
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

            (GraphMemberIdentity Member, string Bindings, int Start, int Length) key = (MemberIdentity(member), AdmissionBindingIdentity(member), expression.SpanStart, expression.Span.Length);

            if (!mustAliasLeaseOrigins.TryGetValue(key, out string? origin))
            {
                origin = member.Model.GetSymbolInfo(identifier).Symbol switch
                {
                    IParameterSymbol parameter => FormalLeaseOrigin(member, parameter),
                    ILocalSymbol local => LocalLeaseOrigin(member, local),
                    _ => null,
                };

                mustAliasLeaseOrigins[key] = origin;
            }

            return origin;
        }

        private static ExpressionSyntax StripLeaseValue(ExpressionSyntax expression) => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => StripLeaseValue(parenthesized.Expression),
            PostfixUnaryExpressionSyntax suppression when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression) =>
                StripLeaseValue(suppression.Operand),
            _ => expression,
        };

        // A formal of this very member, never written anywhere in it, whose bindings are one acquisition and nothing inexact.
        private string? FormalLeaseOrigin(AuthoredMember member, IParameterSymbol parameter)
        {
            if (parameter.RefKind != RefKind.None
                || !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol.OriginalDefinition, member.Symbol.OriginalDefinition)
                || member.AdmissionBindings is null
                || !member.AdmissionBindings.TryGetValue(parameter, out IReadOnlySet<string>? bound)
                || bound.Count != 1
                || bound.Contains(InexactLeaseOrigin)
                || !LeaseSymbolIsStable(member, parameter, definition: null, uncaptured: false))
            {
                return null;
            }

            return bound.Single();
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
                    && LeaseSymbolIsStable(member, local, acquisition, uncaptured: true)
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
                && LeaseSymbolIsStable(member, local, definition: null, uncaptured: true)
                && LeaseSymbolIsStable(member, origin, call, uncaptured: true))
            {
                return Location(call);
            }

            return null;
        }

        // The guarded TryAcquireWorkLease call whose out argument declares this designation.
        private static InvocationExpressionSyntax? LeaseAcquisitionOf(AuthoredMember member, SingleVariableDesignationSyntax designation) =>
            designation.Parent is DeclarationExpressionSyntax
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

        // Never written anywhere in the member, nested callables included, except by the acquisition that defines it; and,
        // when required, never mentioned in a nested lambda or local function at all.
        private static bool LeaseSymbolIsStable(AuthoredMember member, ISymbol symbol, SyntaxNode? definition, bool uncaptured)
        {
            foreach (SyntaxNode node in member.Syntax.DescendantNodesAndSelf())
            {
                if (definition is not null && definition.Span.Contains(node.Span))
                {
                    continue;
                }

                if (SyntaxNodeWritesSymbol(member.Model, node, symbol))
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
