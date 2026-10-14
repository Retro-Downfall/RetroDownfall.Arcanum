using System.Collections.Concurrent;

using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // Lease-Handoff Root. The Long Rest closure under the admitted maintenance is judged once, as its own root, whose admission is the
    // lease formal its callers carry in by value. The fixture is the production shape of RunOwnedAsync -> ProcessAdmittedAsync ->
    // FoldAsync -> ExecutePaidAsync in two compilations. A caller that hands the root its own lease, directly awaited, with no group
    // open and its Work live, re-roots under the root's operation id and canonical Work; every other call to the root is
    // HOSTED_LEASE_HANDOFF_UNPROVEN and is traversed in line, and every reference to the root's method must be a cut call some root proved.
    private const string LeaseHandoffWorkerPath = "src/RetroDownfall.Arcanum.Infrastructure/LeaseHandoff.cs";

    private const string LeaseHandoffRootOperation = "RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance.ProcessAdmittedAsync";

    private const string LeaseHandoffNestedOperation = "RetroDownfall.Arcanum.Api.Intelligence.LongRestFolder.FoldHandoffAsync";

    private const string LeaseHandoffCallerOperation = "Worker.StartAsync";

    private const string LeaseHandoffSecondCallerOperation = "Worker.StopAsync";

    private const string LeaseHandoffSend = "System.IO.File.Delete";

    private const string LeaseHandoffAfterRead = "System.IO.File.Exists";

    private const string LeaseHandoffAcquisition = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease";

    private const string LeaseHandoffGroup = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup";

    private const string LeaseHandoffStandardCall = "await maintenance.ProcessAdmittedAsync(operation, \"owner\", null, admission, ct).ConfigureAwait(false);";

    // The signature of the admitted maintenance, in the port and in its implementation. The root's lease formal is named `admission`.
    private static string LeaseHandoffMaintenanceParameters(string shape) => shape switch
    {
        "second-lease-param" => "Guid operationId, string workerOwner, object? capability, IGrimoireWorkLease admission, CancellationToken ct, IGrimoireWorkLease? other = null",
        "lease-omitted" => "Guid operationId, string workerOwner, object? capability, CancellationToken ct, IGrimoireWorkLease? admission = null",
        "in-formal" => "Guid operationId, string workerOwner, object? capability, in IGrimoireWorkLease admission, CancellationToken ct",
        _ => "Guid operationId, string workerOwner, object? capability, IGrimoireWorkLease admission, CancellationToken ct",
    };

    // The caller's compilation: the hosted worker, the admitted sweep port and its coordinator. `shape` varies one thing at a time.
    private static string LeaseHandoffInfrastructureSource(string shape)
    {
        string kind = shape == "different-work-kind" ? "WorkspaceIndexing" : "LongRestSweep";

        string acquired = shape == "disposed-before-call"
            ? """
                IGrimoireWorkLease lease = admitted!;

                await lease.DisposeAsync().ConfigureAwait(false);
            """
            : "await using IGrimoireWorkLease lease = admitted!;";

        string prelude = shape switch
        {
            "non-must-alias" => "IGrimoireWorkLease chosen = ct.IsCancellationRequested ? admission : admission;",
            "effect-open" => """
                if (!admission.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? held) || held is null) return;

                await using IGrimoireExternalEffectGroup heldGroup = held;
                """,
            "second-acquisition" => """
                if (!gate.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? second)) return;

                await using IGrimoireWorkLease secondLease = second!;
                """,
            "spawned-disposer" => """
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10).ConfigureAwait(false);

                    await using IGrimoireWorkLease gone = admission;
                });
                """,
            "lease-in-holder-local" => "LeaseHolder holder = new(admission);",
            "unrelated-closure" => "Func<int> unrelated = () => 1;\n\n                    _ = unrelated();",
            _ => "",
        };

        string call = shape switch
        {
            "non-must-alias" => "await maintenance.ProcessAdmittedAsync(operation, \"owner\", null, chosen, ct).ConfigureAwait(false);",
            "fire-and-forget" => "_ = maintenance.ProcessAdmittedAsync(operation, \"owner\", null, admission, ct);",
            "stored-task" => "Task running = maintenance.ProcessAdmittedAsync(operation, \"owner\", null, admission, ct);\n\n                    await running.ConfigureAwait(false);",
            "ended-in-arguments" => "await maintenance.ProcessAdmittedAsync(operation, await DisposeAndName(admission).ConfigureAwait(false), null, admission, ct).ConfigureAwait(false);",
            "via-delegate" => "Func<Guid, string, object?, IGrimoireWorkLease, CancellationToken, Task> run = maintenance.ProcessAdmittedAsync;\n\n                    await run(operation, \"owner\", null, admission, ct).ConfigureAwait(false);",
            "two-calls" => LeaseHandoffStandardCall + "\n\n                    " + LeaseHandoffStandardCall,
            "inside-lambda" => "Func<Task> inner = async () => await maintenance.ProcessAdmittedAsync(operation, \"owner\", null, admission, ct).ConfigureAwait(false);\n\n                    await inner().ConfigureAwait(false);",
            "second-lease-param" => "await maintenance.ProcessAdmittedAsync(operation, \"owner\", null, admission, ct, admission).ConfigureAwait(false);",
            "lease-in-holder-argument" => "await maintenance.ProcessAdmittedAsync(operation, \"owner\", new LeaseHolder(admission), admission, ct).ConfigureAwait(false);",
            "lease-in-holder-local" => "await maintenance.ProcessAdmittedAsync(operation, \"owner\", holder, admission, ct).ConfigureAwait(false);",
            "receiver-from-lease" => "await new LeaseCarrier(admission, maintenance).Maintenance.ProcessAdmittedAsync(operation, \"owner\", null, admission, ct).ConfigureAwait(false);",
            "configure-await-arg-ends" => "await maintenance.ProcessAdmittedAsync(operation, \"owner\", null, admission, ct).ConfigureAwait(await EndAsync(admission).ConfigureAwait(false));",
            "configure-await-variable" => "bool configured = false;\n\n                    await maintenance.ProcessAdmittedAsync(operation, \"owner\", null, admission, ct).ConfigureAwait(configured);",
            "lease-omitted" => "await maintenance.ProcessAdmittedAsync(operation, \"owner\", null, ct).ConfigureAwait(false);",
            "in-formal" => "await maintenance.ProcessAdmittedAsync(operation, \"owner\", null, in admission, ct).ConfigureAwait(false);",
            "loop-formal-disposed" or "loop-preserved" => "for (int attempt = 0; attempt < 2; attempt++)\n                    {\n                        " + LeaseHandoffStandardCall + "\n                    }",
            "goto-formal-disposed" => "again:\n\n                    " + LeaseHandoffStandardCall + "\n\n                    if (ct.IsCancellationRequested) goto again;",
            _ => LeaseHandoffStandardCall,
        };

        string after = shape is "after-call" or "formal-disposed" or "formal-stored" or "formal-captured" or "formal-spawned"
            ? "System.IO.File.Exists(\"caller-after-call\");"
            : "";

        string otherWorker = shape == "other-service"
            ? """
            public class OtherWorker(IServiceScopeFactory scopes, IGrimoireConnectionAdmissionGate admission) : IHostedService
            {
                public async Task StartAsync(CancellationToken token)
                {
                    if (!admission.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? admitted)) return;

                    await using IGrimoireWorkLease lease = admitted!;

                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();

                    ILongRestAdmittedSweep sweep = scope.ServiceProvider.GetRequiredService<ILongRestAdmittedSweep>();

                    await sweep.ResumeAdmittedAsync(Guid.Empty, lease, token).ConfigureAwait(false);
                }

                public Task StopAsync(CancellationToken token) => Task.CompletedTask;
            }
            """
            : "";

        string otherRegistration = shape == "other-service" ? "services.AddHostedService<OtherWorker>();" : "";

        string members = shape switch
        {
            "unreached-caller" => """
                private async Task SideAsync(Guid operation, IGrimoireWorkLease admission, CancellationToken ct)
                {
                    await maintenance.ProcessAdmittedAsync(operation, "side", null, admission, ct).ConfigureAwait(false);
                }
                """,
            "ended-in-arguments" => """
                private static async Task<string> DisposeAndName(IGrimoireWorkLease lease)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);

                    return "owner";
                }
                """,
            "configure-await-arg-ends" => """
                private static async Task<bool> EndAsync(IGrimoireWorkLease lease)
                {
                    await using IGrimoireWorkLease ending = lease;

                    return false;
                }
                """,
            "concrete-shell" => "public Task ResumeAsync(Guid operationId, CancellationToken ct) => Task.CompletedTask;",
            _ => "",
        };

        // Types beside the coordinator: a holder a lease can be put in, a carrier the receiver is built from, and callers that name the
        // concrete coordinator instead of the port.
        string types = shape switch
        {
            "lease-in-holder-argument" or "lease-in-holder-local" => """
                internal sealed class LeaseHolder(IGrimoireWorkLease held)
                {
                    public IGrimoireWorkLease Held { get; } = held;
                }
                """,
            "receiver-from-lease" => """
                internal sealed class LeaseCarrier(IGrimoireWorkLease held, ILongRestAdmittedMaintenance maintenance)
                {
                    public IGrimoireWorkLease Held { get; } = held;

                    public ILongRestAdmittedMaintenance Maintenance { get; } = maintenance;
                }
                """,
            "concrete-shell" => """
                internal sealed class SneakyShell(LongRestSweepCoordinator coordinator)
                {
                    public async Task RunAsync(IGrimoireWorkLease lease, CancellationToken ct)
                    {
                        await coordinator.ResumeAdmittedAsync(Guid.Empty, lease, ct).ConfigureAwait(false);

                        await coordinator.ResumeAsync(Guid.Empty, ct).ConfigureAwait(false);
                    }
                }
                """,
            _ => "",
        };

        // A second root of the same service that runs the same worker: both cut to the one handoff root.
        string stopSignature = shape == "two-callers"
            ? "async Task StopAsync(CancellationToken token) { await RunOneAsync(Guid.Empty, token).ConfigureAwait(false); }"
            : "Task StopAsync(CancellationToken token) => Task.CompletedTask;";

        string coordinatorConstructor = shape == "second-acquisition"
            ? "ILongRestAdmittedMaintenance maintenance, IGrimoireConnectionAdmissionGate gate"
            : "ILongRestAdmittedMaintenance maintenance";

        string coordinatorBases = shape == "concrete-shell"
            ? "ILongRestAdmittedSweep, RetroDownfall.Arcanum.Core.LongRest.ILongRestSweepCoordinator"
            : "ILongRestAdmittedSweep";

        string coordinatorInterface = shape == "concrete-shell"
            ? """
            namespace RetroDownfall.Arcanum.Core.LongRest
            {
                public interface ILongRestSweepCoordinator
                {
                    Task ResumeAsync(Guid operationId, CancellationToken ct);
                }
            }
            """
            : "";

        return $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using RetroDownfall.Arcanum.Infrastructure.Data;
            using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RetroDownfall.Arcanum.Api")]

            public class Worker(IServiceScopeFactory scopes, IGrimoireConnectionAdmissionGate admission) : IHostedService
            {
                public async Task StartAsync(CancellationToken token)
                {
                    await RunOneAsync(Guid.Empty, token).ConfigureAwait(false);
                }

                public {{stopSignature}}

                private async Task RunOneAsync(Guid operation, CancellationToken ct)
                {
                    if (!admission.TryAcquireWorkLease(GrimoireWorkKind.{{kind}}, out IGrimoireWorkLease? admitted)) return;

                    {{acquired}}

                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();

                    ILongRestAdmittedSweep sweep = scope.ServiceProvider.GetRequiredService<ILongRestAdmittedSweep>();

                    await sweep.ResumeAdmittedAsync(operation, lease, ct).ConfigureAwait(false);
                }
            }

            {{otherWorker}}

            public static class Composition
            {
                public static void Configure(IServiceCollection services)
                {
                    services.AddHostedService<Worker>();

                    {{otherRegistration}}

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

            {{coordinatorInterface}}

            namespace RetroDownfall.Arcanum.Infrastructure.Operations.LongRest
            {
                public interface ILongRestAdmittedSweep
                {
                    Task ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct);
                }

                internal interface ILongRestAdmittedMaintenance
                {
                    Task ProcessAdmittedAsync({{LeaseHandoffMaintenanceParameters(shape)}});
                }

                {{types}}

                internal sealed class LongRestSweepCoordinator({{coordinatorConstructor}}) : {{coordinatorBases}}
                {
                    public async Task ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct)
                    {
                        if (admission is null) throw new ArgumentNullException(nameof(admission));

                        await ResumeCoreAsync(operationId, null, admission, ct).ConfigureAwait(false);
                    }

                    private async Task ResumeCoreAsync(Guid operationId, object? capability, IGrimoireWorkLease admission, CancellationToken ct)
                    {
                        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, admission.MaintenanceRevocation);

                        await RunOwnedAsync(operationId, capability, admission, lifetime.Token).ConfigureAwait(false);
                    }

                    private async Task RunOwnedAsync(Guid operation, object? capability, IGrimoireWorkLease admission, CancellationToken ct)
                    {
                        {{prelude}}

                        {{call}}

                        {{after}}
                    }

                    {{members}}
                }
            }
            """;
    }

    // The root's compilation: the serving composition, the admitted maintenance and the paid door. The group is begun on the lease
    // parameter, and what the root does with the formal after its fold is the one thing `shape` varies.
    private static string LeaseHandoffApiSource(string shape)
    {
        string afterFold = shape switch
        {
            "formal-disposed" or "loop-formal-disposed" or "goto-formal-disposed" => "await admission.DisposeAsync().ConfigureAwait(false);",
            "formal-stored" => "_held = admission;",
            "formal-captured" => "Func<GrimoireWorkKind> kind = () => admission.Kind;\n\n                    _ = kind();",
            "formal-spawned" => "_ = Task.Run(() => admission.Kind);",
            _ => "",
        };

        string members = shape is "formal-stored" or "second-lease-param" ? "private IGrimoireWorkLease? _held;" : "";

        // A second lease the caller passes, which the root keeps: the heap store is what the cut would otherwise grant Work across.
        string beforeFold = shape == "second-lease-param" ? "_held = other;" : "";

        // A second handoff root inside the first one's closure: the fold is handed the lease again, and owns the paid door.
        string nestedFolder = shape == "nested"
            ? """
                internal sealed class LongRestFolder
                {
                    public async Task FoldHandoffAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct)
                    {
                        if (!admission.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group) || group is null)
                        {
                            throw new InvalidOperationException("The external-effect group was refused.");
                        }

                        await using IGrimoireExternalEffectGroup ownedEffect = group;

                        System.IO.File.Delete("lease-handoff-send");

                        await Task.CompletedTask;
                    }
                }
                """
            : "";

        string fold = shape == "nested"
            ? "await new LongRestFolder().FoldHandoffAsync(operationId, admission, lifetime.Token).ConfigureAwait(false);"
            : "await FoldAsync(operationId, capability, workerOwner, admission, lifetime.Token).ConfigureAwait(false);";

        string otherMaintenance = shape == "two-implementations"
            ? """
                internal sealed class OtherMaintenance : ILongRestAdmittedMaintenance
                {
                    public Task ProcessAdmittedAsync(Guid operationId, string workerOwner, object? capability, IGrimoireWorkLease admission, CancellationToken ct) => Task.CompletedTask;
                }
                """
            : "";

        string otherRegistration = shape == "two-implementations"
            ? """
                services.AddScoped<RetroDownfall.Arcanum.Api.Intelligence.OtherMaintenance>();

                        services.AddScoped<ILongRestAdmittedMaintenance>(static sp => sp.GetRequiredService<RetroDownfall.Arcanum.Api.Intelligence.OtherMaintenance>());
                """
            : "";

        string concreteCaller = shape == "concrete-shell"
            ? """
                internal sealed class SneakyApi(LongRestMaintenance maintenance)
                {
                    public Task RunAsync(IGrimoireWorkLease lease, CancellationToken ct) => maintenance.ProcessAdmittedAsync(Guid.Empty, "sneaky", null, lease, ct);
                }
                """
            : "";

        // A formal passed by reference cannot be the root's admission origin, and an asynchronous method cannot declare one.
        string maintenanceClass = shape == "in-formal"
            ? $$"""
                internal sealed class LongRestMaintenance : ILongRestAdmittedMaintenance
                {
                    public Task ProcessAdmittedAsync({{LeaseHandoffMaintenanceParameters(shape)}}) => Task.CompletedTask;
                }
                """
            : $$"""
                internal sealed class LongRestMaintenance : ILongRestAdmittedMaintenance
                {
                    {{members}}

                    public async Task ProcessAdmittedAsync({{LeaseHandoffMaintenanceParameters(shape)}})
                    {
                        if (admission is null) throw new ArgumentNullException(nameof(admission));

                        if (admission.Kind != GrimoireWorkKind.LongRestSweep) throw new ArgumentException("Long Rest work requires a Long Rest sweep admission.", nameof(admission));

                        ct.ThrowIfCancellationRequested();

                        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, admission.MaintenanceRevocation);

                        {{beforeFold}}

                        {{fold}}

                        {{afterFold}}
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

                        System.IO.File.Delete("lease-handoff-send");

                        return await accept(dispatch.Token).ConfigureAwait(false);
                    }
                }
                """;

        return $$"""
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

                        {{otherRegistration}}

                        return services;
                    }
                }
            }

            namespace RetroDownfall.Arcanum.Api.Intelligence
            {
                {{otherMaintenance}}

                {{nestedFolder}}

                {{maintenanceClass}}

                {{concreteCaller}}
            }
            """;
    }

    // The protocol is recognised by assembly identity, so the fixture's two compilations carry the production assembly names; the
    // framework surface is the production reference pack minus the production projects themselves.
    private static MetadataReference[] LeaseHandoffFrameworkReferences() => [.. HostedGrimoireProducerInventory.ProductionCompilations
        .Single(static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Api")
        .References.Where(static reference => reference is not CompilationReference)];

    private static (CSharpCompilation Infrastructure, CSharpCompilation Api) LeaseHandoffCompilations(string shape)
    {
        MetadataReference[] references = LeaseHandoffFrameworkReferences();

        CSharpCompilationOptions options = new(OutputKind.DynamicallyLinkedLibrary);

        CSharpCompilation infrastructure = CSharpCompilation.Create("RetroDownfall.Arcanum.Infrastructure",
            [CSharpSyntaxTree.ParseText(LeaseHandoffInfrastructureSource(shape), path: LeaseHandoffWorkerPath)], references, options);

        CSharpCompilation api = CSharpCompilation.Create("RetroDownfall.Arcanum.Api",
            [CSharpSyntaxTree.ParseText(LeaseHandoffApiSource(shape), path: "src/Fixture.cs")], [.. references, infrastructure.ToMetadataReference()], options);

        Assert.All(new[] { infrastructure, api }, static compilation => Assert.Empty(compilation.GetDiagnostics().Where(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

        return (infrastructure, api);
    }

    private sealed record LeaseHandoffRun(
        string Shape,
        CSharpCompilation Infrastructure,
        CSharpCompilation Api,
        HostedProducerDiscovery<HostedProducerSite> Result,
        IReadOnlyList<HostedProducerAdmittedStateObservation> States)
    {
        internal SyntaxTree[] Trees => [.. Infrastructure.SyntaxTrees, .. Api.SyntaxTrees];

        internal HostedProducerSite[] Under(string operation) => [.. Result.Items.Where(site => site.AuthorityOperationId == operation)];

        internal HostedProducerSite Single(string operation, HostedProducerSiteKind kind, string callee) =>
            Assert.Single(Under(operation), site => site.Kind == kind && site.Callee == callee);

        internal int StatesOf(string operation) => States.Count(state => state.RootOperation == operation);

        internal string[] Codes => [.. Result.Diagnostics.Select(static diagnostic => diagnostic.Code + "|" + diagnostic.Detail.Split(';')[0]).Order(StringComparer.Ordinal)];

        internal HostedProducerRootTraversalMetric? Root(string operation) =>
            Result.AnalysisMetrics?.RootTraversals.SingleOrDefault(root => root.RootOperation == operation);
    }

    private static HostedProducerOperationEntry LeaseHandoffRootEntry(bool declared = true) => new(
        LeaseHandoffRootOperation, "src/Fixture.cs", "RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance", "ProcessAdmittedAsync",
        HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.LongRestSweep, null, [], declared ? "admission" : null);

    // `entries` is the Worker service's declared operations; the caller is always the hosted worker's start.
    private LeaseHandoffRun RunLeaseHandoff(string shape, bool declareRoot = true, bool declareCaller = true, int rootWorkers = 1, bool reverse = false)
    {
        (CSharpCompilation infrastructure, CSharpCompilation api) = LeaseHandoffCompilations(shape);

        HostedProducerOperationEntry caller = OrdinaryRoot() with
        {
            SourcePath = LeaseHandoffWorkerPath,
            WorkKind = shape == "different-work-kind" ? GrimoireWorkKind.WorkspaceIndexing : GrimoireWorkKind.LongRestSweep,
        };

        HostedProducerOperationEntry[] operations =
        [
            .. declareCaller ? [caller] : Array.Empty<HostedProducerOperationEntry>(),
            .. declareCaller && shape == "two-callers" ? [caller with { OperationId = LeaseHandoffSecondCallerOperation, Member = "StopAsync" }] : Array.Empty<HostedProducerOperationEntry>(),
            .. declareRoot ? [LeaseHandoffRootEntry()] : Array.Empty<HostedProducerOperationEntry>(),
            .. shape == "nested" ? [LeaseHandoffRootEntry() with
            {
                OperationId = LeaseHandoffNestedOperation,
                EnclosingType = "RetroDownfall.Arcanum.Api.Intelligence.LongRestFolder",
                Member = "FoldHandoffAsync",
            }] : Array.Empty<HostedProducerOperationEntry>(),
        ];

        // A second hosted service with its own kind-17 acquisition that reaches the same sweep: a root of another service.
        HostedProducerServiceEntry[] services = shape == "other-service"
            ? [new("Worker", operations), new("OtherWorker", [OrdinaryRoot("OtherWorker.StartAsync") with
            {
                SourcePath = LeaseHandoffWorkerPath,
                EnclosingType = "OtherWorker",
                WorkKind = GrimoireWorkKind.LongRestSweep,
            }])]
            : [new("Worker", operations)];

        ConcurrentQueue<HostedProducerAdmittedStateObservation> states = new();

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [infrastructure, api],
            HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([infrastructure, api]),
            services,
            [],
            rootWorkers: rootWorkers,
            reverseRootFamilies: reverse,
            admittedStateObserver: states.Enqueue);

        LeaseHandoffRun run = new(shape, infrastructure, api, result, [.. states]);

        foreach (HostedProducerSite site in result.Items)
        {
            output.WriteLine($"LEASE_HANDOFF_SITE shape={shape} {site.AuthorityOperationId} {site.EnclosingType}.{site.Member} {site.Kind} {site.Callee} work={(site.WorkFrontierCapsuleId is null ? "null" : "set")} effect={(site.EffectFrontierCapsuleId is null ? "null" : "set")}");
        }

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"LEASE_HANDOFF_DIAGNOSTIC shape={shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        return run;
    }

    [Fact]
    public void ALeaseCarriedAwaitedCallCutsToTheRootWhoseSendsCarryTheFormalCapsuleAndWhoseBindingNamesTheAcquisition()
    {
        LeaseHandoffRun run = RunLeaseHandoff("positive");

        Assert.Empty(run.Result.Diagnostics);

        HostedProducerSite acquired = run.Single(LeaseHandoffCallerOperation, HostedProducerSiteKind.EffectFrontier, LeaseHandoffAcquisition);

        Assert.Equal(GrimoireWorkKind.LongRestSweep, acquired.AdmittedWorkKind);

        Assert.Equal(acquired.CapsuleId, acquired.WorkFrontierCapsuleId);

        // The root's own Work row: the formal is to the root what the acquisition is to the worker.
        HostedProducerSite formal = run.Single(LeaseHandoffRootOperation, HostedProducerSiteKind.EffectFrontier, HostedGrimoireProducerInventory.RootLeaseFormalCallee);

        Assert.Equal("ProcessAdmittedAsync", formal.Member);

        Assert.Equal(GrimoireWorkKind.LongRestSweep, formal.AdmittedWorkKind);

        Assert.Equal(formal.CapsuleId, formal.WorkFrontierCapsuleId);

        Assert.NotEqual(acquired.CapsuleId, formal.CapsuleId);

        HostedProducerSite group = run.Single(LeaseHandoffRootOperation, HostedProducerSiteKind.EffectFrontier, LeaseHandoffGroup);

        HostedProducerSite send = run.Single(LeaseHandoffRootOperation, HostedProducerSiteKind.FileSystemEffect, LeaseHandoffSend);

        // The root's send carries the formal's Work and the group begun on the formal, never the worker's acquisition.
        Assert.Equal(formal.CapsuleId, group.WorkFrontierCapsuleId);

        Assert.Equal(formal.CapsuleId, send.WorkFrontierCapsuleId);

        Assert.Equal(group.CapsuleId, send.EffectFrontierCapsuleId);

        // The handoff-binding row: the caller's call site, the caller's acquisition, the root it hands the lease to.
        HostedProducerSite binding = run.Single(LeaseHandoffCallerOperation, HostedProducerSiteKind.LeaseHandoff, LeaseHandoffRootOperation);

        Assert.Equal("RunOwnedAsync", binding.Member);

        Assert.Equal(acquired.CapsuleId, binding.WorkFrontierCapsuleId);

        Assert.Null(binding.EffectFrontierCapsuleId);

        Assert.Null(binding.AdmittedWorkKind);

        // The closure is judged under the root only: the caller's root holds none of its sites.
        Assert.DoesNotContain(run.Under(LeaseHandoffCallerOperation), static site => site.Callee is LeaseHandoffSend or LeaseHandoffGroup);

        // The manifest the rows render to is self-consistent, and it ties the formal to the worker's acquisition.
        string manifest = HostedGrimoireProducerInventory.RenderCapsuleManifest(run.Result.Items);

        Assert.Empty(HostedGrimoireProducerInventory.ParseCapsuleManifest(manifest.Split('\n')).Diagnostics);
    }

    [Fact]
    public void TheManifestOfACutEdgeIsRefusedWithoutItsBindingOrItsFormalOrWithAnotherWorkKind()
    {
        LeaseHandoffRun run = RunLeaseHandoff("positive");

        HostedProducerSite[] items = [.. run.Result.Items];

        HostedProducerSite binding = Assert.Single(items, static site => site.Kind == HostedProducerSiteKind.LeaseHandoff);

        HostedProducerSite formal = Assert.Single(items, static site => site.Callee == HostedGrimoireProducerInventory.RootLeaseFormalCallee);

        string[] Codes(IEnumerable<HostedProducerSite> sites) => [.. HostedGrimoireProducerInventory.ParseCapsuleManifest(HostedGrimoireProducerInventory.RenderCapsuleManifest(sites).Split('\n'))
            .Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct().Order(StringComparer.Ordinal)];

        Assert.Empty(Codes(items));

        // A formal nobody binds.
        Assert.Equal([HostedGrimoireProducerInventory.ManifestHandoffUnboundCode], Codes(items.Where(site => site != binding)));

        // A binding that names a root with no formal.
        Assert.Equal([HostedGrimoireProducerInventory.ManifestHandoffUnboundCode],
            Codes(items.Where(site => site != formal && site.WorkFrontierCapsuleId != formal.CapsuleId)));

        // A formal that admits another work kind than the acquisition its binding carries.
        Assert.Equal([HostedGrimoireProducerInventory.ManifestHandoffUnboundCode],
            Codes(items.Select(site => site == formal ? site with { AdmittedWorkKind = GrimoireWorkKind.WorkspaceIndexing } : site)));
    }

    [Theory]
    [InlineData("non-must-alias", "the lease argument is not a must-alias of one acquisition")]
    [InlineData("effect-open", "an external-effect group is open at the call")]
    [InlineData("disposed-before-call", "the caller holds no live Work at the call")]
    [InlineData("fire-and-forget", "the call is not directly awaited")]
    [InlineData("stored-task", "the call is not directly awaited")]
    [InlineData("different-work-kind", "the caller's Work kind is not the root's")]
    [InlineData("ended-in-arguments", "the lease has ended before the call begins")]
    [InlineData("inside-lambda", "the call is not in the caller's own method body")]
    [InlineData("two-implementations", "the call does not resolve to exactly the root")]
    [InlineData("second-acquisition", "the Work the caller holds is not the Work of the lease argument")]
    [InlineData("lease-omitted", "the lease argument is not exactly one by-value argument")]
    [InlineData("configure-await-arg-ends", "the await configures the call with an argument that is not a constant")]
    [InlineData("configure-await-variable", "the await configures the call with an argument that is not a constant")]
    [InlineData("second-lease-param", "another argument may hold the caller's lease")]
    [InlineData("lease-in-holder-argument", "another argument may hold the caller's lease")]
    [InlineData("lease-in-holder-local", "another argument may hold the caller's lease")]
    [InlineData("receiver-from-lease", "the receiver may hold the caller's lease")]
    [InlineData("loop-formal-disposed", "the call sits in a loop and the root does not preserve its formal")]
    [InlineData("goto-formal-disposed", "the call sits in a loop and the root does not preserve its formal")]
    [InlineData("spawned-disposer", "a closure of the caller mentions the caller's lease")]
    public void ACallThatFailsAnyPreconditionIsRefusedAndTheClosureIsJudgedInLineUnderTheCallersRoot(string shape, string reason)
    {
        LeaseHandoffRun run = RunLeaseHandoff(shape);

        HostedProducerInventoryDiagnostic refusal = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == HostedGrimoireProducerInventory.LeaseHandoffUnprovenCode);

        Assert.Equal(LeaseHandoffRootOperation + "; " + reason, refusal.Detail);

        // No call was cut, so none is bound, and every call to the root is also an unanalysed caller.
        Assert.DoesNotContain(run.Result.Items, static site => site.Kind == HostedProducerSiteKind.LeaseHandoff);

        Assert.Contains(run.Result.Diagnostics, static diagnostic => diagnostic.Code == HostedGrimoireProducerInventory.LeaseHandoffCallerUnanalysedCode);

        // The closure is still traversed, but under the caller's root and its own Work: the sends are the caller's rows.
        Assert.Contains(run.Under(LeaseHandoffCallerOperation), static site => site.Callee == LeaseHandoffSend);
    }

    [Fact]
    public void ACutCallLeavesTheCallersLeaseAliveForItsLaterSendsWhenTheRootPreservesTheFormal()
    {
        LeaseHandoffRun run = RunLeaseHandoff("after-call");

        Assert.Empty(run.Result.Diagnostics);

        HostedProducerSite acquired = run.Single(LeaseHandoffCallerOperation, HostedProducerSiteKind.EffectFrontier, LeaseHandoffAcquisition);

        // The caller's own read after the call still holds the worker's Work.
        HostedProducerSite later = run.Single(LeaseHandoffCallerOperation, HostedProducerSiteKind.FileSystemRead, LeaseHandoffAfterRead);

        Assert.Equal(acquired.CapsuleId, later.WorkFrontierCapsuleId);
    }

    [Fact]
    public void AHandoffRootThatHandsItsOwnLeaseOnIsCutFromItsClosureAndBoundToItsOwnFormal()
    {
        LeaseHandoffRun run = RunLeaseHandoff("nested");

        Assert.Empty(run.Result.Diagnostics);

        HostedProducerSite acquired = run.Single(LeaseHandoffCallerOperation, HostedProducerSiteKind.EffectFrontier, LeaseHandoffAcquisition);

        HostedProducerSite outer = run.Single(LeaseHandoffRootOperation, HostedProducerSiteKind.EffectFrontier, HostedGrimoireProducerInventory.RootLeaseFormalCallee);

        HostedProducerSite inner = run.Single(LeaseHandoffNestedOperation, HostedProducerSiteKind.EffectFrontier, HostedGrimoireProducerInventory.RootLeaseFormalCallee);

        Assert.Equal(3, new[] { acquired.CapsuleId, outer.CapsuleId, inner.CapsuleId }.Distinct().Count());

        // The worker binds the outer root to its acquisition; the outer root binds the inner root to its own formal.
        HostedProducerSite outerBinding = run.Single(LeaseHandoffCallerOperation, HostedProducerSiteKind.LeaseHandoff, LeaseHandoffRootOperation);

        HostedProducerSite innerBinding = run.Single(LeaseHandoffRootOperation, HostedProducerSiteKind.LeaseHandoff, LeaseHandoffNestedOperation);

        Assert.Equal(acquired.CapsuleId, outerBinding.WorkFrontierCapsuleId);

        Assert.Equal(outer.CapsuleId, innerBinding.WorkFrontierCapsuleId);

        // The send belongs to the inner root alone, with the inner formal's Work and the group begun on it.
        HostedProducerSite group = run.Single(LeaseHandoffNestedOperation, HostedProducerSiteKind.EffectFrontier, LeaseHandoffGroup);

        HostedProducerSite send = run.Single(LeaseHandoffNestedOperation, HostedProducerSiteKind.FileSystemEffect, LeaseHandoffSend);

        Assert.Equal(inner.CapsuleId, send.WorkFrontierCapsuleId);

        Assert.Equal(group.CapsuleId, send.EffectFrontierCapsuleId);

        Assert.DoesNotContain(run.Under(LeaseHandoffRootOperation), static site => site.Callee is LeaseHandoffSend or LeaseHandoffGroup);

        Assert.DoesNotContain(run.Under(LeaseHandoffCallerOperation), static site => site.Callee is LeaseHandoffSend or LeaseHandoffGroup);

        // The manifest ties each formal to its binder, and refuses the chain once the inner root admits another work kind than its binder.
        HostedProducerSite[] items = [.. run.Result.Items];

        string[] Codes(IEnumerable<HostedProducerSite> sites) => [.. HostedGrimoireProducerInventory.ParseCapsuleManifest(HostedGrimoireProducerInventory.RenderCapsuleManifest(sites).Split('\n'))
            .Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct().Order(StringComparer.Ordinal)];

        Assert.Empty(Codes(items));

        Assert.Equal([HostedGrimoireProducerInventory.ManifestHandoffUnboundCode],
            Codes(items.Select(site => site == inner ? site with { AdmittedWorkKind = GrimoireWorkKind.WorkspaceIndexing } : site)));

        Assert.Equal([HostedGrimoireProducerInventory.ManifestHandoffUnboundCode], Codes(items.Where(site => site != innerBinding)));
    }

    [Fact]
    public void ACallFromARootOfAnotherServiceIsRefusedWhateverItsWorkKindAndTheRootIsJudgedInLineThere()
    {
        LeaseHandoffRun run = RunLeaseHandoff("other-service");

        // The root's own service cuts and binds its call. The shared call site is therefore proven, and not an unanalysed caller.
        Assert.Single(run.Under(LeaseHandoffCallerOperation), static site => site.Kind == HostedProducerSiteKind.LeaseHandoff);

        Assert.DoesNotContain(run.Under("OtherWorker.StartAsync"), static site => site.Kind == HostedProducerSiteKind.LeaseHandoff);

        HostedProducerInventoryDiagnostic refusal = Assert.Single(run.Result.Diagnostics);

        Assert.Equal(HostedGrimoireProducerInventory.LeaseHandoffUnprovenCode, refusal.Code);

        Assert.StartsWith("OtherWorker.StartAsync/call@", refusal.Identity, StringComparison.Ordinal);

        Assert.Equal(LeaseHandoffRootOperation + "; the caller is a root of another service", refusal.Detail);

        // The other service pays for the closure under its own root, with its own acquisition's Work.
        HostedProducerSite acquired = run.Single("OtherWorker.StartAsync", HostedProducerSiteKind.EffectFrontier, LeaseHandoffAcquisition);

        Assert.Equal(acquired.CapsuleId, run.Single("OtherWorker.StartAsync", HostedProducerSiteKind.FileSystemEffect, LeaseHandoffSend).WorkFrontierCapsuleId);

        // And the root's own closure still belongs to the root alone.
        Assert.Equal(run.Single(LeaseHandoffRootOperation, HostedProducerSiteKind.EffectFrontier, HostedGrimoireProducerInventory.RootLeaseFormalCallee).CapsuleId,
            run.Single(LeaseHandoffRootOperation, HostedProducerSiteKind.FileSystemEffect, LeaseHandoffSend).WorkFrontierCapsuleId);
    }

    // What the root does with the formal after its fold, and what that costs the caller. The first two are ends the lifetime walk already
    // finds; a closure that captures the formal, and a task that is spawned over it, are ends only Formal Preservation finds, which is why the
    // caller's own read after the call is the row that tells them apart from the control above.
    [Theory]
    [InlineData("formal-disposed", "HOSTED_DISPOSAL_TARGET_UNRESOLVED|System.IAsyncDisposable.DisposeAsync")]
    [InlineData("formal-stored", null)]
    [InlineData("formal-captured", null)]
    [InlineData("formal-spawned", null)]
    public void AFormalTheRootDisposesStoresOrCapturesEndsTheCallersLifetimeAtTheCall(string shape, string? rootDiagnostic)
    {
        LeaseHandoffRun run = RunLeaseHandoff(shape);

        // The call itself is still a proven cut: the root is judged on its own, and its send before the departure keeps the formal's Work.
        HostedProducerSite binding = run.Single(LeaseHandoffCallerOperation, HostedProducerSiteKind.LeaseHandoff, LeaseHandoffRootOperation);

        HostedProducerSite formal = run.Single(LeaseHandoffRootOperation, HostedProducerSiteKind.EffectFrontier, HostedGrimoireProducerInventory.RootLeaseFormalCallee);

        HostedProducerSite send = run.Single(LeaseHandoffRootOperation, HostedProducerSiteKind.FileSystemEffect, LeaseHandoffSend);

        Assert.Equal(formal.CapsuleId, send.WorkFrontierCapsuleId);

        Assert.NotNull(binding.WorkFrontierCapsuleId);

        // After the call the caller's lease is gone, whatever the root did with it: the caller's own read reports its missing Work.
        Assert.Null(run.Single(LeaseHandoffCallerOperation, HostedProducerSiteKind.FileSystemRead, LeaseHandoffAfterRead).WorkFrontierCapsuleId);

        string[] expected = rootDiagnostic is null
            ? ["HOSTED_SITE_WORK_FRONTIER_MISSING|" + LeaseHandoffAfterRead]
            : [rootDiagnostic, "HOSTED_SITE_WORK_FRONTIER_MISSING|" + LeaseHandoffAfterRead];

        Assert.Equal(expected.Order(StringComparer.Ordinal), run.Codes);
    }

    [Fact]
    public void AReferenceToTheRootThatNoRootProvedIsAnUnanalysedCallerWhateverReachesItAtRunTime()
    {
        LeaseHandoffRun run = RunLeaseHandoff("unreached-caller");

        // The one call the worker reaches is proven and bound; the side call is reached by no root at all.
        HostedProducerSite binding = run.Single(LeaseHandoffCallerOperation, HostedProducerSiteKind.LeaseHandoff, LeaseHandoffRootOperation);

        HostedProducerInventoryDiagnostic unanalysed = Assert.Single(run.Result.Diagnostics);

        Assert.Equal(HostedGrimoireProducerInventory.LeaseHandoffCallerUnanalysedCode, unanalysed.Code);

        MethodDeclarationSyntax side = Assert.Single(run.Trees.SelectMany(static tree => tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()),
            static method => method.Identifier.ValueText == "SideAsync");

        InvocationExpressionSyntax sideCall = side.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(static call => call.Expression.ToString() == "maintenance.ProcessAdmittedAsync");

        Assert.Equal(sideCall.SyntaxTree.FilePath + ":" + sideCall.SpanStart, unanalysed.Identity);

        // The proven call is the one the worker reaches, in RunOwnedAsync; the side call is not it.
        Assert.Equal("RunOwnedAsync", binding.Member);

        Assert.NotEqual(HostedGrimoireProducerInventory.Fingerprint(sideCall), binding.SyntaxFingerprint);

        Assert.Equal(LeaseHandoffRootOperation, binding.Callee);
    }

    [Fact]
    public void ACallThroughADelegateIsNotACutEdgeAndItsReferenceIsAnUnanalysedCaller()
    {
        LeaseHandoffRun run = RunLeaseHandoff("via-delegate");

        // The delegate is invoked, never called as the root: nothing is cut and nothing is bound.
        Assert.DoesNotContain(run.Result.Items, static site => site.Kind == HostedProducerSiteKind.LeaseHandoff);

        HostedProducerInventoryDiagnostic unanalysed = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == HostedGrimoireProducerInventory.LeaseHandoffCallerUnanalysedCode);

        Assert.EndsWith("a reference that is not an invocation.", unanalysed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProvenCallSiteOfTheRootGetsItsOwnBindingRow()
    {
        LeaseHandoffRun run = RunLeaseHandoff("two-calls");

        Assert.Empty(run.Result.Diagnostics);

        HostedProducerSite[] bindings = [.. run.Under(LeaseHandoffCallerOperation).Where(static site => site.Kind == HostedProducerSiteKind.LeaseHandoff)];

        Assert.Equal(2, bindings.Length);

        Assert.Equal(2, bindings.Select(static binding => binding.CapsuleId).Distinct().Count());

        Assert.Single(bindings.Select(static binding => binding.WorkFrontierCapsuleId).Distinct());

        // One root, one formal, and the root's closure still has exactly one send.
        Assert.Single(run.Under(LeaseHandoffRootOperation), static site => site.Callee == HostedGrimoireProducerInventory.RootLeaseFormalCallee);

        Assert.Single(run.Under(LeaseHandoffRootOperation), static site => site.Callee == LeaseHandoffSend);
    }

    [Fact]
    public void TheRootsClosureIsTraversedOnceWhetherTheCallerOrTheDeclaredRootReachesItFirst()
    {
        LeaseHandoffRun cut = RunLeaseHandoff("positive");

        // The declared root alone: no caller is declared, so only the root's own traversal produces its states.
        LeaseHandoffRun alone = RunLeaseHandoff("positive", declareCaller: false);

        // The same worker with the root undeclared: the closure belongs to the caller's root and is traversed under its budget.
        LeaseHandoffRun inline = RunLeaseHandoff("positive", declareRoot: false);

        int rootStates = alone.StatesOf(LeaseHandoffRootOperation);

        Assert.True(rootStates > 0);

        // The cut reaches exactly the declared root's states: the state cache dedupes them, so none is admitted twice.
        Assert.Equal(rootStates, cut.StatesOf(LeaseHandoffRootOperation));

        Assert.Equal(rootStates, cut.Root(LeaseHandoffRootOperation)!.AnalyzedStates);

        // The caller's root falls by at least the closure the root now owns.
        int callerCut = cut.StatesOf(LeaseHandoffCallerOperation);

        int callerInline = inline.StatesOf(LeaseHandoffCallerOperation);

        Assert.True(callerInline - callerCut >= rootStates, $"inline={callerInline} cut={callerCut} root={rootStates}");

        // The pool takes the service as one family, so it agrees and admits the closure no more often than one worker does.
        LeaseHandoffRun pooled = RunLeaseHandoff("positive", rootWorkers: 3);

        Assert.Equal(rootStates, pooled.StatesOf(LeaseHandoffRootOperation));

        Assert.Equal(cut.Result.Items.Select(HostedGrimoireProducerInventory.StableSiteIdentity).Order(StringComparer.Ordinal),
            pooled.Result.Items.Select(HostedGrimoireProducerInventory.StableSiteIdentity).Order(StringComparer.Ordinal));

        Assert.Empty(pooled.Result.Diagnostics);
    }

    [Fact]
    public void AHandoffDeclarationWhoseFormalIsMissingOrNotALeaseIsAnUnresolvedRoot()
    {
        (CSharpCompilation infrastructure, CSharpCompilation api) = LeaseHandoffCompilations("positive");

        foreach (string formal in new[] { "missing", "operationId", "ct" })
        {
            HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
                [infrastructure, api],
                HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([infrastructure, api]),
                [new("Worker", [OrdinaryRoot() with { SourcePath = LeaseHandoffWorkerPath, WorkKind = GrimoireWorkKind.LongRestSweep }, LeaseHandoffRootEntry() with { LeaseFormal = formal }])],
                []);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ROOT_UNRESOLVED" && diagnostic.Identity == LeaseHandoffRootOperation);

            // The caller's call to the unusable root is refused for that reason and judged in line, never cut to a root with no formal.
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == HostedGrimoireProducerInventory.LeaseHandoffUnprovenCode
                && diagnostic.Detail == LeaseHandoffRootOperation + "; the declared root is not usable");

            Assert.DoesNotContain(result.Items, static site => site.Kind == HostedProducerSiteKind.LeaseHandoff);
        }
    }

    // A formal passed by reference is not a by-value admission origin, and it is refused as a declaration, so no call reaches the
    // by-value check on the argument: the call is refused because its root is not usable.
    [Fact]
    public void AHandoffFormalPassedByReferenceIsAnUnresolvedRootAndItsCallIsUnproven()
    {
        LeaseHandoffRun run = RunLeaseHandoff("in-formal");

        Assert.Contains(run.Result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ROOT_UNRESOLVED" && diagnostic.Identity == LeaseHandoffRootOperation);

        Assert.Contains(run.Result.Diagnostics, static diagnostic => diagnostic.Code == HostedGrimoireProducerInventory.LeaseHandoffUnprovenCode
            && diagnostic.Detail == LeaseHandoffRootOperation + "; the declared root is not usable");

        Assert.DoesNotContain(run.Result.Items, static site => site.Kind == HostedProducerSiteKind.LeaseHandoff);
    }

    // The refusal only removes authority: what the in-line traversal reported before the cut existed it still reports. The root keeps
    // the second alias in a field, so Work ends at that store and the paid send after it is the row that says so, exactly as it was.
    [Fact]
    public void ASecondAliasOfTheLeaseThatTheRootKeepsStillCostsTheRootsSendItsWorkExactlyAsTheInLineTraversalDid()
    {
        string missing = "HOSTED_SITE_WORK_FRONTIER_MISSING|" + LeaseHandoffSend;

        Assert.Contains(missing, RunLeaseHandoff("second-lease-param").Codes);

        // The same fixture with the root undeclared is the traversal that existed before the cut.
        Assert.Contains(missing, RunLeaseHandoff("second-lease-param", declareRoot: false).Codes);
    }

    // The positive controls of the refusals above: a call in a loop is cut when the root proves it preserves its formal (production's
    // owned run calls the root from a loop), and a closure of the caller that never mentions the lease changes nothing.
    [Theory]
    [InlineData("loop-preserved")]
    [InlineData("unrelated-closure")]
    public void ACallInALoopOrBesideAnUnrelatedClosureIsStillCut(string shape)
    {
        LeaseHandoffRun run = RunLeaseHandoff(shape);

        Assert.Empty(run.Result.Diagnostics);

        HostedProducerSite acquired = run.Single(LeaseHandoffCallerOperation, HostedProducerSiteKind.EffectFrontier, LeaseHandoffAcquisition);

        Assert.Equal(acquired.CapsuleId, run.Single(LeaseHandoffCallerOperation, HostedProducerSiteKind.LeaseHandoff, LeaseHandoffRootOperation).WorkFrontierCapsuleId);
    }

    // The validator holds the same ties as the manifest parser, and the catalog is what makes them mean something: a root lease formal
    // belongs to an operation that declares one, and a binding row names such an operation of its own service.
    private static string[] ValidateLeaseHandoff(IReadOnlyList<HostedProducerSite> items, HostedProducerOperationEntry root, bool rootInAnotherService = false)
    {
        HostedProducerOperationEntry caller = OrdinaryRoot() with { SourcePath = LeaseHandoffWorkerPath, WorkKind = GrimoireWorkKind.LongRestSweep };

        HostedProducerOperationEntry Owned(HostedProducerOperationEntry entry) => entry with { Sites = [.. items.Where(site => site.AuthorityOperationId == entry.OperationId)] };

        HostedProducerServiceEntry[] services = rootInAnotherService
            ? [new("Worker", [Owned(caller)]), new("Other", [Owned(root)])]
            : [new("Worker", [Owned(caller), Owned(root)])];

        return [.. HostedGrimoireProducerInventory.Validate(services, [], new([.. services.Select(static service => service.ServiceType)], []), new(items, []))
            .Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct().Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void TheValidatorRefusesALeaseFormalRowOrABindingRowThatNoHandoffDeclarationOwns()
    {
        LeaseHandoffRun run = RunLeaseHandoff("positive");

        HostedProducerSite[] items = [.. run.Result.Items];

        Assert.DoesNotContain(ValidateLeaseHandoff(items, LeaseHandoffRootEntry()), static code => code.StartsWith("HOSTED_SITE_LEASE_", StringComparison.Ordinal));

        // A root that declares no formal cannot own a formal row, and its sends then have no Work frontier to resolve to.
        string[] undeclaredFormal = ValidateLeaseHandoff(items, LeaseHandoffRootEntry(declared: false));

        Assert.Contains("HOSTED_SITE_LEASE_FORMAL_UNDECLARED", undeclaredFormal);

        Assert.Contains("HOSTED_SITE_WORK_FRONTIER_MISSING", undeclaredFormal);

        // A binding row whose callee is no declared handoff operation of the caller's service.
        Assert.Contains("HOSTED_SITE_LEASE_HANDOFF_UNDECLARED", ValidateLeaseHandoff(
            [.. items.Select(static site => site.Kind == HostedProducerSiteKind.LeaseHandoff ? site with { Callee = "Worker.NotARoot" } : site)], LeaseHandoffRootEntry()));

        // Nor a root declared in another service: the binding names it, but not within its own service.
        Assert.Contains("HOSTED_SITE_LEASE_HANDOFF_UNDECLARED", ValidateLeaseHandoff(items, LeaseHandoffRootEntry(), rootInAnotherService: true));
    }

    // A declared root whose formal no analysed call ever binds would stand in the inventory with Work and no lease behind it.
    [Fact]
    public void ADeclaredRootThatNoRootProvedACallToIsReportedAtDiscoveryNotOnlyWhenTheManifestIsParsed()
    {
        Assert.DoesNotContain(RunLeaseHandoff("positive").Result.Diagnostics, static diagnostic => diagnostic.Code == HostedGrimoireProducerInventory.LeaseHandoffRootUnboundCode);

        LeaseHandoffRun unbound = RunLeaseHandoff("positive", declareCaller: false);

        HostedProducerInventoryDiagnostic diagnostic = Assert.Single(unbound.Result.Diagnostics, static diagnostic => diagnostic.Code == HostedGrimoireProducerInventory.LeaseHandoffRootUnboundCode);

        Assert.Equal(LeaseHandoffRootOperation, diagnostic.Identity);
    }

    private static string LeaseHandoffDigest(LeaseHandoffRun run) => string.Join("\n",
        run.Result.Items.Select(HostedGrimoireProducerInventory.StableSiteIdentity).Order(StringComparer.Ordinal)
            .Concat(run.Result.Diagnostics.Select(static diagnostic => diagnostic.Code + "|" + diagnostic.Identity + "|" + diagnostic.Detail)));

    // Two roots of the one service cut to the one handoff root. The pool takes whole families, so a service that declares a handoff root
    // is one family: its roots run on the worker that runs the handoff root, whose state cache is the only place the closure is shared.
    [Fact]
    public void TheRootsOfAServiceThatDeclaresAHandoffRootRunOnOneWorkerSoThePoolAdmitsTheClosureOnce()
    {
        LeaseHandoffRun single = RunLeaseHandoff("two-callers");

        int closure = single.StatesOf(LeaseHandoffRootOperation);

        Assert.True(closure > 0);

        Assert.Empty(single.Result.Diagnostics);

        Assert.Equal(2, single.Result.Items.Count(static site => site.Kind == HostedProducerSiteKind.LeaseHandoff));

        foreach ((int workers, bool reverse) in new[] { (2, false), (3, false), (3, true) })
        {
            LeaseHandoffRun pooled = RunLeaseHandoff("two-callers", rootWorkers: workers, reverse: reverse);

            Assert.Equal(LeaseHandoffDigest(single), LeaseHandoffDigest(pooled));

            Assert.Equal(closure, pooled.StatesOf(LeaseHandoffRootOperation));

            Assert.Equal(closure, pooled.Root(LeaseHandoffRootOperation)!.AnalyzedStates);

            Assert.Single(pooled.States.Select(static state => state.Worker).Distinct());
        }
    }

    [Fact]
    public void ARootsFamilyIsItsOwnUnlessItsServiceDeclaresALeaseHandoffRoot()
    {
        Assert.NotEqual(HostedProducerRootWorkers.FamilyOf("Worker", "Worker.StartAsync", serviceDeclaresLeaseHandoff: false),
            HostedProducerRootWorkers.FamilyOf("Worker", "Worker.StopAsync", serviceDeclaresLeaseHandoff: false));

        Assert.Equal(HostedProducerRootWorkers.FamilyOf("Worker", "Worker.StartAsync", serviceDeclaresLeaseHandoff: true),
            HostedProducerRootWorkers.FamilyOf("Worker", "Worker.StopAsync", serviceDeclaresLeaseHandoff: true));

        Assert.NotEqual(HostedProducerRootWorkers.FamilyOf("Worker", "Worker.StartAsync", serviceDeclaresLeaseHandoff: true),
            HostedProducerRootWorkers.FamilyOf("Other", "Worker.StartAsync", serviceDeclaresLeaseHandoff: true));
    }

    // The shared chain above the one proved call site. The Session shell reaches the admitted maintenance through the same coordinator core
    // as the hosted worker, and no root of the inventory traverses the shell, so the census cannot see it. It is accepted only because it
    // funnels into the one call site some root proves, and only for as long as these sets are exactly what was reviewed: a new entry into the
    // chain, or a second caller of any link, changes a set and must be reviewed with the handoff root it feeds.
    private static string[] LeaseHandoffChainReferences(IReadOnlyList<CSharpCompilation> compilations, string containingType, string method) =>
        HostedGrimoireProducerInventory.HandoffChainReferences(compilations, containingType, method);

    private const string LeaseHandoffMaintenancePort = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedMaintenance";

    private const string LeaseHandoffCoordinator = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator";

    [Fact]
    public void TheLongRestHandoffChainHasExactlyItsReviewedEntries()
    {
        IReadOnlyList<CSharpCompilation> production = HostedGrimoireProducerInventory.ProductionCompilations;

        // The handoff method: one call, in the owned run.
        Assert.Equal(["LongRestSweepCoordinator.RunOwnedAsync"], LeaseHandoffChainReferences(production, LeaseHandoffMaintenancePort, "ProcessAdmittedAsync"));

        // The owned run: one call, in the shared core.
        Assert.Equal(["LongRestSweepCoordinator.ResumeCoreAsync"], LeaseHandoffChainReferences(production, LeaseHandoffCoordinator, "RunOwnedAsync"));

        // The shared core: the hosted worker's port, and the Session shell that mints its own lease.
        Assert.Equal(["LongRestSweepCoordinator.ResumeAdmittedAsync", "LongRestSweepCoordinator.ResumeAsync"],
            LeaseHandoffChainReferences(production, LeaseHandoffCoordinator, "ResumeCoreAsync"));

        // The hosted entry: the one worker that mints the kind-17 lease it hands over.
        Assert.Equal(["LongRestSweepHostedService.RunOneAsync"],
            LeaseHandoffChainReferences(production, "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedSweep", "ResumeAdmittedAsync"));

        // The request entry: the Session turn preparer, the only caller of the public sweep coordinator's resume.
        Assert.Equal(["CampaignRollupTurnPreparer.PrepareLiveAsync"],
            LeaseHandoffChainReferences(production, "RetroDownfall.Arcanum.Core.LongRest.ILongRestSweepCoordinator", "ResumeAsync"));
    }

    [Theory]
    [InlineData("positive", new[] { "LongRestSweepCoordinator.RunOwnedAsync" })]
    [InlineData("unreached-caller", new[] { "LongRestSweepCoordinator.RunOwnedAsync", "LongRestSweepCoordinator.SideAsync" })]
    [InlineData("via-delegate", new[] { "LongRestSweepCoordinator.RunOwnedAsync (reference)" })]
    [InlineData("two-calls", new[] { "LongRestSweepCoordinator.RunOwnedAsync", "LongRestSweepCoordinator.RunOwnedAsync" })]
    public void TheChainPinSeesAnotherCallerAnAddedCallAndAReferenceThatIsNotACall(string shape, string[] expected)
    {
        (CSharpCompilation infrastructure, CSharpCompilation api) = LeaseHandoffCompilations(shape);

        Assert.Equal(expected.Order(StringComparer.Ordinal), LeaseHandoffChainReferences([infrastructure, api], LeaseHandoffMaintenancePort, "ProcessAdmittedAsync"));
    }

    private const string LeaseHandoffAdmittedSweep = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedSweep";

    private const string LeaseHandoffSweepCoordinatorPort = "RetroDownfall.Arcanum.Core.LongRest.ILongRestSweepCoordinator";

    // A pin on the interface slot alone would miss a caller that names the class registered for it. Each link is matched on the method's
    // identity: the slot, the methods that implement it, the methods those override, and the methods group references are made of.
    [Fact]
    public void TheChainPinSeesACallerThatNamesTheConcreteTypeInsteadOfTheInterfaceSlot()
    {
        (CSharpCompilation infrastructure, CSharpCompilation api) = LeaseHandoffCompilations("concrete-shell");

        CSharpCompilation[] all = [infrastructure, api];

        Assert.Equal(["LongRestSweepCoordinator.RunOwnedAsync", "SneakyApi.RunAsync"], LeaseHandoffChainReferences(all, LeaseHandoffMaintenancePort, "ProcessAdmittedAsync"));

        Assert.Equal(["SneakyShell.RunAsync", "Worker.RunOneAsync"], LeaseHandoffChainReferences(all, LeaseHandoffAdmittedSweep, "ResumeAdmittedAsync"));

        Assert.Equal(["SneakyShell.RunAsync"], LeaseHandoffChainReferences(all, LeaseHandoffSweepCoordinatorPort, "ResumeAsync"));

        // The same links on the control fixture have no such caller: the pin is not merely louder.
        (CSharpCompilation controlInfrastructure, CSharpCompilation controlApi) = LeaseHandoffCompilations("positive");

        Assert.Equal(["Worker.RunOneAsync"], LeaseHandoffChainReferences([controlInfrastructure, controlApi], LeaseHandoffAdmittedSweep, "ResumeAdmittedAsync"));
    }

    [Theory]
    [InlineData("RetroDownfall.MatcherFixture.IPort", new[] { "ViaBase.B", "ViaDerived.C", "ViaGroup.D (reference)", "ViaPort.A" })]
    [InlineData("RetroDownfall.MatcherFixture.Base", new[] { "ViaBase.B", "ViaDerived.C", "ViaGroup.D (reference)", "ViaPort.A" })]
    [InlineData("RetroDownfall.MatcherFixture.Derived", new[] { "ViaBase.B", "ViaDerived.C", "ViaGroup.D (reference)", "ViaPort.A" })]
    [InlineData("RetroDownfall.MatcherFixture.Unrelated", new[] { "Unrelated.E" })]
    public void TheChainMatcherFollowsTheSlotItsImplementationItsOverridesAndTheMethodGroupsAndNothingElse(string link, string[] expected)
    {
        const string source = """
            using System;
            using System.Threading.Tasks;

            namespace RetroDownfall.MatcherFixture;

            public interface IPort { Task GoAsync(); }

            public class Base : IPort { public virtual Task GoAsync() => Task.CompletedTask; }

            public class Derived : Base { public override Task GoAsync() => Task.CompletedTask; }

            public class ViaPort(IPort port) { public Task A() => port.GoAsync(); }

            public class ViaBase(Base value) { public Task B() => value.GoAsync(); }

            public class ViaDerived(Derived value) { public Task C() => value.GoAsync(); }

            public class ViaGroup(IPort port) { public Func<Task> D() => port.GoAsync; }

            public class Unrelated { public Task GoAsync() => Task.CompletedTask; public Task E() => GoAsync(); }
            """;

        CSharpCompilation compilation = CSharpCompilation.Create("RetroDownfall.MatcherFixture", [CSharpSyntaxTree.ParseText(source)],
            LeaseHandoffFrameworkReferences(), new(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Equal(expected, LeaseHandoffChainReferences([compilation], link, "GoAsync"));
    }

    // The projects the inventory does not analyse (the development host, the clients) are scoped out of the chain pin, and what makes
    // that sound is that none of them names a link of the chain at all. A name in one of them is a caller nobody has reviewed.
    [Fact]
    public void NoProjectOutsideTheAnalysedCompilationsNamesAnyLinkOfTheHandoffChain()
    {
        Assert.Empty(HostedGrimoireProducerInventory.HandoffChainNamesOutsideTheAnalysedProjects());
    }

    [Theory]
    [InlineData("class C { void M(object o, object p) { o.ProcessAdmittedAsync(); p.ResumeAsync(); } }", 1)]
    [InlineData("class C { void M(LongRestSweepCoordinator c, ILongRestAdmittedSweep s) { } }", 2)]
    [InlineData("class C { /* RunOwnedAsync */ string s = \"ResumeCoreAsync\"; }", 0)]
    public void TheScopePinSeesAnIdentifierOfTheChainAndIgnoresCommentsAndStrings(string source, int hits)
    {
        Assert.Equal(hits, HostedGrimoireProducerInventory.HandoffChainNamesIn(CSharpSyntaxTree.ParseText(source)).Length);
    }
}
