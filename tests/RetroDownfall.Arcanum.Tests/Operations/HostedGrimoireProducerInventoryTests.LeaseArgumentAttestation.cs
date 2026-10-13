using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // The production shape of Presented-Lease Attestation: the acquired lease travels by value into a sealed
    // facts reader, which forwards it to the ABSTRACT gate member that the DI alias binds to the sealed gate. The
    // concrete body is a lock plus `lease is ConcreteLease work && reads`. A later internal abstract facet passes
    // the same lease to a store, and a physical send follows. The checker must keep granting the acquired Work
    // across the attestation, and it must stop granting it the moment the real bound body does anything else.
    private const string LeaseAttestationGate = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate";

    private const string LeaseAttestationCallee = LeaseAttestationGate + ".AttestDrainedMaintenance";

    private const string LeaseAttestationFile = "System.IO.File.Delete";

    // Shared data layer: the sealed gate with the abstract attestation, the sealed facts reader, and the
    // internal abstract facet implemented by the store. `bodyPrefix` is spliced before the real predicate.
    private static string LeaseAttestationDataLayer(string shape, bool storeGroup = true)
    {
        string bodyPrefix = shape switch
        {
            "gate-body-effect" => "System.IO.File.Delete(\"attestation-gate-body\");",
            "disposing-body" or "default-slot-disposing-body" => "lease.Dispose();",
            _ => "",
        };

        string reachability = shape switch
        {
            "delegate-escape" => "_ = Observe(lease);",
            "object-escape" => "System.GC.KeepAlive(lease);",
            _ => "",
        };

        string storeEffect = storeGroup
            ? """
                        if (!admission.TryBeginExternalEffectGroup(out System.IDisposable group)) return;

                        using var owned = group;

                        await System.Threading.Tasks.Task.CompletedTask;

                        System.IO.File.Delete("clean-readmission-store");
            """
            : """
                        await System.Threading.Tasks.Task.CompletedTask;

                        _ = System.IO.File.Exists("clean-readmission-store");
            """;

        return $$"""

            namespace RetroDownfall.Arcanum.Infrastructure.Data
            {
                using System.Collections.Generic;

                internal sealed class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate
                {
                    private readonly object _sync = new();

                    private readonly HashSet<ConcreteWorkLease> _workLeases = new();

                    private long _generation = 2;

                    private long _drainedAbortFloor = 1;

                    public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease)
                    {
                        lock (_sync)
                        {
                            ConcreteWorkLease admitted = new(this, _generation);

                            _workLeases.Add(admitted);

                            lease = admitted;

                            return true;
                        }
                    }

                    public bool AttestDrainedMaintenance(IGrimoireWorkLease lease, long previousGeneration, long currentGeneration)
                    {
                        lock (_sync)
                        {
                            {{bodyPrefix}}

                            return lease is ConcreteWorkLease work && ReferenceEquals(work.Gate, this)
                                && _workLeases.Contains(work) && !work.IsReleased && !work.RevocationPending
                                && currentGeneration == _generation && work.Generation == currentGeneration
                                && previousGeneration >= _drainedAbortFloor && previousGeneration < currentGeneration;
                        }
                    }

                    private sealed class ConcreteWorkLease(GrimoireConnectionAdmissionGate gate, long generation) : IGrimoireWorkLease
                    {
                        internal GrimoireConnectionAdmissionGate Gate { get; } = gate;

                        internal long Generation { get; } = generation;

                        internal bool IsReleased { get; set; }

                        internal bool RevocationPending { get; set; }

                        public bool MaintenanceRevocationPending => RevocationPending;

                        public bool TryBeginExternalEffectGroup(out System.IDisposable group)
                        {
                            group = new Group();

                            return true;
                        }

                        public void Dispose() => IsReleased = true;

                        public System.Threading.Tasks.ValueTask DisposeAsync()
                        {
                            IsReleased = true;

                            return default;
                        }

                        private sealed class Group : System.IDisposable
                        {
                            public void Dispose() { }
                        }
                    }
                }

                internal static class OpaqueGateSource
                {
                    internal static IGrimoireConnectionAdmissionGate Instance { get; set; } = null!;

                    internal static IGrimoireConnectionAdmissionGate Create() => Instance;
                }
            }

            namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
            {
                internal sealed class LongRestLiveFenceReader(IGrimoireConnectionAdmissionGate admission)
                {
                    private static System.Func<IGrimoireWorkLease, bool> Observe { get; set; } = static _ => true;

                    internal bool AttestCleanAdmission(IGrimoireWorkLease lease, long previousGeneration, long currentGeneration)
                    {
                        {{reachability}}

                        return admission.AttestDrainedMaintenance(lease, previousGeneration, currentGeneration);
                    }
                }

                internal interface ILongRestDiscoveryStore
                {
                }

                internal interface ILongRestCleanReadmission
                {
                    System.Threading.Tasks.Task ReadmitCleanAsync(IGrimoireWorkLease admission, long previousAdmission, System.Threading.CancellationToken ct);
                }

                internal sealed class LongRestDiscoveryStore : ILongRestDiscoveryStore, ILongRestCleanReadmission
                {
                    public async System.Threading.Tasks.Task ReadmitCleanAsync(IGrimoireWorkLease admission, long previousAdmission, System.Threading.CancellationToken ct)
                    {
                        if (admission.MaintenanceRevocationPending) return;

                        {{storeEffect}}
                    }
                }
            }
            """;
    }

    private static string LeaseAttestationProtocol(string source, string shape)
    {
        string slot = shape == "default-slot-disposing-body"
            ? "bool AttestDrainedMaintenance(IGrimoireWorkLease lease, long previousGeneration, long currentGeneration) => false;"
            : "bool AttestDrainedMaintenance(IGrimoireWorkLease lease, long previousGeneration, long currentGeneration);";

        source = source.ReplaceSdkFixtureText(
            "public interface IGrimoireWorkLease : System.IDisposable, System.IAsyncDisposable { bool TryBeginExternalEffectGroup(out System.IDisposable group); }",
            "public interface IGrimoireWorkLease : System.IDisposable, System.IAsyncDisposable { bool MaintenanceRevocationPending { get; } bool TryBeginExternalEffectGroup(out System.IDisposable group); }",
            StringComparison.Ordinal);

        return source.ReplaceSdkFixtureText(
            "public interface IGrimoireConnectionAdmissionGate { bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease); }",
            "public interface IGrimoireConnectionAdmissionGate { bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease); " + slot + " }",
            StringComparison.Ordinal);
    }

    private static string LeaseAttestationGateRegistration(string shape) => shape == "incomplete-gate-di"
        ? "services.AddSingleton<RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate>(sp => RetroDownfall.Arcanum.Infrastructure.Data.OpaqueGateSource.Create());"
        : "services.AddSingleton<RetroDownfall.Arcanum.Infrastructure.Data.GrimoireConnectionAdmissionGate>(); services.AddSingleton<RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate>(sp => sp.GetRequiredService<RetroDownfall.Arcanum.Infrastructure.Data.GrimoireConnectionAdmissionGate>());";

    // Ordinary root: the hosted worker acquires the lease and passes the acquired local by value, through a scoped
    // admitted-sweep interface, into the owned run that attests and then sends. There is no holder anywhere.
    private static string LeaseArgumentAttestationOrdinaryFixture(string shape)
    {
        bool holder = shape == "holder-bound";

        string registrations = "services.AddHostedService<Worker>(); "
            + (holder ? "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission>(); " : "")
            + "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedSweep, RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator>(); "
            + "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestLiveFenceReader>(); "
            + "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ILongRestDiscoveryStore, RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestDiscoveryStore>(); "
            + LeaseAttestationGateRegistration(shape);

        string source = RegistrationSource(registrations);

        source = source.ReplaceSdkFixtureText("public class Worker : IHostedService", "public class Worker(IServiceScopeFactory scopeFactory) : IHostedService", StringComparison.Ordinal);

        source = source.ReplaceSdkFixtureText("new Worker();", "new Worker(provider.GetRequiredService<IServiceScopeFactory>());", StringComparison.Ordinal);

        string binding = holder
            ? "using IDisposable binding = scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission>().Bind(lease);"
            : "";

        source = source.ReplaceSdkFixtureText("public Task StartAsync(CancellationToken token) => Task.CompletedTask;", $$"""
            public async Task StartAsync(CancellationToken token)
            {
                await RunOneAsync(Guid.Empty, token).ConfigureAwait(false);
            }

            private async Task RunOneAsync(Guid operation, CancellationToken ct)
            {
                RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!;

                if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.LongRestSweep,
                    out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease admitted)) return;

                await using RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease = admitted;

                await using var scope = scopeFactory.CreateAsyncScope();

                {{binding}}

                RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedSweep sweep = scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedSweep>();

                await sweep.ResumeAdmittedAsync(operation, lease, ct).ConfigureAwait(false);
            }
            """, StringComparison.Ordinal);

        source += """

            namespace RetroDownfall.Arcanum.Infrastructure.Data
            {
                public enum GrimoireWorkKind { WorkspaceIndexing = 4, LongRestSweep = 17 }
                public interface IGrimoireWorkLease : System.IDisposable, System.IAsyncDisposable { bool TryBeginExternalEffectGroup(out System.IDisposable group); }
                public interface IGrimoireConnectionAdmissionGate { bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease); }
            }
            """;

        source = LeaseAttestationProtocol(source, shape);

        source += """

            namespace RetroDownfall.Arcanum.Infrastructure.Operations.LongRest
            {
                using RetroDownfall.Arcanum.Infrastructure.Data;

                using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

                internal interface ILongRestAdmittedSweep
                {
                    Task ResumeAdmittedAsync(Guid operation, IGrimoireWorkLease admission, CancellationToken ct);
                }

                internal sealed class LongRestSweepCoordinator(
                    LongRestLiveFenceReader facts,
                    ILongRestDiscoveryStore discovery) : ILongRestAdmittedSweep
                {
                    public async Task ResumeAdmittedAsync(Guid operation, IGrimoireWorkLease admission, CancellationToken ct)
                    {
                        await RunOwnedAsync(operation, admission, ct).ConfigureAwait(false);
                    }

                    private async Task RunOwnedAsync(Guid operation, IGrimoireWorkLease admission, CancellationToken ct)
                    {
                        await SendFirstAsync(admission, ct).ConfigureAwait(false);

                        if (facts.AttestCleanAdmission(admission, 1L, 2L) && discovery is ILongRestCleanReadmission clean)
                        {
                            await clean.ReadmitCleanAsync(admission, 1L, ct).ConfigureAwait(false);
                        }

                        await SendSecondAsync(admission, ct).ConfigureAwait(false);
                    }

                    private async Task SendFirstAsync(IGrimoireWorkLease lease, CancellationToken ct)
                    {
                        if (!lease.TryBeginExternalEffectGroup(out IDisposable group)) return;

                        using var owned = group;

                        await Task.CompletedTask;

                        System.IO.File.Delete("lease-argument-first");
                    }

                    private async Task SendSecondAsync(IGrimoireWorkLease lease, CancellationToken ct)
                    {
                        if (!lease.TryBeginExternalEffectGroup(out IDisposable group)) return;

                        using var owned = group;

                        await Task.CompletedTask;

                        System.IO.File.Delete("lease-argument-second");
                    }
                }
            }
            """;

        if (holder)
        {
            source += """

                namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
                {
                    using RetroDownfall.Arcanum.Infrastructure.Data;

                    internal sealed class LongRestWorkAdmission
                    {
                        private IGrimoireWorkLease? current;

                        internal IDisposable Bind(IGrimoireWorkLease lease)
                        {
                            current = lease;

                            return new Binding(this, lease);
                        }

                        private sealed class Binding(LongRestWorkAdmission owner, IGrimoireWorkLease lease) : IDisposable
                        {
                            public void Dispose()
                            {
                                if (ReferenceEquals(owner.current, lease)) owner.current = null;
                            }
                        }
                    }
                }
                """;
        }

        return source + LeaseAttestationDataLayer(shape);
    }

    // Recovery root: the reviewed per-unit scoped recovery fixture, whose handler reads the bound holder lease. The
    // handler passes that read by value into the same chain between its two sends.
    private static string LeaseArgumentAttestationRecoveryFixture(string shape, bool storeGroup)
    {
        string source = PerUnitScopedRecoveryMatrixFixture("scoped-positive");

        void ReplaceOne(string original, string replacement)
        {
            Assert.Equal(1, source.Split(original, StringSplitOptions.None).Length - 1);

            source = source.Replace(original, replacement, StringComparison.Ordinal);
        }

        source = LeaseAttestationProtocol(source, shape);

        const string holderRegistration = "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission>();";

        ReplaceOne(holderRegistration, holderRegistration
            + " services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestLiveFenceReader>();"
            + " services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ILongRestDiscoveryStore, RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestDiscoveryStore>(); "
            + LeaseAttestationGateRegistration(shape));

        const string handlerStart = "internal sealed class PerUnitRecoveryHandler(";

        int handler = source.IndexOf(handlerStart, StringComparison.Ordinal);

        Assert.True(handler > 0);

        // The handler is the last type of the final namespace of the reviewed per-unit fixture.
        source = source[..handler] + """
            internal sealed class PerUnitRecoveryHandler(
                LongRestWorkAdmission workAdmission,
                LongRestLiveFenceReader facts,
                ILongRestDiscoveryStore discovery) : ILongRunningOperationRecoveryHandler
            {
                public string Kind => LongRunningOperationKinds.PerUnit;

                public int SupportedCheckpointVersion => 0;

                public async Task RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
                {
                    await SendFirstAsync();

                    await AttestAsync(workAdmission.Lease, cancellationToken);

                    await SendSecondAsync();
                }

                private async Task AttestAsync(IGrimoireWorkLease admission, CancellationToken ct)
                {
                    if (facts.AttestCleanAdmission(admission, 1L, 2L) && discovery is ILongRestCleanReadmission clean)
                    {
                        await clean.ReadmitCleanAsync(admission, 1L, ct);
                    }
                }

                private async Task SendFirstAsync()
                {
                    IGrimoireWorkLease lease = workAdmission.Lease;

                    if (!lease.TryBeginExternalEffectGroup(out IDisposable group)) return;

                    using var owned = group;

                    await Task.CompletedTask;

                    System.IO.File.Delete("per-unit-first");
                }

                private async Task SendSecondAsync()
                {
                    IGrimoireWorkLease lease = workAdmission.Lease;

                    if (!lease.TryBeginExternalEffectGroup(out IDisposable group)) return;

                    using var owned = group;

                    await Task.CompletedTask;

                    System.IO.File.Delete("per-unit-second");
                }
            }
        }
        """ + LeaseAttestationDataLayer(shape, storeGroup);

        return source;
    }

    private sealed record LeaseAttestationRun(string Root, string Shape, CSharpCompilation Compilation, SyntaxTree Tree, HostedProducerDiscovery<HostedProducerSite> Result)
    {
        internal ClassDeclarationSyntax Type(string name) => Assert.Single(Tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText == name);

        internal MethodDeclarationSyntax Method(string owner, string name) => Assert.Single(Type(owner).Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == name);

        internal InvocationExpressionSyntax Call(MethodDeclarationSyntax member, string text) => Assert.Single(member.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => call.ToString().StartsWith(text, StringComparison.Ordinal));

        internal string Coordinate(SyntaxNode node) => Tree.FilePath + ":" + node.SpanStart;

        // A reached site is identified by the coordinate of its authored invocation.
        internal HostedProducerSite[] Sites(SyntaxNode invocation) => Result.Items.Where(site => site.RootType == "Worker"
            && site.OperationId.EndsWith("/site@" + Coordinate(invocation), StringComparison.Ordinal)).ToArray();

        internal HostedProducerInventoryDiagnostic[] Diagnostics(string code, string suffix) => Result.Diagnostics.Where(diagnostic => diagnostic.Code == code
            && diagnostic.Identity.EndsWith(suffix, StringComparison.Ordinal)).ToArray();
    }

    private LeaseAttestationRun RunLeaseAttestation(string root, string source, string shape)
    {
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        HostedProducerDiscovery<HostedProducerSite> result = root == "recovery"
            ? DiscoverRecoveryMatrixFixture([compilation])
            : HostedGrimoireProducerInventory.DiscoverProducerSites(
                [compilation],
                HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]),
                [new("Worker", [OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep }])],
                []);

        LeaseAttestationRun run = new(root, shape, compilation, tree, result);

        // Premise guards: the fixture really has the shape the verdicts depend on.
        Assert.Equal(shape != "incomplete-gate-di", source.Contains("sp.GetRequiredService<RetroDownfall.Arcanum.Infrastructure.Data.GrimoireConnectionAdmissionGate>()", StringComparison.Ordinal));

        Assert.Equal(shape == "incomplete-gate-di", source.Contains("OpaqueGateSource.Create()", StringComparison.Ordinal));

        INamedTypeSymbol gate = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetSemanticModel(tree).GetDeclaredSymbol(run.Type("GrimoireConnectionAdmissionGate")));

        Assert.True(gate.IsSealed);

        INamedTypeSymbol contract = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(LeaseAttestationGate));

        IMethodSymbol slot = Assert.Single(contract.GetMembers("AttestDrainedMaintenance").OfType<IMethodSymbol>());

        Assert.Equal(["RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", "long", "long"], slot.Parameters.Select(static parameter => parameter.Type.ToDisplayString()));

        // The member is abstract on the contract, except in the one row that documents why it has to be.
        Assert.Equal(shape == "default-slot-disposing-body", !slot.IsAbstract);

        MethodDeclarationSyntax body = run.Method("GrimoireConnectionAdmissionGate", "AttestDrainedMaintenance");

        Assert.Single(body.DescendantNodes().OfType<LockStatementSyntax>());

        Assert.Single(body.DescendantNodes().OfType<IsPatternExpressionSyntax>());

        Assert.NotEmpty(run.Method("LongRestLiveFenceReader", "AttestCleanAdmission").DescendantNodes().OfType<InvocationExpressionSyntax>());

        foreach (HostedProducerSite site in result.Items.Where(static site => site.RootType == "Worker" && site.Callee is LeaseAttestationFile or "System.IO.File.Exists"))
        {
            output.WriteLine($"LEASE_ATTESTATION_SITE root={root} shape={shape} {site.EnclosingType}.{site.Member} {site.Callee} work={(site.WorkFrontierCapsuleId is null ? "null" : "set")} effect={(site.EffectFrontierCapsuleId is null ? "null" : "set")}");
        }

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"LEASE_ATTESTATION_DIAGNOSTIC root={root} shape={shape} {diagnostic.Code} {diagnostic.Detail}");
        }

        return run;
    }

    [Theory]
    [InlineData("positive")]
    [InlineData("gate-body-effect")]
    [InlineData("disposing-body")]
    [InlineData("incomplete-gate-di")]
    [InlineData("delegate-escape")]
    [InlineData("object-escape")]
    [InlineData("default-slot-disposing-body")]
    [InlineData("holder-bound")]
    public void LeaseArgumentAttestationOrdinaryRootKeepsOrEndsTheAcquiredWorkExactlyWhereTheBoundBodyDoes(string shape)
    {
        LeaseAttestationRun run = RunLeaseAttestation("ordinary", LeaseArgumentAttestationOrdinaryFixture(shape), shape);

        const string coordinator = "LongRestSweepCoordinator";

        const string store = "LongRestDiscoveryStore";

        HostedProducerSite acquired = Assert.Single(run.Result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync" && site.Callee == LeaseAttestationGate + ".TryAcquireWorkLease");

        Assert.Equal(GrimoireWorkKind.LongRestSweep, acquired.AdmittedWorkKind);

        InvocationExpressionSyntax firstDelete = run.Call(run.Method(coordinator, "SendFirstAsync"), "System.IO.File.Delete");

        InvocationExpressionSyntax secondDelete = run.Call(run.Method(coordinator, "SendSecondAsync"), "System.IO.File.Delete");

        InvocationExpressionSyntax storeDelete = run.Call(run.Method(store, "ReadmitCleanAsync"), "System.IO.File.Delete");

        HostedProducerSite Single(InvocationExpressionSyntax delete) => Assert.Single(run.Sites(delete));

        HostedProducerSite Group(string owner, string member) => Assert.Single(run.Result.Items, site => site.RootType == "Worker"
            && site.EnclosingType.EndsWith(owner, StringComparison.Ordinal) && site.Member == member
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        HostedProducerSite first = Single(firstDelete);

        if (shape == "holder-bound")
        {
            // Binding the acquired lease into a holder stores it on the heap, which ends its lifetime at the Bind:
            // not even the send before the attestation keeps the Work. This is the loss the by-value spine removes
            // by deleting the holder; until then the lease cannot be both bound and carried by value.
            foreach (InvocationExpressionSyntax delete in new[] { firstDelete, secondDelete, storeDelete })
            {
                HostedProducerSite lost = Single(delete);

                Assert.Null(lost.WorkFrontierCapsuleId);

                Assert.NotNull(lost.EffectFrontierCapsuleId);

                HostedProducerInventoryDiagnostic missing = Assert.Single(run.Result.Diagnostics, diagnostic =>
                    diagnostic.Identity.EndsWith("/site@" + run.Coordinate(delete), StringComparison.Ordinal));

                Assert.Equal("HOSTED_SITE_WORK_FRONTIER_MISSING", missing.Code);

                Assert.Equal(LeaseAttestationFile, missing.Detail);
            }

            Assert.Equal(3, run.Result.Diagnostics.Count);

            return;
        }

        // The send BEFORE the attestation is untouched by what the bound body does later.
        Assert.Equal(acquired.CapsuleId, first.WorkFrontierCapsuleId);

        Assert.Equal(Group(coordinator, "SendFirstAsync").CapsuleId, first.EffectFrontierCapsuleId);

        HostedProducerSite second = Single(secondDelete);

        HostedProducerSite readmit = Single(storeDelete);

        InvocationExpressionSyntax forwarded = run.Call(run.Method("LongRestLiveFenceReader", "AttestCleanAdmission"), "admission.AttestDrainedMaintenance");

        string unresolved = run.Coordinate(forwarded);

        void AssertAttestationEndsWork(params string[] diagnosticCodes)
        {
            // Both the later send and the store lose the acquired Work, with exactly the missing-frontier refusals.
            Assert.Null(second.WorkFrontierCapsuleId);

            Assert.Null(readmit.WorkFrontierCapsuleId);

            foreach (HostedProducerSite lost in new[] { second, readmit })
            {
                Assert.Contains(run.Result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    && diagnostic.Detail == LeaseAttestationFile
                    && diagnostic.Identity.EndsWith("/site@" + (lost == second ? run.Coordinate(secondDelete) : run.Coordinate(storeDelete)), StringComparison.Ordinal));
            }

            foreach (string code in diagnosticCodes) Assert.Contains(run.Result.Diagnostics, diagnostic => diagnostic.Code == code);
        }

        if (shape is "positive" or "gate-body-effect" or "default-slot-disposing-body")
        {
            // The attestation, the abstract facet and the store keep the same acquired Work, and each physical
            // send keeps its own group's Effect.
            Assert.Equal(acquired.CapsuleId, second.WorkFrontierCapsuleId);

            Assert.Equal(Group(coordinator, "SendSecondAsync").CapsuleId, second.EffectFrontierCapsuleId);

            Assert.Equal(acquired.CapsuleId, readmit.WorkFrontierCapsuleId);

            Assert.Equal(Group(store, "ReadmitCleanAsync").CapsuleId, readmit.EffectFrontierCapsuleId);

            Assert.Equal(3, new[] { first.EffectFrontierCapsuleId, second.EffectFrontierCapsuleId, readmit.EffectFrontierCapsuleId }.Distinct(StringComparer.Ordinal).Count());
        }

        switch (shape)
        {
            case "positive":
                Assert.Empty(run.Result.Diagnostics);

                break;

            case "gate-body-effect":
            {
                // The REAL DI-bound body is traversed, not a declaration: its physical effect is found under the
                // acquired Work, and being outside any group it is refused at its own coordinate.
                InvocationExpressionSyntax inBody = run.Call(run.Method("GrimoireConnectionAdmissionGate", "AttestDrainedMaintenance"), "System.IO.File.Delete");

                HostedProducerSite site = Single(inBody);

                Assert.Equal(acquired.CapsuleId, site.WorkFrontierCapsuleId);

                Assert.Null(site.EffectFrontierCapsuleId);

                HostedProducerInventoryDiagnostic missing = Assert.Single(run.Result.Diagnostics);

                Assert.Equal("HOSTED_SITE_EFFECT_FRONTIER_MISSING", missing.Code);

                Assert.Equal(LeaseAttestationFile, missing.Detail);

                Assert.EndsWith("/site@" + run.Coordinate(inBody), missing.Identity, StringComparison.Ordinal);

                break;
            }

            case "disposing-body":
            {
                // The bound body disposes the presented lease: its lifetime recursion ends the caller's Work.
                AssertAttestationEndsWork();

                Assert.Equal(2, run.Result.Diagnostics.Count);

                Assert.DoesNotContain(run.Result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CALL_TARGET_UNRESOLVED" or "HOSTED_ADMISSION_HANDLE_ESCAPE");

                break;
            }

            case "incomplete-gate-di":
            {
                // An incomplete registration leaves the interface call without a target: nothing is assumed.
                AssertAttestationEndsWork("HOSTED_CALL_TARGET_UNRESOLVED", "HOSTED_ADMISSION_HANDLE_ESCAPE");

                HostedProducerInventoryDiagnostic target = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED");

                Assert.Equal(unresolved, target.Identity);

                Assert.Equal(LeaseAttestationCallee, target.Detail);

                Assert.Contains(run.Result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE"
                    && diagnostic.Detail.StartsWith(LeaseAttestationCallee + ";", StringComparison.Ordinal));

                break;
            }

            case "delegate-escape":
            {
                InvocationExpressionSyntax observe = run.Call(run.Method("LongRestLiveFenceReader", "AttestCleanAdmission"), "Observe(");

                AssertAttestationEndsWork();

                HostedProducerInventoryDiagnostic callback = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

                Assert.EndsWith("/callback@" + run.Coordinate(observe), callback.Identity, StringComparison.Ordinal);

                Assert.StartsWith("System.Func`2.Invoke;", callback.Detail, StringComparison.Ordinal);

                break;
            }

            case "object-escape":
            {
                InvocationExpressionSyntax keepAlive = run.Call(run.Method("LongRestLiveFenceReader", "AttestCleanAdmission"), "System.GC.KeepAlive");

                AssertAttestationEndsWork("HOSTED_ADMISSION_HANDLE_ESCAPE");

                Assert.Contains(run.Result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE"
                    && diagnostic.Detail.StartsWith("System.GC.KeepAlive;", StringComparison.Ordinal));

                Assert.Contains(run.Result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                    && diagnostic.Identity == run.Coordinate(keepAlive));

                break;
            }

            default:
            {
                Assert.Equal("default-slot-disposing-body", shape);

                // Distinguishing row. The same disposing body behind a DEFAULT interface slot does not end the Work
                // above: lifetime analysis walks the default body, not the override the DI alias binds. That is a
                // false grant, held back only by the unresolved disposal in the override. It is why the production
                // member is abstract, and why disposing-body ends the Work while this row does not.
                InvocationExpressionSyntax dispose = run.Call(run.Method("GrimoireConnectionAdmissionGate", "AttestDrainedMaintenance"), "lease.Dispose");

                HostedProducerInventoryDiagnostic refusal = Assert.Single(run.Result.Diagnostics);

                Assert.Equal("HOSTED_DISPOSAL_TARGET_UNRESOLVED", refusal.Code);

                Assert.EndsWith("/dispose@" + run.Coordinate(dispose), refusal.Identity, StringComparison.Ordinal);

                break;
            }
        }
    }

    [Theory]
    [InlineData("positive")]
    [InlineData("gate-body-effect")]
    [InlineData("disposing-body")]
    [InlineData("incomplete-gate-di")]
    [InlineData("delegate-escape")]
    [InlineData("object-escape")]
    [InlineData("default-slot-disposing-body")]
    [InlineData("store-group-on-formal")]
    public void LeaseArgumentAttestationRecoveryRootGrantsOrRefusesAtTheExactCoordinate(string shape)
    {
        // The recovery root grants its handler sends from the reviewed per-unit proof, independently of what the
        // chain does with the presented lease. What the chain changes is therefore visible as refusals.
        bool storeGroup = shape == "store-group-on-formal";

        LeaseAttestationRun run = RunLeaseAttestation("recovery", LeaseArgumentAttestationRecoveryFixture(storeGroup ? "positive" : shape, storeGroup), shape);

        const string handler = "PerUnitRecoveryHandler";

        HostedProducerSite acquired = Assert.Single(run.Result.Items, static site => site.RootType == "Worker"
            && site.Member == "StartAsync" && site.Callee == LeaseAttestationGate + ".TryAcquireWorkLease");

        InvocationExpressionSyntax firstDelete = run.Call(run.Method(handler, "SendFirstAsync"), "System.IO.File.Delete");

        InvocationExpressionSyntax secondDelete = run.Call(run.Method(handler, "SendSecondAsync"), "System.IO.File.Delete");

        HostedProducerSite first = Assert.Single(run.Sites(firstDelete));

        HostedProducerSite second = Assert.Single(run.Sites(secondDelete));

        // Before and after the attestation, each send keeps the acquired Work and its own group's Effect.
        Assert.Equal(acquired.CapsuleId, first.WorkFrontierCapsuleId);

        Assert.Equal(acquired.CapsuleId, second.WorkFrontierCapsuleId);

        Assert.NotNull(first.EffectFrontierCapsuleId);

        Assert.NotNull(second.EffectFrontierCapsuleId);

        Assert.NotEqual(first.EffectFrontierCapsuleId, second.EffectFrontierCapsuleId);

        MethodDeclarationSyntax readmitMember = run.Method("LongRestDiscoveryStore", "ReadmitCleanAsync");

        InvocationExpressionSyntax forwarded = run.Call(run.Method("LongRestLiveFenceReader", "AttestCleanAdmission"), "admission.AttestDrainedMaintenance");

        switch (shape)
        {
            case "positive":
            {
                // The lease reaches the abstract facet and its store by value, under the acquired Work.
                InvocationExpressionSyntax read = run.Call(readmitMember, "System.IO.File.Exists");

                HostedProducerSite site = Assert.Single(run.Sites(read));

                Assert.Equal(acquired.CapsuleId, site.WorkFrontierCapsuleId);

                Assert.Empty(run.Result.Diagnostics);

                break;
            }

            case "gate-body-effect":
            {
                InvocationExpressionSyntax inBody = run.Call(run.Method("GrimoireConnectionAdmissionGate", "AttestDrainedMaintenance"), "System.IO.File.Delete");

                HostedProducerSite site = Assert.Single(run.Sites(inBody));

                Assert.Equal(acquired.CapsuleId, site.WorkFrontierCapsuleId);

                Assert.Null(site.EffectFrontierCapsuleId);

                foreach (string code in new[] { "HOSTED_SITE_EFFECT_FRONTIER_MISSING", "HOSTED_RECOVERY_PER_UNIT_HANDOFF_UNPROVEN" })
                {
                    Assert.NotEmpty(run.Diagnostics(code, "/site@" + run.Coordinate(inBody)));
                }

                Assert.DoesNotContain(run.Result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CALL_TARGET_UNRESOLVED" or "HOSTED_ADMISSION_HANDLE_ESCAPE");

                break;
            }

            case "disposing-body" or "default-slot-disposing-body":
            {
                InvocationExpressionSyntax dispose = run.Call(run.Method("GrimoireConnectionAdmissionGate", "AttestDrainedMaintenance"), "lease.Dispose");

                HostedProducerInventoryDiagnostic refusal = Assert.Single(run.Result.Diagnostics);

                Assert.Equal("HOSTED_DISPOSAL_TARGET_UNRESOLVED", refusal.Code);

                Assert.EndsWith("/dispose@" + run.Coordinate(dispose), refusal.Identity, StringComparison.Ordinal);

                break;
            }

            case "incomplete-gate-di":
            {
                HostedProducerInventoryDiagnostic target = Assert.Single(run.Result.Diagnostics);

                Assert.Equal("HOSTED_CALL_TARGET_UNRESOLVED", target.Code);

                Assert.Equal(run.Coordinate(forwarded), target.Identity);

                Assert.Equal(LeaseAttestationCallee, target.Detail);

                break;
            }

            case "delegate-escape":
            {
                InvocationExpressionSyntax observe = run.Call(run.Method("LongRestLiveFenceReader", "AttestCleanAdmission"), "Observe(");

                HostedProducerInventoryDiagnostic callback = Assert.Single(run.Result.Diagnostics);

                Assert.Equal("HOSTED_CALLBACK_OWNERSHIP_UNPROVEN", callback.Code);

                Assert.EndsWith("/callback@" + run.Coordinate(observe), callback.Identity, StringComparison.Ordinal);

                Assert.StartsWith("System.Func`2.Invoke;", callback.Detail, StringComparison.Ordinal);

                break;
            }

            case "object-escape":
            {
                InvocationExpressionSyntax keepAlive = run.Call(run.Method("LongRestLiveFenceReader", "AttestCleanAdmission"), "System.GC.KeepAlive");

                HostedProducerInventoryDiagnostic unclassified = Assert.Single(run.Result.Diagnostics);

                Assert.Equal("HOSTED_SITE_UNCLASSIFIED", unclassified.Code);

                Assert.Equal(run.Coordinate(keepAlive), unclassified.Identity);

                break;
            }

            default:
            {
                Assert.Equal("store-group-on-formal", shape);

                // Documented boundary of the current recovery frontier: a group opened on a lease FORMAL is not the
                // exact retained recovery lease, so the group and its send are refused at their own coordinates.
                // The receiver-proof work for recovery frontiers closes this; until then it must stay refused.
                InvocationExpressionSyntax group = run.Call(readmitMember, "admission.TryBeginExternalEffectGroup");

                InvocationExpressionSyntax delete = run.Call(readmitMember, "System.IO.File.Delete");

                Assert.NotEmpty(run.Diagnostics("HOSTED_RECOVERY_PER_UNIT_HANDOFF_UNPROVEN", "/site@" + run.Coordinate(group)));

                Assert.NotEmpty(run.Diagnostics("HOSTED_RECOVERY_PER_UNIT_HANDOFF_UNPROVEN", "/site@" + run.Coordinate(delete)));

                Assert.NotEmpty(run.Diagnostics("HOSTED_SITE_EFFECT_FRONTIER_MISSING", "/site@" + run.Coordinate(delete)));

                Assert.All(run.Sites(delete), site => Assert.Null(site.EffectFrontierCapsuleId));

                break;
            }
        }
    }
}
