using System.Globalization;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    // Lease-Handoff Root. The existing re-rooting of a self-admitted task launch is generalised to a lease-carried awaited call:
    // a declared root whose admission is not minted inside it but carried in by value, as the one IGrimoireWorkLease formal its
    // catalog entry names. The formal is the root's admission origin (the Root Lease Formal), so every escape, lifetime and receiver
    // rule applies to it exactly as it applies to an acquisition. A caller that hands the root its own lease does not traverse the
    // root's closure again under its own budget: it re-roots, under the root's operation id and the root's canonical Work, when
    // every precondition below holds, and the state cache dedupes that traversal against the declared root's own.
    //
    //   - the callee is exactly the declared root, and the caller is a root of the same service with the same work kind;
    //   - the lease argument is a must-alias of the one acquisition whose Work the caller holds at the call;
    //   - the call is directly awaited in the caller's own body (`await call`, or `await call.ConfigureAwait(constant)`: an argument
    //     evaluated between the call and its await would run while the root does), no external-effect group is open, and nothing in the
    //     argument evaluation has ended the lease;
    //   - no other argument and not the receiver may hold the lease (the canonical root binds its formal alone, so a second alias would
    //     reach it unbound, outside every rule the cut grants Work under), and no closure of the caller mentions the lease;
    //   - a call that can run again (in a loop, or behind a backward jump) is cut only if the root preserves its formal, because a root
    //     that disposes it would be handed the same lease, already ended, on the next pass.
    //
    // A call that is the declared root but fails one of them is HOSTED_LEASE_HANDOFF_UNPROVEN and is traversed in line, under the
    // caller's budget. The caller's lease lifetime continues after a cut call only if the root proves its formal is never
    // disposed, stored, returned, captured or escaped anywhere in its closure (Formal Preservation); otherwise the lifetime
    // ends at the call. Every syntactic reference to the root's method in analysed production source must be a cut call that some
    // root proved (the Handoff Caller Census), and the manifest ties the root's formal capsule to each caller's acquisition capsule
    // with one handoff-binding row per call site. Captures of the lease in an ancestor of the caller are not judged at this edge: the
    // Authority Carrier Ban (AuthorityCarrierScanner, AuthorityCarrierBanTests) refuses a closure over a work lease wherever it is.
    internal const string RootLeaseFormalCallee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.RootLeaseFormal";

    internal const string LeaseHandoffUnprovenCode = "HOSTED_LEASE_HANDOFF_UNPROVEN";

    internal const string LeaseHandoffCallerUnanalysedCode = "HOSTED_LEASE_HANDOFF_CALLER_UNANALYSED";

    internal const string ManifestHandoffUnboundCode = "HOSTED_MANIFEST_HANDOFF_UNBOUND";

    internal const string LeaseHandoffRootUnboundCode = "HOSTED_LEASE_HANDOFF_ROOT_UNBOUND";

    internal const string LeaseFormalUndeclaredCode = "HOSTED_SITE_LEASE_FORMAL_UNDECLARED";

    internal const string LeaseHandoffUndeclaredCode = "HOSTED_SITE_LEASE_HANDOFF_UNDECLARED";

    private const string LeaseHandoffFormalType = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease";

    // A work frontier is the row that every Work capsule of an operation resolves to: an acquisition, or the formal of a handoff root.
    private static bool IsWorkFrontierCallee(string callee) =>
        callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal) || callee == RootLeaseFormalCallee;

    // The chain pin. The Session shell reaches the handoff method through request-path code that no root of the inventory traverses, so the
    // Handoff Caller Census cannot judge it; the chain above the one proven call site is therefore pinned by an exact reviewed list of
    // callers for each of its links. A link is matched on the method's identity, never on the type a caller happens to name: the method
    // itself, the interface slots it implements, the methods that implement a slot, the methods those override or are overridden by, and
    // the method groups that name any of them. A pin keyed on the interface alone is bypassed by a caller of the registered class.
    private static readonly string[] HandoffChainIdentifiers =
    [
        "ProcessAdmittedAsync",
        "ResumeAdmittedAsync",
        "ResumeCoreAsync",
        "RunOwnedAsync",
        "LongRestSweepCoordinator",
        "ILongRestAdmittedSweep",
        "ILongRestAdmittedMaintenance",
        "ILongRestSweepCoordinator",
    ];

    // Every reference to a link of the chain in the given compilations, as the member that holds it. `containingType` names the link's
    // type (an interface slot or a class) and `method` its name. A reference that is not an invocation is marked, so a method group or a
    // delegate cannot be a caller the pin does not see. The matching is the one shared matcher (see MethodReferences).
    internal static string[] HandoffChainReferences(IReadOnlyList<CSharpCompilation> compilations, string containingType, string method)
    {
        List<string> found = [];

        foreach (MethodReference reference in MethodReferencesTo([.. compilations], containingType, method))
        {
            BaseMethodDeclarationSyntax? enclosing = reference.Name.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();

            string owner = enclosing?.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "<none>";

            string member = enclosing is MethodDeclarationSyntax declared ? declared.Identifier.ValueText : "<other>";

            found.Add(owner + "." + member + (reference.Invoked ? "" : " (reference)"));
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }

    // The chain's identifiers in one tree, as line and name. Tokens, so a comment or a string never counts.
    internal static string[] HandoffChainNamesIn(SyntaxTree tree) => [.. tree.GetRoot().DescendantTokens()
        .Where(static token => token.IsKind(SyntaxKind.IdentifierToken) && HandoffChainIdentifiers.Contains(token.ValueText, StringComparer.Ordinal))
        .Select(token => (tree.GetLineSpan(token.Span).StartLinePosition.Line + 1).ToString(CultureInfo.InvariantCulture) + ":" + token.ValueText)];

    // The projects the inventory does not analyse are scoped out of the pin, and the scope-out is sound only while none of them names a
    // link of the chain. The development host and the benchmark and smoke harnesses can see the internals; the client projects cannot
    // and are held to the same rule. The unit-test projects exercise the chain and are the only ones excluded.
    internal static string[] HandoffChainNamesOutsideTheAnalysedProjects()
    {
        string repository = FindRepositoryRoot();

        HashSet<string> analysed = [.. ProductionCompilations.Select(static compilation => compilation.AssemblyName!)];

        List<string> found = [];

        foreach (string root in new[] { "src", "tests" })
        {
            foreach (string project in Directory.EnumerateDirectories(Path.Combine(repository, root)).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(project);

                if (analysed.Contains(name) || name.EndsWith(".Tests", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    string relative = Path.GetRelativePath(repository, file).Replace('\\', '/');

                    if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    found.AddRange(HandoffChainNamesIn(CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: relative)).Select(hit => relative + ":" + hit));
                }
            }
        }

        return [.. found];
    }

    // The handoff-binding association. Each formal row is bound by at least one binding row (a LeaseHandoff site in a caller's operation
    // whose callee is the root's operation id), each binding row names a root that has a formal row, and the Work the binding carries is
    // a work frontier of the work kind the formal admits: an acquisition, or the formal of an outer handoff root that hands its own lease
    // on. A formal nobody binds, or a binding to a root without a formal, is a manifest that would let a root's Work stand with no lease
    // behind it.
    private static void ValidateManifestHandoffBindings(IReadOnlyList<HostedProducerSite> sites, List<HostedProducerInventoryDiagnostic> diagnostics)
    {
        HostedProducerSite[] formals = [.. sites.Where(static site => site.Kind == HostedProducerSiteKind.EffectFrontier && site.Callee == RootLeaseFormalCallee)];

        HostedProducerSite[] bindings = [.. sites.Where(static site => site.Kind == HostedProducerSiteKind.LeaseHandoff)];

        foreach (HostedProducerSite formal in formals)
        {
            HostedProducerSite[] bound = [.. bindings.Where(binding => binding.RootType == formal.RootType && binding.Callee == formal.AuthorityOperationId)];

            if (bound.Length == 0)
            {
                diagnostics.Add(new(ManifestHandoffUnboundCode, StableSiteIdentity(formal), "A root lease formal needs one handoff-binding row for each caller call site."));
            }
        }

        foreach (HostedProducerSite binding in bindings)
        {
            HostedProducerSite[] formal = [.. formals.Where(candidate => candidate.RootType == binding.RootType && candidate.AuthorityOperationId == binding.Callee)];

            HostedProducerSite[] acquisition = binding.WorkFrontierCapsuleId is null
                ? []
                : [.. sites.Where(candidate => candidate.RootType == binding.RootType && candidate.AuthorityOperationId == binding.AuthorityOperationId
                    && candidate.CapsuleId == binding.WorkFrontierCapsuleId && candidate.Kind == HostedProducerSiteKind.EffectFrontier
                    && IsWorkFrontierCallee(candidate.Callee))];

            if (formal.Length != 1 || acquisition.Length != 1 || acquisition[0].AdmittedWorkKind != formal[0].AdmittedWorkKind)
            {
                diagnostics.Add(new(ManifestHandoffUnboundCode, StableSiteIdentity(binding), "A handoff-binding row must name one root lease formal and carry one work frontier of the work kind that formal admits."));
            }
        }
    }

    private sealed partial class ProducerGraph
    {
        // The canonical form of a handoff root: the declared member as the root traversal binds it, with the formal bound to its own
        // origin. The declared root's traversal and every caller's cut edge use this one object, so they share one traversal state.
        private sealed record LeaseHandoffRoot(
            HostedProducerOperationEntry Entry,
            string RootType,
            AuthoredMember Member,
            SyntaxNode Selection,
            IParameterSymbol Formal,
            ParameterSyntax FormalSyntax,
            string Origin,
            string Work);

        private readonly Dictionary<string, string> leaseHandoffRootTypes = new(StringComparer.Ordinal);

        private readonly Dictionary<string, LeaseHandoffRoot?> leaseHandoffRoots = new(StringComparer.Ordinal);

        private readonly Dictionary<string, bool> leaseFormalPreservation = new(StringComparer.Ordinal);

        private readonly HashSet<string> leaseFormalPreservationRunning = new(StringComparer.Ordinal);

        // Handoff call sites some traversal proved, as the root's operation id and the call's coordinate.
        private readonly HashSet<string> provenLeaseHandoffCalls = new(StringComparer.Ordinal);

        private static string LeaseHandoffCallKey(HostedProducerOperationEntry entry, SyntaxNode call) => entry.OperationId + "|" + Location(call);

        private HostedProducerOperationEntry? LeaseHandoffEntryAt(string operationId) =>
            declaredOperations.FirstOrDefault(entry => entry.LeaseFormal is not null && entry.OperationId == operationId);

        private HostedProducerOperationEntry? LeaseHandoffEntryOf(AuthoredMember target) =>
            declaredOperations.FirstOrDefault(entry => entry.LeaseFormal is not null && entry.Member == target.Symbol.Name
                && entry.EnclosingType == TypeKey(target.Symbol.ContainingType) && entry.SourcePath == target.Syntax.SyntaxTree.FilePath);

        // The declared formal: exactly one by-value parameter of the lease type, declared in the root's own tree.
        private static (IParameterSymbol Formal, ParameterSyntax Syntax)? LeaseHandoffFormal(AuthoredMember member, HostedProducerOperationEntry entry)
        {
            IParameterSymbol[] formals = [.. member.Symbol.Parameters.Where(parameter => parameter.Name == entry.LeaseFormal)];

            if (formals is not [IParameterSymbol { RefKind: RefKind.None, IsParams: false } formal]
                || TypeKey(formal.Type) != LeaseHandoffFormalType
                || formal.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is not ParameterSyntax syntax
                || syntax.SyntaxTree != member.Syntax.SyntaxTree
                || !member.Syntax.Span.Contains(syntax.Span))
            {
                return null;
            }

            return (formal, syntax);
        }

        // The declared root as its traversal starts: the lifecycle and declared-root factory contexts, then, for a handoff root, the
        // formal bound to its own origin and the Work that origin mints.
        private (AuthoredMember Member, string? Work) CanonicalDeclaredRoot(AuthoredMember member, string operationId, SyntaxNode? selection, bool lifecycle)
        {
            member = BindLifecycleRootFactoryContext(member);

            member = BindDeclaredRootFactoryContext(member, operationId, selection);

            if (lifecycle || LeaseHandoffEntryAt(operationId) is not { } entry || LeaseHandoffFormal(member, entry) is not var (formal, syntax))
            {
                return (member, null);
            }

            string origin = Location(syntax);

            Dictionary<ISymbol, IReadOnlySet<string>> bindings = member.AdmissionBindings is null
                ? new(SymbolEqualityComparer.Default)
                : new(member.AdmissionBindings, SymbolEqualityComparer.Default);

            bindings[formal] = new HashSet<string>(StringComparer.Ordinal) { origin };

            AuthoredMember bound = member with { AdmissionBindings = bindings };

            _ = TryRegisterEvaluationEnvironmentToken(bound);

            return (bound, RootOperation(operationId) + "@work:" + origin);
        }

        private LeaseHandoffRoot? LeaseHandoffRootFor(HostedProducerOperationEntry entry)
        {
            if (leaseHandoffRoots.TryGetValue(entry.OperationId, out LeaseHandoffRoot? known))
            {
                return known;
            }

            LeaseHandoffRoot? root = null;

            AuthoredMember[] candidates = [.. AuthoredMembers.Where(member => member.Syntax.SyntaxTree.FilePath == entry.SourcePath
                && TypeKey(member.Symbol.ContainingType) == entry.EnclosingType && member.Symbol.Name == entry.Member)];

            if (candidates is [AuthoredMember raw]
                && SelectRoot(raw, entry.OperationId) is { } selection && selection == raw.Syntax
                && leaseHandoffRootTypes.TryGetValue(entry.OperationId, out string? rootType))
            {
                (AuthoredMember member, string? work) = CanonicalDeclaredRoot(raw, entry.OperationId, selection, lifecycle: false);

                if (work is not null && LeaseHandoffFormal(member, entry) is var (formal, syntax))
                {
                    root = new(entry, rootType, member, selection, formal, syntax, Location(syntax), work);
                }
            }

            leaseHandoffRoots[entry.OperationId] = root;

            return root;
        }

        // A handoff declaration that names no usable formal never reaches a traversal as a root with Work.
        private void ValidateLeaseHandoffDeclarations()
        {
            foreach (HostedProducerOperationEntry entry in declaredOperations.Where(static entry => entry.LeaseFormal is not null))
            {
                if (entry.Authority != HostedProducerAuthorityKind.OrdinaryHostedWork || LeaseHandoffRootFor(entry) is null)
                {
                    diagnostics.Add(new("HOSTED_ROOT_UNRESOLVED", entry.OperationId, "A lease-handoff root must be an ordinary hosted root whose declared formal binds once to a by-value IGrimoireWorkLease parameter."));
                }
            }
        }

        // The root's own Work row: the formal is to a handoff root what the acquisition is to any other, the frontier every Work
        // capsule of the operation resolves to.
        private TraversalEvidence AddLeaseFormalSite(AuthoredMember member, string rootType, string operationId, LeaseHandoffRoot root) =>
            AddSite(HostedProducerSiteKind.EffectFrontier, RootLeaseFormalCallee, member, rootType, operationId, root.FormalSyntax);

        private sealed record LeaseHandoffEdge(LeaseHandoffRoot Root, string Origin);

        // Judges one resolved call. Null means the call is not a handoff root, or it is one that was refused (the refusal is a
        // diagnostic, and the caller traverses the target in line).
        private LeaseHandoffEdge? TryLeaseHandoff(
            AuthoredMember caller,
            InvocationExpressionSyntax call,
            AuthoredMember target,
            int targetCount,
            string rootType,
            string operationId,
            HostedProducerOperationEntry? callerRoot,
            string? work,
            string? effect,
            string? recoveryEffect)
        {
            if (LeaseHandoffEntryOf(target) is not { } entry)
            {
                return null;
            }

            string? refusal = LeaseHandoffRefusal(caller, call, target, targetCount, rootType, callerRoot, work, effect, recoveryEffect, entry, out LeaseHandoffRoot? root, out string? origin);

            if (refusal is not null || root is null || origin is null)
            {
                RefuseTraversalCompletion();

                diagnostics.Add(new(LeaseHandoffUnprovenCode, RootOperation(operationId) + "/call@" + Location(call), entry.OperationId + "; " + (refusal ?? "The handoff root is not usable.")));

                return null;
            }

            provenLeaseHandoffCalls.Add(LeaseHandoffCallKey(entry, call));

            return new(root, origin);
        }

        private string? LeaseHandoffRefusal(
            AuthoredMember caller,
            InvocationExpressionSyntax call,
            AuthoredMember target,
            int targetCount,
            string rootType,
            HostedProducerOperationEntry? callerRoot,
            string? work,
            string? effect,
            string? recoveryEffect,
            HostedProducerOperationEntry entry,
            out LeaseHandoffRoot? root,
            out string? origin)
        {
            origin = null;

            root = LeaseHandoffRootFor(entry);

            if (root is null)
            {
                return "the declared root is not usable";
            }

            if (targetCount != 1)
            {
                return "the call does not resolve to exactly the root";
            }

            if (callerRoot is not { Authority: HostedProducerAuthorityKind.OrdinaryHostedWork } || callerRoot.WorkKind != entry.WorkKind)
            {
                return "the caller's Work kind is not the root's";
            }

            if (rootType != root.RootType)
            {
                return "the caller is a root of another service";
            }

            // No root of a handoff service reaches a recovery handler, so a caller that carries a recovery tuple is not one. The refusal only
            // removes authority.
            if (caller.RecoveryContext is not null)
            {
                return "the caller carries a recovery context";
            }

            if (caller.Syntax is not MethodDeclarationSyntax || call.Ancestors().TakeWhile(ancestor => ancestor != caller.Syntax).Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            {
                return "the call is not in the caller's own method body";
            }

            // The open group of the caller is pinned by the effect-open fixture. A recovery effect opens only at the exact settle call of the
            // reconciler, whose try/finally and disposition shape the recovery fixtures own; it is the same refusal and only removes authority.
            if (effect is not null || recoveryEffect is not null)
            {
                return "an external-effect group is open at the call";
            }

            if (work is null)
            {
                return "the caller holds no live Work at the call";
            }

            if (AwaitRefusal(caller, call) is { } notAwaited)
            {
                return notAwaited;
            }

            int formalOrdinal = root.Formal.Ordinal;

            // A by-reference formal never reaches this check: LeaseHandoffFormal refuses it as a declaration, so the root is not usable.
            (ExpressionSyntax Expression, IParameterSymbol? Parameter, RefKind RefKind)[] leases = [.. AdmissionArguments(caller, call).Where(argument => argument.Parameter?.Ordinal == formalOrdinal)];

            if (leases is not [{ Parameter: not null, RefKind: RefKind.None } lease] || lease.Expression is DeclarationExpressionSyntax)
            {
                return "the lease argument is not exactly one by-value argument";
            }

            string? acquisition = MustAliasLeaseOrigin(caller, lease.Expression);

            if (acquisition is null)
            {
                return "the lease argument is not a must-alias of one acquisition";
            }

            if (WorkOrigin(work) != acquisition)
            {
                return "the Work the caller holds is not the Work of the lease argument";
            }

            if (AdmissionEndedBefore(caller, acquisition, call.Span.End, []))
            {
                return "the lease has ended before the call begins";
            }

            // The canonical root binds its one formal. Any other argument, and the receiver, that may hold the lease would reach the root
            // unbound, outside the escape and lifetime rules the cut grants Work under; the in-line traversal judged them.
            if (AdmissionArguments(caller, call).Any(argument => argument.Parameter?.Ordinal != formalOrdinal && MayHoldAdmissionAlias(caller, argument.Expression)))
            {
                return "another argument may hold the caller's lease";
            }

            if (AdmissionReceiver(caller, call) is { } receiver && MayHoldAdmissionAlias(caller, receiver))
            {
                return "the receiver may hold the caller's lease";
            }

            // The caller side of Formal Preservation: a closure that mentions the lease can end it at a time no position names.
            if (LeaseMentionedInClosure(caller, acquisition))
            {
                return "a closure of the caller mentions the caller's lease";
            }

            // The root's lifetime proof is positional, so a pass of the call that comes after the root ended its formal is not covered by it.
            if (CallMayRepeat(caller, call) && !FormalPreserved(entry))
            {
                return "the call sits in a loop and the root does not preserve its formal";
            }

            origin = acquisition;

            return null;
        }

        // `await call` or `await call.ConfigureAwait(constant)` and nothing else: no conditional, coalesce, switch, cast, stored task or discard.
        // A ConfigureAwait argument is evaluated after the root has started and before the await, so it runs while the root does and could
        // end the lease then; only a compile-time constant evaluates nothing. Null means the call is awaited that way.
        private static string? AwaitRefusal(AuthoredMember caller, InvocationExpressionSyntax call)
        {
            SyntaxNode expression = call;

            while (true)
            {
                if (expression.Parent is ParenthesizedExpressionSyntax parentheses)
                {
                    expression = parentheses;
                }
                else if (expression.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait", Parent: InvocationExpressionSyntax configured }
                    && caller.Model.GetSymbolInfo(configured).Symbol is IMethodSymbol method && IsExactFrameworkConfigureAwait(method))
                {
                    if (configured.ArgumentList.Arguments is not [{ Expression: { } option }] || !caller.Model.GetConstantValue(option).HasValue)
                    {
                        return "the await configures the call with an argument that is not a constant";
                    }

                    expression = configured;
                }
                else
                {
                    return expression.Parent is AwaitExpressionSyntax ? null : "the call is not directly awaited";
                }
            }
        }

        // Another argument or the receiver may hold the lease: a value of any type that is not closed pure data or a token, computed from an
        // expression that mentions the lease (the origin walk is a may-alias walk). A holder built from the lease, a second parameter given
        // the lease, and a receiver constructed from it are all such values.
        private bool MayHoldAdmissionAlias(AuthoredMember member, ExpressionSyntax expression)
        {
            if (AdmissionOrigins(member, expression, new HashSet<ISymbol>(SymbolEqualityComparer.Default)).Count == 0)
            {
                return false;
            }

            ITypeSymbol? type = member.Model.GetTypeInfo(expression).Type;

            return type is null
                || !IsHandleIncapableValueType(type, member.Model.Compilation)
                    && !IsExactFrameworkType(type, "System.Threading.CancellationToken", typeof(CancellationToken).Assembly.GetName());
        }

        // The call can run again: it sits in a loop, or a jump after it lands before it.
        private static bool CallMayRepeat(AuthoredMember caller, InvocationExpressionSyntax call)
        {
            if (call.Ancestors().TakeWhile(ancestor => ancestor != caller.Syntax).Any(static ancestor => ancestor is ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax))
            {
                return true;
            }

            foreach (GotoStatementSyntax jump in caller.Syntax.DescendantNodes().OfType<GotoStatementSyntax>().Where(jump => jump.SpanStart >= call.Span.End))
            {
                if (jump.IsKind(SyntaxKind.GotoStatement))
                {
                    if (jump.Expression is null
                        || caller.Model.GetSymbolInfo(jump.Expression).Symbol is not ILabelSymbol label
                        || label.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree != call.SyntaxTree || reference.Span.Start <= call.SpanStart))
                    {
                        return true;
                    }
                }
                else if (call.Ancestors().OfType<SwitchStatementSyntax>().Any(switchStatement => switchStatement.Span.Contains(jump.Span)))
                {
                    return true;
                }
            }

            return false;
        }

        // Formal Preservation, computed by the root and consulted by every caller's lifetime. The formal survives its root's closure only if
        // nothing in it disposes, returns or stores the lease, hands it to a callee the lifetime cannot follow, or captures it in a closure.
        // A root whose proof is cyclic or incomplete does not preserve it.
        private bool FormalPreserved(HostedProducerOperationEntry entry)
        {
            if (leaseFormalPreservation.TryGetValue(entry.OperationId, out bool known))
            {
                return known;
            }

            if (!leaseFormalPreservationRunning.Add(entry.OperationId))
            {
                return false;
            }

            try
            {
                bool preserved = LeaseHandoffRootFor(entry) is { } root
                    && !AdmissionEndedBefore(root.Member, root.Origin, int.MaxValue, [])
                    && !LeaseFormalCaptured(root.Member, root.Origin, []);

                leaseFormalPreservation[entry.OperationId] = preserved;

                return preserved;
            }
            finally
            {
                leaseFormalPreservationRunning.Remove(entry.OperationId);
            }
        }

        // The lifetime walk follows dispose, return, store and by-reference hand-outs; a closure that mentions the lease holds it past any
        // position the walk can name, so a nested callable that reads a lease-carrying variable of its owner ends the proof.
        private bool LeaseFormalCaptured(AuthoredMember member, string origin, HashSet<GraphMemberIdentity> visited)
        {
            if (!visited.Add(MemberIdentity(member)))
            {
                return false;
            }

            if (LeaseMentionedInClosure(member, origin))
            {
                return true;
            }

            foreach (InvocationExpressionSyntax call in member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (AdmissionArguments(member, call).Any(argument => argument.Expression is not DeclarationExpressionSyntax && LeaseCarrier(member, origin, argument.Expression))
                    && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                    && Resolve(method, member.Model.Compilation) is { } target
                    && LeaseFormalCaptured(BindAdmissionArguments(member, call, target), origin, visited))
                {
                    return true;
                }
            }

            return false;
        }

        private bool LeaseCarrier(AuthoredMember member, string origin, ExpressionSyntax expression) =>
            AdmissionOrigins(member, expression, new HashSet<ISymbol>(SymbolEqualityComparer.Default)).Contains(origin) && MayCarryAdmissionHandle(member, expression);

        // A nested callable that reads a lease-carrying variable of its owner holds the lease past any position the walk can name.
        private bool LeaseMentionedInClosure(AuthoredMember member, string origin)
        {
            foreach (SyntaxNode node in member.Syntax.DescendantNodes().Where(static node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            {
                foreach (IdentifierNameSyntax reference in node.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    ISymbol? symbol = member.Model.GetSymbolInfo(reference).Symbol;

                    if (symbol is not (ILocalSymbol or IParameterSymbol)
                        || symbol.DeclaringSyntaxReferences.Any(declaration => declaration.SyntaxTree == node.SyntaxTree && node.Span.Contains(declaration.Span)))
                    {
                        continue;
                    }

                    if (LeaseCarrier(member, origin, reference))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // The Handoff Caller Census. Every syntactic reference to a declared handoff root's method in analysed source (the method, an
        // interface slot it implements, a method it overrides) must be an invocation some traversal proved as a cut edge. A reference that is
        // not an invocation (a method group, a delegate), a call that was refused, and a call no traversal reached are all unanalysed.
        // Callers reached only by request paths that no root traverses are therefore not silently accepted: a call site that no primary
        // root proves fails here, whatever reaches it at run time, and the shared chain above the one proved site is pinned by an exact
        // reviewed list in the architecture tests.
        private void CheckLeaseHandoffCallerCensus()
        {
            foreach (HostedProducerOperationEntry entry in declaredOperations.Where(static entry => entry.LeaseFormal is not null))
            {
                if (LeaseHandoffRootFor(entry) is not { } root)
                {
                    continue;
                }

                if (!provenLeaseHandoffCalls.Any(key => key.StartsWith(entry.OperationId + "|", StringComparison.Ordinal)))
                {
                    diagnostics.Add(new(LeaseHandoffRootUnboundCode, entry.OperationId, "No root proved a call to this handoff root, so its formal has no binding."));
                }

                IMethodSymbol handoff = root.Member.Symbol.OriginalDefinition;

                bool IsContract(IMethodSymbol referenced)
                {
                    IMethodSymbol definition = (referenced.ReducedFrom ?? referenced).OriginalDefinition;

                    if (SymbolEqualityComparer.Default.Equals(definition, handoff))
                    {
                        return true;
                    }

                    if (definition.ContainingType.TypeKind == TypeKind.Interface
                        && handoff.ContainingType.FindImplementationForInterfaceMember(definition) is IMethodSymbol implementation
                        && SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, handoff))
                    {
                        return true;
                    }

                    for (IMethodSymbol? overridden = handoff.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
                    {
                        if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, definition))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                foreach ((SyntaxTree tree, SemanticModel model) in semanticModels.OrderBy(static pair => pair.Key.FilePath, StringComparer.Ordinal).Select(static pair => (pair.Key, pair.Value)))
                {
                    foreach (SimpleNameSyntax name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>().Where(name => name.Identifier.ValueText == entry.Member))
                    {
                        SymbolInfo info = model.GetSymbolInfo(name);

                        IMethodSymbol? referenced = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault(IsContract);

                        if (referenced is null || !IsContract(referenced) || name.Ancestors().OfType<InvocationExpressionSyntax>().Any(invocation => model.GetOperation(invocation) is INameOfOperation))
                        {
                            continue;
                        }

                        InvocationExpressionSyntax? call = name.Parent switch
                        {
                            InvocationExpressionSyntax direct when direct.Expression == name => direct,
                            MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax access } member when member.Name == name && access.Expression == member => access,
                            MemberBindingExpressionSyntax { Parent: InvocationExpressionSyntax conditional } binding when binding.Name == name && conditional.Expression == binding => conditional,
                            _ => null,
                        };

                        if (call is not null && provenLeaseHandoffCalls.Contains(LeaseHandoffCallKey(entry, call)))
                        {
                            continue;
                        }

                        diagnostics.Add(new(LeaseHandoffCallerUnanalysedCode, Location(call ?? (SyntaxNode)name),
                            entry.OperationId + "; " + (call is null ? "a reference that is not an invocation" : "no root proved this call as a cut edge") + "."));
                    }
                }
            }
        }
    }
}
