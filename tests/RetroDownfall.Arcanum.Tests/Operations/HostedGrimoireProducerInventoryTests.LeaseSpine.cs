using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // Lease Spine: one lease is minted by the hosted worker and carried by value through every use. The fixture is
    // the production shape in two compilations: the Infrastructure side owns the worker, the admitted sweep port and
    // the coordinator; the Api side owns the maintenance that opens the external-effect group on the lease it was
    // handed. No object stores the lease, so the checker must grant the worker's Work and the group's Effect exactly
    // at the physical send, and must stop granting the Work the moment the lease is disposed, stored, captured,
    // passed through a delegate or handed to a callee it cannot prove.
    private const string LeaseSpineWorkerPath = "src/RetroDownfall.Arcanum.Infrastructure/LeaseSpine.cs";

    private const string LeaseSpineGroup = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup";

    private const string LeaseSpineAcquisition = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease";

    private const string LeaseSpineSend = "System.IO.File.Delete";

    // Infrastructure compilation: the lease protocol, the gate, the hosted worker, the admitted sweep port and its
    // coordinator. `workerPrefix` is spliced between the acquisition and the port call, `ownedPrefix` before the
    // coordinator hands the lease to the admitted maintenance, and `members` into the coordinator body.
    private static string LeaseSpineInfrastructureSource(string shape)
    {
        string acquired = shape == "disposed-before-call"
            ? """
                IGrimoireWorkLease lease = admitted!;

                await lease.DisposeAsync().ConfigureAwait(false);
            """
            : "await using IGrimoireWorkLease lease = admitted!;";

        string owned = shape switch
        {
            "stored-in-field" => "_held = admission;",
            "captured-in-lambda" => "_ = Task.Run(() => maintenance.ProcessAdmittedAsync(operation, \"owner\", null, admission, ct));",
            "through-delegate" => "await Observe(admission).ConfigureAwait(false);",
            "object-unknown-callee" => "System.GC.KeepAlive(admission);",
            _ => "",
        };

        string members = shape switch
        {
            "stored-in-field" => "private IGrimoireWorkLease? _held;",
            "through-delegate" => "private static Func<IGrimoireWorkLease, Task> Observe { get; set; } = static _ => Task.CompletedTask;",
            _ => "",
        };

        string handoff = shape == "captured-in-lambda"
            ? ""
            : "await maintenance.ProcessAdmittedAsync(operation, \"owner\", null, admission, ct).ConfigureAwait(false);";

        return $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using RetroDownfall.Arcanum.Infrastructure.Data;
            using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LeaseSpine.Api")]

            public class Worker(IServiceScopeFactory scopes, IGrimoireConnectionAdmissionGate admission) : IHostedService
            {
                public async Task StartAsync(CancellationToken token)
                {
                    await RunOneAsync(Guid.Empty, token).ConfigureAwait(false);
                }

                public Task StopAsync(CancellationToken token) => Task.CompletedTask;

                private async Task RunOneAsync(Guid operation, CancellationToken ct)
                {
                    if (!admission.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? admitted)) return;

                    {{acquired}}

                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();

                    ILongRestAdmittedSweep sweep = scope.ServiceProvider.GetRequiredService<ILongRestAdmittedSweep>();

                    await sweep.ResumeAdmittedAsync(operation, lease, ct).ConfigureAwait(false);
                }
            }

            public static class Composition
            {
                public static void Configure(IServiceCollection services)
                {
                    services.AddHostedService<Worker>();

                    services.AddSingleton<ProbeGate>();

                    services.AddSingleton<IGrimoireConnectionAdmissionGate>(sp => sp.GetRequiredService<ProbeGate>());

                    services.AddScoped<LongRestSweepCoordinator>();

                    services.AddScoped<ILongRestAdmittedSweep>(static sp => sp.GetRequiredService<LongRestSweepCoordinator>());
                }
            }

            namespace RetroDownfall.Arcanum.Infrastructure.Data
            {
                public enum GrimoireWorkKind { WorkspaceIndexing = 4, LongRunningOperationRecovery = 14, LongRestSweep = 17 }

                public interface IGrimoireExternalEffectGroup : IAsyncDisposable
                {
                }

                public interface IGrimoireWorkLease : IAsyncDisposable
                {
                    GrimoireWorkKind Kind { get; }

                    CancellationToken MaintenanceRevocation { get; }

                    bool MaintenanceRevocationPending { get; }

                    bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup);
                }

                public interface IGrimoireConnectionAdmissionGate
                {
                    bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease? lease);
                }

                public sealed class ProbeGate : IGrimoireConnectionAdmissionGate
                {
                    public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease? lease)
                    {
                        lease = new ProbeLease(kind);

                        return true;
                    }

                    private sealed class ProbeLease(GrimoireWorkKind kind) : IGrimoireWorkLease
                    {
                        private readonly CancellationTokenSource _revocation = new();

                        public GrimoireWorkKind Kind { get; } = kind;

                        public CancellationToken MaintenanceRevocation => _revocation.Token;

                        public bool MaintenanceRevocationPending => _revocation.IsCancellationRequested;

                        public bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup)
                        {
                            effectGroup = new ProbeGroup();

                            return true;
                        }

                        public ValueTask DisposeAsync() => default;
                    }

                    private sealed class ProbeGroup : IGrimoireExternalEffectGroup
                    {
                        public ValueTask DisposeAsync() => default;
                    }
                }
            }

            namespace RetroDownfall.Arcanum.Infrastructure.Operations.LongRest
            {
                public interface ILongRestAdmittedSweep
                {
                    Task ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct);
                }

                internal interface ILongRestAdmittedMaintenance
                {
                    Task ProcessAdmittedAsync(Guid operationId, string workerOwner, object? capability, IGrimoireWorkLease admission, CancellationToken ct);
                }

                internal sealed class LongRestSweepCoordinator(ILongRestAdmittedMaintenance maintenance) : ILongRestAdmittedSweep
                {
                    {{members}}

                    public async Task ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct)
                    {
                        if (admission is null) throw new ArgumentNullException(nameof(admission));

                        if (admission.Kind != GrimoireWorkKind.LongRestSweep) throw new ArgumentException("Long Rest work requires a Long Rest sweep admission.", nameof(admission));

                        await ResumeCoreAsync(operationId, null, admission, ct).ConfigureAwait(false);
                    }

                    private async Task ResumeCoreAsync(Guid operationId, object? capability, IGrimoireWorkLease admission, CancellationToken ct)
                    {
                        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, admission.MaintenanceRevocation);

                        await RunOwnedAsync(operationId, capability, admission, lifetime.Token).ConfigureAwait(false);
                    }

                    private async Task RunOwnedAsync(Guid operation, object? capability, IGrimoireWorkLease admission, CancellationToken ct)
                    {
                        {{owned}}

                        {{handoff}}
                    }
                }
            }
            """;
    }

    // Api compilation: the serving composition, the admitted maintenance and the paid door. The group is begun on the
    // lease parameter, and the local function that runs before the send does not mention the lease.
    private static string LeaseSpineApiSource() => """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using RetroDownfall.Arcanum.Infrastructure.Data;
        using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

        namespace RetroDownfall.Arcanum.Api
        {
            public static class ApiBootstrapper
            {
                public static IServiceCollection AddArcanumApiServices(this IServiceCollection services)
                {
                    global::Composition.Configure(services);

                    services.AddScoped<RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance>();

                    services.AddScoped<ILongRestAdmittedMaintenance>(static sp => sp.GetRequiredService<RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance>());

                    return services;
                }
            }
        }

        namespace RetroDownfall.Arcanum.Api.Intelligence
        {
            internal sealed class LongRestMaintenance : ILongRestAdmittedMaintenance
            {
                public async Task ProcessAdmittedAsync(Guid operationId, string workerOwner, object? capability, IGrimoireWorkLease admission, CancellationToken ct)
                {
                    if (admission is null) throw new ArgumentNullException(nameof(admission));

                    if (admission.Kind != GrimoireWorkKind.LongRestSweep) throw new ArgumentException("Long Rest work requires a Long Rest sweep admission.", nameof(admission));

                    ct.ThrowIfCancellationRequested();

                    using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, admission.MaintenanceRevocation);

                    await FoldAsync(operationId, capability, workerOwner, admission, lifetime.Token).ConfigureAwait(false);
                }

                private async Task FoldAsync(Guid operationId, object? capability, string workerOwner, IGrimoireWorkLease admission, CancellationToken ct)
                {
                    await ExecutePaidAsync(operationId, capability, workerOwner, admission,
                        async (token) =>
                        {
                            await Task.CompletedTask;

                            return operationId;
                        }, ct).ConfigureAwait(false);
                }

                private async Task<T> ExecutePaidAsync<T>(Guid operationId, object? capability, string workerOwner, IGrimoireWorkLease admission,
                    Func<CancellationToken, Task<T>> accept, CancellationToken ct)
                {
                    if (!admission.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group) || group is null)
                    {
                        ct.ThrowIfCancellationRequested();

                        throw new InvalidOperationException("The external-effect group was refused.");
                    }

                    await using IGrimoireExternalEffectGroup ownedEffect = group;

                    using CancellationTokenSource dispatch = CancellationTokenSource.CreateLinkedTokenSource(ct, admission.MaintenanceRevocation);

                    int sent = 0;

                    async Task BeforeSend(CancellationToken token)
                    {
                        if (Interlocked.Exchange(ref sent, 1) != 0) throw new InvalidOperationException("Already sent.");

                        await Task.CompletedTask;
                    }

                    await BeforeSend(dispatch.Token).ConfigureAwait(false);

                    System.IO.File.Delete("lease-spine-send");

                    return await accept(dispatch.Token).ConfigureAwait(false);
                }
            }
        }
        """;

    private sealed record LeaseSpineRun(string Shape, CSharpCompilation Infrastructure, CSharpCompilation Api, HostedProducerDiscovery<HostedProducerSite> Result)
    {
        internal SyntaxTree[] Trees => [.. Infrastructure.SyntaxTrees, .. Api.SyntaxTrees];

        internal MethodDeclarationSyntax Method(string owner, string name) => Assert.Single(Trees.SelectMany(static tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Where(type => type.Identifier.ValueText == owner).SelectMany(static type => type.Members.OfType<MethodDeclarationSyntax>()),
            method => method.Identifier.ValueText == name);

        internal InvocationExpressionSyntax Call(MethodDeclarationSyntax member, string text) => Assert.Single(member.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => call.ToString().StartsWith(text, StringComparison.Ordinal));

        internal static string Coordinate(SyntaxNode node) => node.SyntaxTree.FilePath + ":" + node.SpanStart;

        // A reached site is identified by the coordinate of its authored invocation.
        internal HostedProducerSite[] Sites(SyntaxNode invocation) => Result.Items.Where(site => site.RootType == "Worker"
            && site.OperationId.EndsWith("/site@" + Coordinate(invocation), StringComparison.Ordinal)).ToArray();
    }

    private LeaseSpineRun RunLeaseSpine(string shape)
    {
        // Both compilations bind against the production reference pack, so the Infrastructure side exposes the same
        // framework surface the Api side consumes. Their assembly names are the fixtures' own: the production
        // assemblies' internals are not visible to them.
        CSharpCompilation api = CompileWithProductionReferencePack(LeaseSpineApiSource(), "RetroDownfall.Arcanum.Api")
            .WithAssemblyName("LeaseSpine.Api");

        CSharpCompilation infrastructure = CSharpCompilation.Create("LeaseSpine.Infrastructure",
            [CSharpSyntaxTree.ParseText(LeaseSpineInfrastructureSource(shape), path: LeaseSpineWorkerPath)],
            api.References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        api = api.AddReferences(infrastructure.ToMetadataReference());

        Assert.All(new[] { infrastructure, api }, static compilation => Assert.Empty(compilation.GetDiagnostics().Where(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [infrastructure, api],
            HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([infrastructure, api]),
            [new("Worker", [OrdinaryRoot() with { SourcePath = LeaseSpineWorkerPath, WorkKind = GrimoireWorkKind.LongRestSweep }])],
            []);

        LeaseSpineRun run = new(shape, infrastructure, api, result);

        foreach (HostedProducerSite site in result.Items.Where(static site => site.RootType == "Worker"))
        {
            output.WriteLine($"LEASE_SPINE_SITE shape={shape} {site.EnclosingType}.{site.Member} {site.Callee} work={(site.WorkFrontierCapsuleId is null ? "null" : "set")} effect={(site.EffectFrontierCapsuleId is null ? "null" : "set")}");
        }

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"LEASE_SPINE_DIAGNOSTIC shape={shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        return run;
    }

    [Theory]
    [InlineData("positive")]
    [InlineData("disposed-before-call")]
    [InlineData("stored-in-field")]
    [InlineData("captured-in-lambda")]
    [InlineData("through-delegate")]
    [InlineData("object-unknown-callee")]
    public void LeaseSpineGrantsTheWorkersWorkAndTheGroupsEffectAtTheSendOrEndsThemAtTheExactDeparture(string shape)
    {
        LeaseSpineRun run = RunLeaseSpine(shape);

        HostedProducerSite acquired = Assert.Single(run.Result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync" && site.Callee == LeaseSpineAcquisition);

        Assert.Equal(GrimoireWorkKind.LongRestSweep, acquired.AdmittedWorkKind);

        // The acquisition itself never depends on what happens to the lease afterwards.
        Assert.Null(acquired.EffectFrontierCapsuleId);

        HostedProducerSite scope = Assert.Single(run.Result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync" && site.Callee.EndsWith(".CreateAsyncScope", StringComparison.Ordinal));

        MethodDeclarationSyntax paid = run.Method("LongRestMaintenance", "ExecutePaidAsync");

        InvocationExpressionSyntax send = run.Call(paid, LeaseSpineSend);

        HostedProducerSite sendSite = Assert.Single(run.Sites(send));

        HostedProducerSite group = Assert.Single(run.Result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType.EndsWith("LongRestMaintenance", StringComparison.Ordinal) && site.Member == "ExecutePaidAsync"
            && site.Callee == LeaseSpineGroup);

        // The group is begun on the lease parameter in every row, so the send's Effect is always that group.
        Assert.Equal(group.CapsuleId, sendSite.EffectFrontierCapsuleId);

        string sendCoordinate = LeaseSpineRun.Coordinate(send);

        string[] diagnostics = [.. run.Result.Diagnostics.Select(static diagnostic => diagnostic.Code + "|" + diagnostic.Detail.Split(';')[0]).Order(StringComparer.Ordinal)];

        if (shape == "positive")
        {
            // The very Work minted in the worker reaches the group and the physical send, and the send's Effect is
            // the group begun on the lease parameter of the paid door: nothing is granted by name or by position.
            Assert.Equal(acquired.CapsuleId, scope.WorkFrontierCapsuleId);

            Assert.Equal(acquired.CapsuleId, group.WorkFrontierCapsuleId);

            Assert.Equal(acquired.CapsuleId, sendSite.WorkFrontierCapsuleId);

            Assert.Empty(diagnostics);

            return;
        }

        // Every departure ends the Work at or before the paid door, never later, and says so at the send itself.
        Assert.Null(group.WorkFrontierCapsuleId);

        Assert.Null(sendSite.WorkFrontierCapsuleId);

        Assert.Equal(shape == "disposed-before-call" ? null : acquired.CapsuleId, scope.WorkFrontierCapsuleId);

        HostedProducerInventoryDiagnostic missing = Assert.Single(run.Result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Identity.EndsWith("/site@" + sendCoordinate, StringComparison.Ordinal));

        Assert.Equal(LeaseSpineSend, missing.Detail);

        string[] expected = shape switch
        {
            "disposed-before-call" =>
            [
                "HOSTED_SITE_WORK_FRONTIER_MISSING|Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope",
                "HOSTED_SITE_WORK_FRONTIER_MISSING|" + LeaseSpineSend,
            ],
            "stored-in-field" => ["HOSTED_SITE_WORK_FRONTIER_MISSING|" + LeaseSpineSend],
            "captured-in-lambda" =>
            [
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN|RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedMaintenance.ProcessAdmittedAsync",
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN|System.Threading.Tasks.Task.Run",
                "HOSTED_SITE_WORK_FRONTIER_MISSING|" + LeaseSpineSend,
            ],
            "through-delegate" =>
            [
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN|System.Func`2.Invoke",
                "HOSTED_SITE_WORK_FRONTIER_MISSING|" + LeaseSpineSend,
            ],
            _ =>
            [
                "HOSTED_ADMISSION_HANDLE_ESCAPE|System.GC.KeepAlive",
                "HOSTED_SITE_UNCLASSIFIED|System.GC.KeepAlive",
                "HOSTED_SITE_WORK_FRONTIER_MISSING|" + LeaseSpineSend,
            ],
        };

        Assert.Equal(expected.Order(StringComparer.Ordinal), diagnostics);
    }
}
