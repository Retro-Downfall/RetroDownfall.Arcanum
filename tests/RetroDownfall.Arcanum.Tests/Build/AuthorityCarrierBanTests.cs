using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Collections;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Carried Admission passes one work lease by value from its mint to every use. Outside the admission gate nothing
/// stores that lease or the external-effect group begun on it, captures either in a closure, binds either to a delegate,
/// or hands either to a callee that could keep it. This is also what backs the hosted-producer inventory's documented limits
/// on a handle written to heap storage (a group stashed in an instance field and read back through a closed key, a static
/// alias through a local or a reference, a delegate that carries it): the inventory cannot follow those shapes, so
/// production is not allowed to contain them.
/// </summary>
[Collection(HostedProducerAnalysisCollection.Name)]
[Trait("Category", "HostedProducerAnalysis")]
public sealed class AuthorityCarrierBanTests(ITestOutputHelper output)
{
    private const string Data = AuthorityCarrierScanner.Namespace;

    private const string Store = Data + ".LongRest.LongRestDiscoveryStore";

    private const string Coordinator = Data + ".CovenantErasureCoordinator";

    /// <summary>
    /// Every carrier of a gate-minted handle in production, outside the gate itself, reviewed one by one.
    /// </summary>
    /// <remarks>
    /// The request lease, the closing owner and the exclusive closed lease are kept where the request and the erasure close
    /// have to carry them across their own lifetime: the request scope holds the lease it admitted until the container
    /// disposes the scope, and the erasure closure and the closed-period authority hold the closing owner and the closed
    /// lease the whole closed period. None of them is a work lease or an external-effect group.
    ///
    /// <para>The two Long Rest store closures are the one place a work lease is captured. Each is the async body of
    /// <c>SqliteBusyRetry.ExecuteAsync</c>, awaited in the statement that creates it, so the lease is lent for the call and
    /// never outlives it: <see cref="The_pinned_closure_captures_of_the_work_lease_are_awaited_inline_by_the_busy_retry"/> pins that shape,
    /// and any other capture of a work lease, or a second closure in either method, is a new finding.</para>
    /// </remarks>
    internal static readonly string[] ReviewedCensus =
    [
        "closure|" + Store + ".ReadmitCleanAsync{closure#0:admission}|IGrimoireWorkLease",
        "closure|" + Store + ".ReconcileCompletedCheckpointAsync{closure#0:admission}|IGrimoireWorkLease",
        "field|" + Data + ".Covenant.CovenantClosedPeriodAuthority._closed|IGrimoireExclusiveClosedLease",
        "field|" + Data + ".GrimoireRequestAdmissionScope._lease|IGrimoireRequestLease",
        "primary-constructor|" + Coordinator + ".CovenantGrimoireClosure(closed)|IGrimoireExclusiveClosedLease",
        "primary-constructor|" + Coordinator + ".CovenantGrimoireClosure(closingOwner)|IGrimoireClosingOwner",
        "property|" + Coordinator + ".CovenantGrimoireClosure.Closed|IGrimoireExclusiveClosedLease",
        "property|" + Coordinator + ".GrimoireClosureSlot.ClosedLease|IGrimoireExclusiveClosedLease",
        "property|" + Coordinator + ".GrimoireClosureSlot.ClosingOwner|IGrimoireClosingOwner",
        "property|" + Data + ".GrimoireRequestAdmissionScope.Lease|IGrimoireRequestLease",
    ];

    [Fact]
    public void Production_carries_no_authority_handle_outside_the_gate_except_the_reviewed_census()
    {
        string[] actual = [.. new AuthorityCarrierScanner([.. HostedGrimoireProducerInventory.ProductionCompilations]).Scan().Select(static finding => finding.ToString())];

        foreach (string line in actual)
        {
            output.WriteLine("AUTHORITY_CARRIER " + line);
        }

        string[] unreviewed = [.. actual.Except(ReviewedCensus, StringComparer.Ordinal)];

        string[] stale = [.. ReviewedCensus.Except(actual, StringComparer.Ordinal)];

        Assert.True(unreviewed.Length == 0 && stale.Length == 0,
            "An authority handle is kept, captured or lent where it is not reviewed.\nUnreviewed:\n" + string.Join('\n', unreviewed)
            + "\nReviewed but gone:\n" + string.Join('\n', stale));

        Assert.Equal(ReviewedCensus.Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void The_reviewed_census_never_excuses_a_work_lease_or_a_group_except_the_two_pinned_inline_closures()
    {
        foreach (string entry in ReviewedCensus)
        {
            string authority = entry[(entry.LastIndexOf('|') + 1)..];

            if (AuthorityCarrierScanner.BannedAuthorities.Contains(authority, StringComparer.Ordinal))
            {
                Assert.StartsWith("closure|" + Store + ".", entry, StringComparison.Ordinal);

                Assert.Contains(entry.Split('|')[1], new[]
                {
                    Store + ".ReadmitCleanAsync{closure#0:admission}",
                    Store + ".ReconcileCompletedCheckpointAsync{closure#0:admission}",
                });
            }
            else
            {
                Assert.Contains(authority, AuthorityCarrierScanner.ReviewedAuthorities);
            }
        }

        Assert.Equal(2, ReviewedCensus.Count(static entry => entry.EndsWith("|IGrimoireWorkLease", StringComparison.Ordinal)));

        Assert.DoesNotContain(ReviewedCensus, static entry => entry.EndsWith("|IGrimoireExternalEffectGroup", StringComparison.Ordinal));
    }

    [Fact]
    public void The_pinned_closure_captures_of_the_work_lease_are_awaited_inline_by_the_busy_retry()
    {
        CSharpCompilation infrastructure = Assert.Single(HostedGrimoireProducerInventory.ProductionCompilations,
            static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        INamedTypeSymbol store = Assert.IsAssignableFrom<INamedTypeSymbol>(infrastructure.GetTypeByMetadataName(Store));

        foreach (string name in new[] { "ReadmitCleanAsync", "ReconcileCompletedCheckpointAsync" })
        {
            IMethodSymbol method = Assert.Single(store.GetMembers(name).OfType<IMethodSymbol>());

            MethodDeclarationSyntax syntax = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(method.DeclaringSyntaxReferences).GetSyntax());

            SemanticModel model = infrastructure.GetSemanticModel(syntax.SyntaxTree);

            ParenthesizedLambdaExpressionSyntax closure = Assert.Single(syntax.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>());

            Assert.Contains(closure.Modifiers, static modifier => modifier.IsKind(SyntaxKind.AsyncKeyword));

            // The closure is the first argument of the busy retry, and the call is awaited and returned by the same statement.
            ArgumentSyntax argument = Assert.IsType<ArgumentSyntax>(closure.Parent);

            InvocationExpressionSyntax retry = Assert.IsType<InvocationExpressionSyntax>(argument.Parent!.Parent);

            Assert.Same(closure, retry.ArgumentList.Arguments[0].Expression);

            IMethodSymbol called = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(retry).Symbol);

            Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.SqliteBusyRetry.ExecuteAsync", called.ContainingType.ToDisplayString() + "." + called.Name);

            MemberAccessExpressionSyntax configure = Assert.IsType<MemberAccessExpressionSyntax>(retry.Parent);

            Assert.Equal("ConfigureAwait", configure.Name.Identifier.ValueText);

            AwaitExpressionSyntax awaited = Assert.IsType<AwaitExpressionSyntax>(Assert.IsType<InvocationExpressionSyntax>(configure.Parent).Parent);

            Assert.IsType<ReturnStatementSyntax>(awaited.Parent);

            // The retry runs the closure and nothing else: the delegate it is given is only ever invoked, awaited where it is invoked.
            INamedTypeSymbol retryType = Assert.IsAssignableFrom<INamedTypeSymbol>(called.ContainingType);

            foreach (IMethodSymbol runner in retryType.GetMembers("ExecuteAsync").OfType<IMethodSymbol>())
            {
                AssertTheRunnerOnlyInvokesAndAwaitsTheDelegate(infrastructure, retryType, runner);
            }
        }
    }

    // The pin above is only as good as what it refuses. A runner that invokes and awaits the delegate where it invokes it, directly or
    // through the one forwarding closure it hands back to its own generic overload, is the shape the lease is lent under; a runner that
    // runs the delegate on another task, keeps it, or calls it without awaiting it would let the lease outlive the awaited call.
    [Theory]
    [InlineData("Awaiting", true)]
    [InlineData("Forwarding", true)]
    [InlineData("TaskRun", false)]
    [InlineData("Stored", false)]
    [InlineData("Unawaited", false)]
    [InlineData("ForwardedElsewhere", false)]
    [InlineData("TwoClosures", false)]
    public void The_busy_retry_pin_accepts_an_awaited_invocation_and_refuses_anything_that_lets_the_delegate_outlive_it(string runnerName, bool accepted)
    {
        CSharpCompilation compilation = Synthetic("""
            public static class Retry
            {
                private static Func<Task>? s_kept;

                public static async Task<T> Awaiting<T>(Func<Task<T>> action, CancellationToken ct = default)
                {
                    while (true)
                    {
                        try
                        {
                            return await action().ConfigureAwait(false);
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    }
                }

                public static async Task Forwarding(Func<Task> action)
                {
                    _ = await Forwarding<bool>(async () =>
                    {
                        await action().ConfigureAwait(false);

                        return true;
                    }).ConfigureAwait(false);
                }

                public static Task<T> Forwarding<T>(Func<Task<T>> action) => action();

                public static Task<T> TaskRun<T>(Func<Task<T>> action) => Task.Run(() => action());

                public static Task Stored(Func<Task> action)
                {
                    s_kept = action;

                    return Task.CompletedTask;
                }

                public static Task Unawaited(Func<Task> action)
                {
                    _ = action();

                    return Task.CompletedTask;
                }

                public static async Task ForwardedElsewhere(Func<Task> action)
                {
                    await Task.Run(async () =>
                    {
                        await action().ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }

                public static async Task TwoClosures(Func<Task> action)
                {
                    await Task.Run(() => Task.Run(async () =>
                    {
                        await action().ConfigureAwait(false);
                    })).ConfigureAwait(false);
                }
            }
            """);

        INamedTypeSymbol retry = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("Fixture.Retry"));

        IMethodSymbol runner = retry.GetMembers(runnerName).OfType<IMethodSymbol>().First(static method => method.Parameters.Length > 0 && method.DeclaringSyntaxReferences.Length == 1
            && method.Parameters[0].Type is INamedTypeSymbol { Name: "Func" });

        // The forwarding runner hands its closure to the retry's own generic overload, which is named ExecuteAsync in production.
        Action check = () => AssertTheRunnerOnlyInvokesAndAwaitsTheDelegate(compilation, retry, runner, runnerName == "Forwarding" ? "Forwarding" : "ExecuteAsync");

        if (accepted)
        {
            check();
        }
        else
        {
            // A failed assertion of the pin, not a defect of the fixture.
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(check);
        }
    }

    // The delegate is the one parameter typed Func<Task> or Func<Task<T>>, whatever its position. Every use of it is `await action().ConfigureAwait(..)`,
    // and the only closure that may hold it is the forwarding lambda handed straight back to the runner's own generic overload, awaited too:
    // a `Task.Run(() => action())` or a stored delegate would let the lease outlive the awaited call.
    private static void AssertTheRunnerOnlyInvokesAndAwaitsTheDelegate(CSharpCompilation infrastructure, INamedTypeSymbol retryType, IMethodSymbol runner, string overload = "ExecuteAsync")
    {
        MethodDeclarationSyntax body = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(runner.DeclaringSyntaxReferences).GetSyntax());

        SemanticModel runnerModel = infrastructure.GetSemanticModel(body.SyntaxTree);

        IParameterSymbol operation = Assert.Single(runner.Parameters, static parameter =>
            parameter.Type is INamedTypeSymbol { Name: "Func", TypeArguments: [INamedTypeSymbol { Name: "Task" }] });

        IdentifierNameSyntax[] uses = [.. body.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(name => SymbolEqualityComparer.Default.Equals(runnerModel.GetSymbolInfo(name).Symbol, operation))];

        Assert.NotEmpty(uses);

        foreach (IdentifierNameSyntax use in uses)
        {
            InvocationExpressionSyntax invocation = Assert.IsType<InvocationExpressionSyntax>(use.Parent);

            Assert.Same(use, invocation.Expression);

            AssertAwaitedWithConfigureAwait(invocation);

            AnonymousFunctionExpressionSyntax[] closures = [.. use.Ancestors().TakeWhile(ancestor => ancestor != body).OfType<AnonymousFunctionExpressionSyntax>()];

            Assert.True(closures.Length <= 1, "The delegate is held by more than one closure.");

            foreach (AnonymousFunctionExpressionSyntax closure in closures)
            {
                ArgumentSyntax argument = Assert.IsType<ArgumentSyntax>(closure.Parent);

                InvocationExpressionSyntax forwarded = Assert.IsType<InvocationExpressionSyntax>(argument.Parent!.Parent);

                Assert.Same(closure, forwarded.ArgumentList.Arguments[0].Expression);

                IMethodSymbol target = Assert.IsAssignableFrom<IMethodSymbol>(runnerModel.GetSymbolInfo(forwarded).Symbol);

                Assert.True(SymbolEqualityComparer.Default.Equals(retryType, target.ContainingType) && target.Name == overload && target.IsGenericMethod,
                    "The closure that holds the delegate is not handed to the runner's own generic overload.");

                AssertAwaitedWithConfigureAwait(forwarded);
            }
        }

        static void AssertAwaitedWithConfigureAwait(InvocationExpressionSyntax call)
        {
            MemberAccessExpressionSyntax configure = Assert.IsType<MemberAccessExpressionSyntax>(call.Parent);

            Assert.Equal("ConfigureAwait", configure.Name.Identifier.ValueText);

            Assert.IsType<AwaitExpressionSyntax>(Assert.IsType<InvocationExpressionSyntax>(configure.Parent).Parent);
        }
    }

    /// <summary>
    /// The disposable handle interfaces the gate hands out that are not authority handles, each with the reason the ban leaves it alone.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> ExcludedHandles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["IGrimoireConnectionOpenTicket"] = "One physical ordinary-open attempt, disposed synchronously by the connection initializer; it admits nothing and authorises no work.",
        ["IGrimoireExpiredLeaseAdoptionInterlock"] = "Held by the Covenant adoption for the duration of one revalidation; it blocks closing and authorises neither a request nor background work.",
    };

    [Fact]
    public void Every_disposable_handle_the_gate_mints_is_banned_reviewed_or_excluded_with_a_reason()
    {
        CSharpCompilation infrastructure = Assert.Single(HostedGrimoireProducerInventory.ProductionCompilations,
            static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        foreach (string name in AuthorityCarrierScanner.BannedAuthorities.Concat(AuthorityCarrierScanner.ReviewedAuthorities))
        {
            INamedTypeSymbol handle = Assert.IsAssignableFrom<INamedTypeSymbol>(infrastructure.GetTypeByMetadataName(Data + "." + name));

            Assert.Equal(TypeKind.Interface, handle.TypeKind);

            Assert.Contains(handle.AllInterfaces, static contract => contract.ToDisplayString() == "System.IAsyncDisposable");
        }

        INamedTypeSymbol gate = Assert.IsAssignableFrom<INamedTypeSymbol>(infrastructure.GetTypeByMetadataName(Data + ".IGrimoireConnectionAdmissionGate"));

        Assert.NotNull(infrastructure.GetTypeByMetadataName(AuthorityCarrierScanner.GateType));

        // Everything the gate and a work lease return or assign to an out parameter, however wrapped (Result, ValueTask, Task), that
        // is a disposable interface of the admission namespace: the handles that exist because the gate made them.
        SortedSet<string> minted = new(StringComparer.Ordinal);

        foreach (INamedTypeSymbol owner in new[] { gate, Assert.IsAssignableFrom<INamedTypeSymbol>(infrastructure.GetTypeByMetadataName(Data + ".IGrimoireWorkLease")) })
        {
            foreach (IMethodSymbol method in owner.GetMembers().OfType<IMethodSymbol>())
            {
                Collect(method.ReturnType);

                foreach (IParameterSymbol parameter in method.Parameters.Where(static parameter => parameter.RefKind is RefKind.Out or RefKind.Ref))
                {
                    Collect(parameter.Type);
                }
            }
        }

        void Collect(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol named)
            {
                return;
            }

            if (named.TypeKind == TypeKind.Interface && named.ContainingNamespace.ToDisplayString() == Data
                && named.AllInterfaces.Any(static contract => contract.ToDisplayString() is "System.IAsyncDisposable" or "System.IDisposable"))
            {
                minted.Add(named.Name);
            }

            foreach (ITypeSymbol argument in named.TypeArguments)
            {
                Collect(argument);
            }
        }

        string[] classified = [.. AuthorityCarrierScanner.BannedAuthorities.Concat(AuthorityCarrierScanner.ReviewedAuthorities).Concat(ExcludedHandles.Keys).Order(StringComparer.Ordinal)];

        // A handle the gate mints that nobody classified is a new authority the ban does not look at, and a classification of a handle
        // the gate no longer mints is a stale one.
        Assert.Equal(classified, minted);

        Assert.All(ExcludedHandles.Values, static reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    public static TheoryData<string> ShapeNames()
    {
        TheoryData<string> names = [];

        foreach (string name in AuthorityCarrierShapes.Shapes.Keys)
        {
            names.Add(name);
        }

        return names;
    }

    private static readonly Lazy<IReadOnlyList<MetadataReference>> PlatformReferences = new(FreshPlatformReferences);

    private static IReadOnlyList<MetadataReference> FreshPlatformReferences() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(static path => MetadataReference.CreateFromFile(path))];

    private static CSharpCompilation Synthetic(string source)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "SyntheticHolderAssembly",
            [CSharpSyntaxTree.ParseText("#nullable enable\n" + AuthorityCarrierShapes.Protocol + "\nnamespace Fixture\n{\n" + source + "\n}\n", path: "src/Fixture.cs")],
            PlatformReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return compilation;
    }

    private static string[] Scan(CSharpCompilation compilation) =>
        [.. new AuthorityCarrierScanner([compilation]).Scan().Select(static finding => finding.ToString())];

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void The_scan_reports_exactly_the_carriers_of_a_synthetic_shape(string name)
    {
        AuthorityCarrierShapes.Shape shape = AuthorityCarrierShapes.Shapes[name];

        string[] actual = Scan(Synthetic(shape.Source));

        foreach (string line in actual)
        {
            output.WriteLine("SYNTHETIC_CARRIER shape=" + name + " " + line);
        }

        Assert.Equal(shape.Expected.Order(StringComparer.Ordinal), actual);
    }

    // A compilation sees another compilation's members through a retargeting layer, so the formal a call site reports is not the
    // symbol the callee's body is read with. The handle must still be followed into a callee another compilation owns. Production
    // is always the retargeted case (every project reference is one), which Roslyn only builds when the two compilations do not share
    // their platform references: the shared case is the control, and the test fails if the retargeting it is about did not happen.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_handle_handed_to_a_callee_another_compilation_owns_is_followed_into_it(bool retargeted)
    {
        CSharpParseOptions parse = CSharpParseOptions.Default;

        IReadOnlyList<MetadataReference> lowerPlatform = retargeted ? FreshPlatformReferences() : PlatformReferences.Value;

        IReadOnlyList<MetadataReference> upperPlatform = retargeted ? FreshPlatformReferences() : PlatformReferences.Value;

        CSharpCompilation lower = CSharpCompilation.Create(
            "SyntheticLower",
            [CSharpSyntaxTree.ParseText("#nullable enable\n" + AuthorityCarrierShapes.Protocol + """

                namespace Fixture
                {
                    public static class Keep
                    {
                        private static readonly object[] s_items = new object[1];

                        public static void Park(object kept)
                        {
                            s_items[0] = kept;
                        }

                        public static void KeepGeneric<T>(T kept)
                        {
                            s_items[0] = kept!;
                        }

                        public static void Look(object seen)
                        {
                            _ = seen.ToString();
                        }
                    }

                    public sealed class Keeper(object kept)
                    {
                        public override string ToString() => kept.ToString() ?? string.Empty;
                    }

                    public sealed record Wrap(object Value);

                    public class Base
                    {
                        public virtual void Take(object value)
                        {
                        }
                    }

                    public sealed class Derived : Base
                    {
                        private static object? s_kept;

                        public override void Take(object value)
                        {
                            s_kept = value;
                        }
                    }
                }
                """, parse, path: "src/Lower.cs")],
            lowerPlatform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CSharpCompilation upper = CSharpCompilation.Create(
            "SyntheticUpper",
            [CSharpSyntaxTree.ParseText("""
                #nullable enable
                using RetroDownfall.Arcanum.Infrastructure.Data;

                namespace Fixture.Upper
                {
                    public static class Caller
                    {
                        public static void Go(IGrimoireWorkLease lease) => Fixture.Keep.Park(lease);

                        public static void GoGeneric(IGrimoireExternalEffectGroup group) => Fixture.Keep.KeepGeneric(group);

                        public static void Peek(IGrimoireWorkLease lease) => Fixture.Keep.Look(lease);

                        public static object Hold(IGrimoireWorkLease lease) => new Fixture.Keeper(lease);

                        public static object Record(IGrimoireWorkLease lease) => new Fixture.Wrap(lease);

                        public static void Dispatch(Fixture.Base target, IGrimoireWorkLease lease) => target.Take(lease);
                    }
                }
                """, parse, path: "src/Upper.cs")],
            [.. upperPlatform, lower.ToMetadataReference()],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.DoesNotContain(lower.GetDiagnostics().Concat(upper.GetDiagnostics()), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        // The upper compilation reads the lower one through a retargeting layer exactly when the platform references are not shared.
        IAssemblySymbol seen = Assert.Single(upper.SourceModule.ReferencedAssemblySymbols, static assembly => assembly.Name == "SyntheticLower");

        if (retargeted)
        {
            Assert.NotSame(lower.Assembly, seen);
        }
        else
        {
            Assert.Same(lower.Assembly, seen);
        }

        // The callees that keep the handle are reported once each, from the compilation that owns them; the one that only looks at it is not.
        Assert.Equal(
            new[]
            {
                "primary-constructor|Fixture.Keeper(kept)|IGrimoireWorkLease",
                "store|Fixture.Derived.s_kept|IGrimoireWorkLease",
                "store|Fixture.Keep.KeepGeneric.[]|IGrimoireExternalEffectGroup",
                "store|Fixture.Keep.Park.[]|IGrimoireWorkLease",
                "store|Fixture.Wrap.Value|IGrimoireWorkLease",
            },
            new AuthorityCarrierScanner([lower, upper]).Scan().Select(static finding => finding.ToString()));
    }

    [Fact]
    public void Every_shape_row_is_either_a_positive_or_a_named_negative_twin()
    {
        string[] negatives = [.. AuthorityCarrierShapes.Shapes.Where(static pair => pair.Value.Expected.Length == 0).Select(static pair => pair.Key)];

        Assert.Equal(
            new[]
            {
                "a-helper-result-used-only-locally",
                "a-lease-passed-by-parameter-only",
                "a-minted-lease-held-by-await-using",
                "a-primary-constructor-parameter-used-only-to-initialise",
                "a-record-property-that-is-not-a-handle-is-not-a-store",
                "acquisition-out-parameter-of-the-handle-type",
                "bind-members-that-take-no-handle",
                "framework-list-of-leases-that-never-escapes",
                "generic-helper-that-only-reads",
                "lambda-capturing-this-of-an-ordinary-class",
                "numeric-reads-of-a-carrier-are-not-carriers",
                "pattern-and-loop-variables-used-only-locally",
                "primary-constructor-base-argument-that-is-only-looked-at",
                "reviewed-pass-throughs",
                "self-referential-generic-types-terminate",
                "type-parameter-constrained-to-an-unrelated-interface",
                "virtual-dispatch-to-overrides-that-only-look",
            }.Order(StringComparer.Ordinal),
            negatives.Order(StringComparer.Ordinal));

        // Every kind the scan can report is exercised by at least one positive row.
        string[] kinds = [.. AuthorityCarrierShapes.Shapes.Values.SelectMany(static shape => shape.Expected).Select(static line => line[..line.IndexOf('|')]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Assert.Equal(
            new[] { "bind", "closure", "delegate-receiver", "field", "primary-constructor", "property", "ref-store", "service", "sink", "store" },
            kinds);
    }

    [Fact]
    public void The_gate_nested_types_are_the_only_exception_and_the_exception_is_real()
    {
        // The protocol alone contains the gate with every kind of holder inside it, and reports nothing.
        Assert.Empty(Scan(Synthetic("")));

        // The same gate, renamed, is not the gate: every holder inside it is reported.
        CSharpCompilation renamed = CSharpCompilation.Create(
            "SyntheticHolderAssembly",
            [CSharpSyntaxTree.ParseText("#nullable enable\n" + AuthorityCarrierShapes.Protocol
                .Replace("class GrimoireConnectionAdmissionGate", "class SomeOtherGate", StringComparison.Ordinal)
                .Replace("(GrimoireConnectionAdmissionGate gate)", "(SomeOtherGate gate)", StringComparison.Ordinal), path: "src/Fixture.cs")],
            PlatformReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        string[] reported = Scan(renamed);

        Assert.NotEmpty(reported);

        Assert.Contains(reported, static line => line.StartsWith("field|" + Data + ".SomeOtherGate._leases|", StringComparison.Ordinal));

        Assert.Contains(reported, static line => line.StartsWith("field|" + Data + ".SomeOtherGate.WorkLease._active|", StringComparison.Ordinal));
    }

    [Fact]
    public void The_census_comparison_refuses_an_unreviewed_carrier_and_a_stale_entry()
    {
        string[] reviewed = ["field|Fixture.RequestScope._lease|IGrimoireRequestLease"];

        string[] actual = Scan(Synthetic(AuthorityCarrierShapes.Shapes["request-family-field"].Source));

        Assert.Equal(reviewed, actual);

        string[] extra = Scan(Synthetic(AuthorityCarrierShapes.Shapes["request-family-field"].Source + AuthorityCarrierShapes.Shapes["static-field"].Source));

        Assert.Equal(
            ["field|Fixture.Ambient.s_held|IGrimoireWorkLease"],
            extra.Except(reviewed, StringComparer.Ordinal));

        Assert.Equal(
            reviewed,
            reviewed.Except(Scan(Synthetic(AuthorityCarrierShapes.Shapes["a-lease-passed-by-parameter-only"].Source)), StringComparer.Ordinal));
    }
}
