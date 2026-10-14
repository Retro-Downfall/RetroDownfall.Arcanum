using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Collections;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The Admitted Entry Census. Carried Admission has one lease spine, and these are its doors: every call that starts, carries
/// or readmits a Long Rest sweep names exactly the members that are allowed to, so a new caller of an admitted entry, a second
/// place that mints a Long Rest lease, or a second reader of a parked sweep is a failing test rather than a review comment.
/// </summary>
/// <remarks>
/// Each door is matched on the exact interface symbol (and on the implementations of it, so a call through the concrete type
/// or a method group is not a way around the pin), never on a method name. The hosted-producer inventory proves what the
/// hosted worker's own graph does with the lease; it says nothing about who may enter the spine from outside, and the Session
/// shell is not one of its roots.
/// </remarks>
[Collection(HostedProducerAnalysisCollection.Name)]
[Trait("Category", "HostedProducerAnalysis")]
public sealed class AdmittedEntryCensusTests(ITestOutputHelper output)
{
    private const string Infrastructure = "RetroDownfall.Arcanum.Infrastructure";

    private const string Worker = Infrastructure + ".Hosting.LongRestSweepHostedService";

    private const string Coordinator = Infrastructure + ".Operations.LongRest.LongRestSweepCoordinator";

    private const string Gate = Infrastructure + ".Data.IGrimoireConnectionAdmissionGate";

    private const string WorkKind = Infrastructure + ".Data.GrimoireWorkKind";

    [Fact]
    public void ResumeAdmittedAsync_is_called_only_from_the_hosted_workers_run_one()
    {
        // The Session shell mints its own lease inline and joins the private core under it; it never calls the hosted port,
        // so the hosted port has exactly one caller. The shell and the port share the core, which is pinned next.
        Assert.Equal(
            [Worker + ".RunOneAsync"],
            Census.Callers(Production(), Infrastructure + ".Operations.LongRest.ILongRestAdmittedSweep", "ResumeAdmittedAsync"));

        Assert.Equal(
            [Coordinator],
            Census.Implementations(Production(), Infrastructure + ".Operations.LongRest.ILongRestAdmittedSweep"));
    }

    [Fact]
    public void The_sweep_core_is_entered_only_by_the_session_shell_and_the_hosted_port()
    {
        Assert.Equal(
            [Coordinator + ".ResumeAdmittedAsync", Coordinator + ".ResumeAsync"],
            Census.Callers(Production(), Coordinator, "ResumeCoreAsync"));
    }

    [Fact]
    public void ProcessAdmittedAsync_is_called_only_from_the_owned_run()
    {
        Assert.Equal(
            [Coordinator + ".RunOwnedAsync"],
            Census.Callers(Production(), Infrastructure + ".Operations.LongRest.ILongRestAdmittedMaintenance", "ProcessAdmittedAsync"));

        Assert.Equal(
            ["RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance"],
            Census.Implementations(Production(), Infrastructure + ".Operations.LongRest.ILongRestAdmittedMaintenance"));
    }

    [Fact]
    public void The_clean_readmission_and_the_completed_checkpoint_proof_are_called_only_from_the_owned_run()
    {
        Assert.Equal(
            [Coordinator + ".RunOwnedAsync"],
            Census.Callers(Production(), Infrastructure + ".Data.LongRest.ILongRestCleanReadmission", "ReadmitCleanAsync"));

        Assert.Equal(
            [Coordinator + ".RunOwnedAsync"],
            Census.Callers(Production(), Infrastructure + ".Data.LongRest.ILongRestCompletedCheckpointReconciliation", "ReconcileCompletedCheckpointAsync"));
    }

    [Fact]
    public void The_session_resume_is_called_only_from_the_campaign_rollup_turn_preparer()
    {
        const string port = "RetroDownfall.Arcanum.Core.LongRest.ILongRestSweepCoordinator";

        Assert.Equal(
            ["RetroDownfall.Arcanum.Api.Intelligence.CampaignRollupTurnPreparer.PrepareLiveAsync"],
            Census.Callers(Production(), port, "ResumeAsync"));

        Assert.Equal([Coordinator], Census.Implementations(Production(), port));
    }

    [Fact]
    public void A_Long_Rest_work_lease_is_minted_only_by_the_hosted_workers_discovery_and_run_one_and_the_session_shell()
    {
        string[] minted = Census.Acquisitions(Production(), Gate, WorkKind, "LongRestSweep");

        foreach (string caller in minted)
        {
            output.WriteLine("LONG_REST_LEASE_MINT " + caller);
        }

        Assert.Equal(
            new[] { Worker + ".FindNextPendingAsync", Worker + ".RunOneAsync", Coordinator + ".ResumeAsync" }.Order(StringComparer.Ordinal),
            minted);
    }

    [Fact]
    public void Atomic_readmission_is_called_only_from_the_coordinator_core()
    {
        Assert.Equal(
            [Coordinator + ".ResumeCoreAsync"],
            Census.Callers(Production(), Infrastructure + ".Data.LongRest.ILongRestParkedReadmission", "TryReadmitParkedAsync"));

        Assert.Equal(
            [Infrastructure + ".Data.LongRest.LongRestSweepStore"],
            Census.Implementations(Production(), Infrastructure + ".Data.LongRest.ILongRestParkedReadmission"));
    }

    [Fact]
    public void The_census_is_not_vacuous_and_matches_a_door_only_by_its_exact_symbol()
    {
        // A door that nothing calls would pass every pin above; each door in this census has a caller in production.
        IReadOnlyList<Compilation> production = Production();

        Assert.All(
            new[]
            {
                (Infrastructure + ".Operations.LongRest.ILongRestAdmittedSweep", "ResumeAdmittedAsync"),
                (Infrastructure + ".Operations.LongRest.ILongRestAdmittedMaintenance", "ProcessAdmittedAsync"),
                (Infrastructure + ".Data.LongRest.ILongRestParkedReadmission", "TryReadmitParkedAsync"),
                (Infrastructure + ".Data.LongRest.ILongRestCleanReadmission", "ReadmitCleanAsync"),
                (Coordinator, "ResumeCoreAsync"),
            },
            door => Assert.NotEmpty(Census.Callers(production, door.Item1, door.Item2)));
    }

    private static IReadOnlyList<Compilation> Production() => [.. HostedGrimoireProducerInventory.ProductionCompilations];

    // The protocol, the doors and the callers of the synthetic assembly. Every caller below is a distinct way of reaching a door.
    private const string Synthetic = """
        #nullable enable
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Fixture.Data
        {
            public enum GrimoireWorkKind { Other = 4, LongRestSweep = 17 }

            public interface IGrimoireWorkLease : IAsyncDisposable
            {
            }

            public interface IGrimoireConnectionAdmissionGate
            {
                bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease? lease);
            }
        }

        namespace Fixture.LongRest
        {
            using Fixture.Data;

            public interface ILongRestAdmittedSweep
            {
                Task ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct);
            }

            public interface IUnrelatedPort
            {
                Task ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct);
            }

            // A second interface that the same implementation method satisfies: a call through it is a call of the door.
            public interface IAlias
            {
                Task ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct);
            }

            public delegate bool Minter(GrimoireWorkKind kind, out IGrimoireWorkLease? lease);

            public sealed class Coordinator : ILongRestAdmittedSweep, IAlias
            {
                public Task ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct) => Task.CompletedTask;

                private Task Core() => Task.CompletedTask;

                public Task Shell() => Core();
            }

            public sealed class Unrelated : IUnrelatedPort
            {
                public Task ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct) => Task.CompletedTask;
            }

            public sealed class Second : ILongRestAdmittedSweep
            {
                Task ILongRestAdmittedSweep.ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct) => Task.CompletedTask;
            }

            public sealed class Outer
            {
                // A second implementation of the door that is nested in some other class.
                private sealed class Hidden : ILongRestAdmittedSweep
                {
                    public Task ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct) => Task.CompletedTask;
                }
            }

            public sealed class Worker(ILongRestAdmittedSweep sweep, IGrimoireConnectionAdmissionGate gate)
            {
                public async Task RunOneAsync(IGrimoireWorkLease lease)
                {
                    gate.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? admitted);

                    await sweep.ResumeAdmittedAsync(Guid.Empty, lease, default);
                }
            }

            public sealed class Rogue(ILongRestAdmittedSweep sweep, Coordinator concrete, IAlias alias, IGrimoireConnectionAdmissionGate gate, GrimoireWorkKind chosen)
            {
                public Task ViaInterface(IGrimoireWorkLease lease) => sweep.ResumeAdmittedAsync(Guid.Empty, lease, default);

                public Task ViaAlias(IGrimoireWorkLease lease) => alias.ResumeAdmittedAsync(Guid.Empty, lease, default);

                public Task ViaClass(IGrimoireWorkLease lease) => concrete.ResumeAdmittedAsync(Guid.Empty, lease, default);

                public Func<Guid, IGrimoireWorkLease, CancellationToken, Task> ViaMethodGroup() => sweep.ResumeAdmittedAsync;

                public Task ViaLambda(IGrimoireWorkLease lease) => Task.Run(() => sweep.ResumeAdmittedAsync(Guid.Empty, lease, default));

                public Task ViaExplicitImplementation(Second second, IGrimoireWorkLease lease) => ((ILongRestAdmittedSweep)second).ResumeAdmittedAsync(Guid.Empty, lease, default);

                public void Mint() => gate.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out _);

                public void MintOther() => gate.TryAcquireWorkLease(GrimoireWorkKind.Other, out _);

                public void MintChosen() => gate.TryAcquireWorkLease(chosen, out _);

                private static void Use(GrimoireWorkKind kind, Minter minter) => minter(GrimoireWorkKind.LongRestSweep, out _);

                // Invoked elsewhere, with a kind this scan cannot read: the first argument of the call that carries it is another kind.
                public void MintBareMethodGroup() => Use(GrimoireWorkKind.Other, gate.TryAcquireWorkLease);

                public string MintNamed() => nameof(gate.TryAcquireWorkLease);
            }

            public sealed class Innocent(IUnrelatedPort port, Unrelated unrelated)
            {
                public Task Other(IGrimoireWorkLease lease) => port.ResumeAdmittedAsync(Guid.Empty, lease, default);

                public Task Direct(IGrimoireWorkLease lease) => unrelated.ResumeAdmittedAsync(Guid.Empty, lease, default);

                public string Named() => nameof(ILongRestAdmittedSweep.ResumeAdmittedAsync);
            }
        }
        """;

    private static readonly Lazy<IReadOnlyList<MetadataReference>> PlatformReferences = new(static () =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(static path => MetadataReference.CreateFromFile(path))]);

    private static IReadOnlyList<Compilation> SyntheticCompilation()
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "SyntheticEntries",
            [CSharpSyntaxTree.ParseText(Synthetic, path: "src/Fixture.cs")],
            PlatformReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return [compilation];
    }

    [Fact]
    public void The_census_finds_every_way_of_reaching_a_door_and_only_that_door()
    {
        IReadOnlyList<Compilation> synthetic = SyntheticCompilation();

        // Through the interface, through another interface the same method satisfies, through the concrete type, as a method group, inside
        // a lambda and through an explicit implementation: each is reported, once per reference, against the member that contains it.
        Assert.Equal(
            new[]
            {
                "Fixture.LongRest.Rogue.ViaAlias",
                "Fixture.LongRest.Rogue.ViaClass",
                "Fixture.LongRest.Rogue.ViaExplicitImplementation",
                "Fixture.LongRest.Rogue.ViaInterface",
                "Fixture.LongRest.Rogue.ViaLambda",
                "Fixture.LongRest.Rogue.ViaMethodGroup",
                "Fixture.LongRest.Worker.RunOneAsync",
            },
            Census.Callers(synthetic, "Fixture.LongRest.ILongRestAdmittedSweep", "ResumeAdmittedAsync"));

        // The same name on another port is another door: it is neither reported here nor does it hide a caller.
        Assert.Equal(
            ["Fixture.LongRest.Innocent.Direct", "Fixture.LongRest.Innocent.Other"],
            Census.Callers(synthetic, "Fixture.LongRest.IUnrelatedPort", "ResumeAdmittedAsync"));

        // A reference by nameof is not a call.
        Assert.DoesNotContain("Fixture.LongRest.Innocent.Named", Census.Callers(synthetic, "Fixture.LongRest.ILongRestAdmittedSweep", "ResumeAdmittedAsync"));

        // A private member of the class is matched by the class symbol.
        Assert.Equal(["Fixture.LongRest.Coordinator.Shell"], Census.Callers(synthetic, "Fixture.LongRest.Coordinator", "Core"));

        // A second implementation is found wherever it is declared, a class nested in another class included.
        Assert.Equal(
            ["Fixture.LongRest.Coordinator", "Fixture.LongRest.Outer.Hidden", "Fixture.LongRest.Second"],
            Census.Implementations(synthetic, "Fixture.LongRest.ILongRestAdmittedSweep"));

        Assert.Equal(
            ["Fixture.LongRest.Coordinator"],
            Census.Implementations(synthetic, "Fixture.LongRest.IAlias"));
    }

    [Fact]
    public void The_census_reports_a_lease_mint_by_its_constant_kind_and_a_kind_it_cannot_resolve()
    {
        IReadOnlyList<Compilation> synthetic = SyntheticCompilation();

        // The kind-17 mints, the call whose kind is not a constant and the mint passed on as a method group: either may be a Long Rest
        // mint, so each is reported too. The kind of the call that merely receives a method group is not the kind of the mint.
        Assert.Equal(
            new[]
            {
                "Fixture.LongRest.Rogue.Mint",
                "Fixture.LongRest.Rogue.MintBareMethodGroup?",
                "Fixture.LongRest.Rogue.MintChosen?",
                "Fixture.LongRest.Worker.RunOneAsync",
            }.Order(StringComparer.Ordinal),
            Census.Acquisitions(synthetic, "Fixture.Data.IGrimoireConnectionAdmissionGate", "Fixture.Data.GrimoireWorkKind", "LongRestSweep"));

        // Another kind is not a Long Rest mint, and a reference by nameof is no mint at all.
        string[] minted = Census.Acquisitions(synthetic, "Fixture.Data.IGrimoireConnectionAdmissionGate", "Fixture.Data.GrimoireWorkKind", "LongRestSweep");

        Assert.DoesNotContain("Fixture.LongRest.Rogue.MintOther", minted);

        Assert.DoesNotContain(minted, static caller => caller.StartsWith("Fixture.LongRest.Rogue.MintNamed", StringComparison.Ordinal));
    }

    [Fact]
    public void The_census_comparison_refuses_a_caller_the_pin_does_not_name()
    {
        IReadOnlyList<Compilation> synthetic = SyntheticCompilation();

        string[] pinned = ["Fixture.LongRest.Worker.RunOneAsync"];

        string[] actual = Census.Callers(synthetic, "Fixture.LongRest.ILongRestAdmittedSweep", "ResumeAdmittedAsync");

        Assert.NotEqual(pinned, actual);

        Assert.Equal(
            ["Fixture.LongRest.Rogue.ViaAlias", "Fixture.LongRest.Rogue.ViaClass", "Fixture.LongRest.Rogue.ViaExplicitImplementation", "Fixture.LongRest.Rogue.ViaInterface", "Fixture.LongRest.Rogue.ViaLambda", "Fixture.LongRest.Rogue.ViaMethodGroup"],
            actual.Except(pinned, StringComparer.Ordinal));
    }

    /// <summary>The exact-symbol reader behind every pin above.</summary>
    internal static class Census
    {
        /// <summary>The members that name a door: call it, bind a method group to it or reach it through an implementation of it.</summary>
        internal static string[] Callers(IReadOnlyList<Compilation> compilations, string containingType, string methodName)
        {
            IMethodSymbol door = Door(compilations, containingType, methodName);

            IReadOnlyList<(INamedTypeSymbol Type, IMethodSymbol Method)> implementations = DoorImplementations(compilations, door);

            List<string> callers = [];

            foreach (Compilation compilation in compilations)
            {
                foreach (SyntaxTree tree in compilation.SyntaxTrees)
                {
                    SemanticModel model = compilation.GetSemanticModel(tree);

                    foreach (SimpleNameSyntax name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>()
                        .Where(candidate => candidate.Identifier.ValueText == methodName))
                    {
                        if (InNameof(model, name) || model.GetSymbolInfo(name).Symbol is not IMethodSymbol target || !Reaches(target, door, implementations))
                        {
                            continue;
                        }

                        callers.Add(AuthorityCarrierScanner.Qualified(Member(model.GetEnclosingSymbol(name.SpanStart))));
                    }
                }
            }

            return [.. callers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }

        /// <summary>
        /// The types that implement an interface, nested ones included, so a second implementation of a door is a finding too
        /// wherever it is declared.
        /// </summary>
        internal static string[] Implementations(IReadOnlyList<Compilation> compilations, string containingType)
        {
            INamedTypeSymbol contract = Contract(compilations, containingType);

            return [.. DeclaredTypes(compilations)
                .Where(type => type.AllInterfaces.Any(candidate => Same(candidate.OriginalDefinition, contract)))
                .Select(AuthorityCarrierScanner.Qualified).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }

        /// <summary>
        /// The members that mint a work lease of one kind, matched on the gate contract and on the constant kind of the call whose
        /// invoked name is the mint. Anything else that names the mint, a method group or a call whose kind is not a constant, may mint
        /// any kind, so it is reported with a trailing question mark.
        /// </summary>
        internal static string[] Acquisitions(IReadOnlyList<Compilation> compilations, string gateType, string kindType, string kind)
        {
            IMethodSymbol door = Door(compilations, gateType, "TryAcquireWorkLease");

            IReadOnlyList<(INamedTypeSymbol Type, IMethodSymbol Method)> implementations = DoorImplementations(compilations, door);

            INamedTypeSymbol kinds = Contract(compilations, kindType);

            object wanted = Assert.IsAssignableFrom<IFieldSymbol>(Assert.Single(kinds.GetMembers(kind))).ConstantValue!;

            List<string> minted = [];

            foreach (Compilation compilation in compilations)
            {
                foreach (SyntaxTree tree in compilation.SyntaxTrees)
                {
                    SemanticModel model = compilation.GetSemanticModel(tree);

                    foreach (SimpleNameSyntax name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>()
                        .Where(static candidate => candidate.Identifier.ValueText == "TryAcquireWorkLease"))
                    {
                        if (InNameof(model, name) || model.GetSymbolInfo(name).Symbol is not IMethodSymbol target || !Reaches(target, door, implementations))
                        {
                            continue;
                        }

                        string caller = AuthorityCarrierScanner.Qualified(Member(model.GetEnclosingSymbol(name.SpanStart)));

                        // Only the call that invokes the name has the kind as its first argument; a name passed as a method group
                        // is invoked somewhere this scan cannot see, with a kind it cannot read.
                        Optional<object?> constant = InvokedBy(name) is { ArgumentList.Arguments.Count: > 0 } call
                            ? model.GetConstantValue(call.ArgumentList.Arguments[0].Expression)
                            : default;

                        if (!constant.HasValue)
                        {
                            minted.Add(caller + "?");
                        }
                        else if (Equals(Convert.ToInt32(constant.Value), Convert.ToInt32(wanted)))
                        {
                            minted.Add(caller);
                        }
                    }
                }
            }

            return [.. minted.Order(StringComparer.Ordinal)];
        }

        // The call whose invoked expression is this name, however it is reached (x.Name(...), Name(...), x?.Name(...)).
        private static InvocationExpressionSyntax? InvokedBy(SimpleNameSyntax name) => name.Parent switch
        {
            InvocationExpressionSyntax call when call.Expression == name => call,
            MemberAccessExpressionSyntax access when access.Name == name && access.Parent is InvocationExpressionSyntax call && call.Expression == access => call,
            MemberBindingExpressionSyntax binding when binding.Name == name && binding.Parent is InvocationExpressionSyntax call && call.Expression == binding => call,
            _ => null,
        };

        private static bool InNameof(SemanticModel model, SimpleNameSyntax name) => name.Ancestors().OfType<InvocationExpressionSyntax>().Any(call =>
            call.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } && model.GetConstantValue(call).HasValue);

        // A caller is the member a closure sits in: a lambda or local function is part of the member that wrote it.
        private static ISymbol Member(ISymbol? enclosing)
        {
            ISymbol? current = enclosing;

            while (current is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
            {
                current = current.ContainingSymbol;
            }

            return current ?? throw new InvalidOperationException("A reference has no enclosing member.");
        }

        private static INamedTypeSymbol Contract(IReadOnlyList<Compilation> compilations, string metadataName) =>
            compilations.Select(compilation => compilation.GetTypeByMetadataName(metadataName))
                .First(type => type is { DeclaringSyntaxReferences.IsEmpty: false })!;

        private static IMethodSymbol Door(IReadOnlyList<Compilation> compilations, string containingType, string methodName) =>
            Assert.Single(Contract(compilations, containingType).GetMembers(methodName).OfType<IMethodSymbol>());

        // Every type the source declares, nested types included: a door's second implementation may be private to some other class.
        private static IEnumerable<INamedTypeSymbol> DeclaredTypes(IReadOnlyList<Compilation> compilations)
        {
            Stack<INamespaceOrTypeSymbol> pending = new(compilations.Select(static compilation => (INamespaceOrTypeSymbol)compilation.Assembly.GlobalNamespace));

            while (pending.Count > 0)
            {
                INamespaceOrTypeSymbol container = pending.Pop();

                foreach (INamespaceOrTypeSymbol member in container.GetMembers().OfType<INamespaceOrTypeSymbol>())
                {
                    if (member is INamedTypeSymbol { DeclaringSyntaxReferences.IsEmpty: true })
                    {
                        continue;
                    }

                    if (member is INamedTypeSymbol type)
                    {
                        yield return type;
                    }

                    pending.Push(member);
                }
            }
        }

        // A compilation sees another compilation's types through a retargeting layer, so the interface an Api type implements is
        // not the symbol object the Infrastructure compilation declares. A declared symbol is identified by the syntax that
        // declares it, which is one place however many compilations see it; anything else compares by symbol.
        private static bool Same(ISymbol? left, ISymbol? right)
        {
            if (left is null || right is null)
            {
                return false;
            }

            if (SymbolEqualityComparer.Default.Equals(left, right))
            {
                return true;
            }

            return left.DeclaringSyntaxReferences.FirstOrDefault() is { } declared
                && right.DeclaringSyntaxReferences.FirstOrDefault() is { } other
                && declared.SyntaxTree == other.SyntaxTree
                && declared.Span == other.Span;
        }

        // The method each implementing type runs for the door. Another interface member that the same method satisfies is a way
        // to the door too, so the doors are found by implementation and not only by the interface they were declared on.
        private static IReadOnlyList<(INamedTypeSymbol Type, IMethodSymbol Method)> DoorImplementations(IReadOnlyList<Compilation> compilations, IMethodSymbol door)
        {
            if (door.ContainingType.TypeKind != TypeKind.Interface)
            {
                return [];
            }

            List<(INamedTypeSymbol, IMethodSymbol)> implementations = [];

            foreach (INamedTypeSymbol type in DeclaredTypes(compilations).Where(static type => type.TypeKind is TypeKind.Class or TypeKind.Struct))
            {
                if (ImplementationIn(type, door) is { } method)
                {
                    implementations.Add((type, method));
                }
            }

            return implementations;
        }

        // The method a type runs for an interface member, found through the type's own view of the interface.
        private static IMethodSymbol? ImplementationIn(INamedTypeSymbol type, IMethodSymbol interfaceMember)
        {
            foreach (INamedTypeSymbol contract in type.AllInterfaces.Where(candidate => Same(candidate.OriginalDefinition, interfaceMember.ContainingType.OriginalDefinition)))
            {
                foreach (IMethodSymbol member in contract.GetMembers(interfaceMember.Name).OfType<IMethodSymbol>().Where(member => Same(member.OriginalDefinition, interfaceMember.OriginalDefinition)))
                {
                    if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation)
                    {
                        return implementation;
                    }
                }
            }

            return null;
        }

        // A target reaches the door when it is the door, overrides it, implements it (implicitly or explicitly), is the method a
        // type runs for the door or an override of it, or is another interface member that such a method also satisfies.
        private static bool Reaches(IMethodSymbol target, IMethodSymbol door, IReadOnlyList<(INamedTypeSymbol Type, IMethodSymbol Method)> implementations)
        {
            IMethodSymbol original = target.OriginalDefinition;

            for (IMethodSymbol? current = original; current is not null; current = current.OverriddenMethod?.OriginalDefinition)
            {
                if (Same(current, door) || implementations.Any(implementation => Same(current, implementation.Method.OriginalDefinition)))
                {
                    return true;
                }
            }

            if (original.ExplicitInterfaceImplementations.Any(implemented => Same(implemented.OriginalDefinition, door)))
            {
                return true;
            }

            if (door.ContainingType.TypeKind != TypeKind.Interface)
            {
                return false;
            }

            if (original.ContainingType.TypeKind == TypeKind.Interface)
            {
                // Another interface member: it is the door when some type runs it by the very method it runs the door by.
                return implementations.Any(implementation => ImplementationIn(implementation.Type, original) is { } method
                    && Same(method.OriginalDefinition, implementation.Method.OriginalDefinition));
            }

            return original.ContainingType.AllInterfaces.Any(contract =>
                Same(contract.OriginalDefinition, door.ContainingType)
                && contract.GetMembers(door.Name).OfType<IMethodSymbol>().Any(member =>
                    Same(member.OriginalDefinition, door)
                    && Same(original.ContainingType.FindImplementationForInterfaceMember(member)?.OriginalDefinition, original)));
        }
    }
}
