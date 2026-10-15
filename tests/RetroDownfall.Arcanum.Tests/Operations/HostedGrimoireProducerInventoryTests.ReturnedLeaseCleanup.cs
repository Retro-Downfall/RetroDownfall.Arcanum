using Microsoft.CodeAnalysis;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // Returned-Lease Cleanup. The hosted worker takes its lease from a helper that returns it, and `await using` disposes it, so the
    // `DisposeAsync` that runs is the one of whatever lease the gate hands out. The checker follows that cleanup only when the gate
    // the worker acquires from is a closed receiver: a field initialised once with a literal `new`, or the same literal behind a
    // reference conversion or a forwarding gate that does nothing but forward to one, or an interface default body of a gate it can
    // name. A gate it cannot name (a mutable static, a factory whose default body it cannot select, or the result of an authored
    // conversion) leaves the lease's disposal unresolved, and the disposal effect is then refused with
    // HOSTED_DISPOSAL_TARGET_UNRESOLVED rather than assumed away. The rows are the same worker with the gate expression varied.
    private const string ReturnedLeaseCleanupFile = "returned-lease-cleanup";

    private static string ReturnedLeaseCleanupFixture(string shape)
    {
        string[] closed = ["readonly-gate", "forwarded-readonly-gate", "literal-reference-conversion", "default-body-gate"];

        string[] open = ["unknown-gate", "literal-authored-conversion", "default-body-unknown-current", "default-body-unknown-factory", "unknown-default-factory"];

        string[] shapes = [.. closed, .. open];

        Assert.Contains(shape, shapes);

        string gateExpression = shape switch
        {
            "forwarded-readonly-gate" => "new ForwardingGate()",
            "literal-reference-conversion" => "(IGrimoireConnectionAdmissionGate)new GrimoireConnectionAdmissionGate()",
            "literal-authored-conversion" => "(AlternateGate)new GrimoireConnectionAdmissionGate()",
            "unknown-gate" => "UnprovenGate.Current",
            "default-body-unknown-current" => "UnprovenGate.Current",
            "default-body-unknown-factory" => "UnprovenGate.Factory.Create()",
            "unknown-default-factory" => "UnprovenGate.Factories.Current.CreateGate()",
            _ => "new GrimoireConnectionAdmissionGate()",
        };

        bool defaultBody = shape.StartsWith("default-body", StringComparison.Ordinal);

        string gateContract = defaultBody
            ? """
                public interface IGrimoireConnectionAdmissionGate
                {
                    bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease)
                    {
                        lease = new CleanupLease();

                        return true;
                    }
                }
                """
            : """
                public interface IGrimoireConnectionAdmissionGate
                {
                    bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease);
                }
                """;

        string gateBody = defaultBody
            ? ""
            : """
                public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease)
                    {
                        lease = new CleanupLease();

                        return true;
                    }
                """;

        if (shape == "literal-authored-conversion")
        {
            gateBody += """

                    public static explicit operator AlternateGate(GrimoireConnectionAdmissionGate value) => new AlternateGate();
                """;
        }

        string extras = shape switch
        {
            "forwarded-readonly-gate" => """
                internal sealed class ForwardingGate : IGrimoireConnectionAdmissionGate
                {
                    private readonly IGrimoireConnectionAdmissionGate inner = new GrimoireConnectionAdmissionGate();

                    public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease)
                        => inner.TryAcquireWorkLease(kind, out lease);
                }
                """,
            "literal-authored-conversion" => """
                internal sealed class AlternateGate : IGrimoireConnectionAdmissionGate
                {
                    public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease)
                        => throw new InvalidOperationException("The selected authored conversion returned another implementation.");
                }
                """,
            "unknown-gate" or "default-body-unknown-current" or "default-body-unknown-factory" or "unknown-default-factory" => """
                internal interface IGateFactory
                {
                    IGrimoireConnectionAdmissionGate Create() => new GrimoireConnectionAdmissionGate();

                    IGrimoireConnectionAdmissionGate CreateGate() => new GrimoireConnectionAdmissionGate();
                }

                internal sealed class DefaultGateFactory : IGateFactory { }

                internal static class UnprovenGate
                {
                    internal static IGrimoireConnectionAdmissionGate Current = new GrimoireConnectionAdmissionGate();

                    internal static IGateFactory Factory = new DefaultGateFactory();

                    internal static class Factories
                    {
                        internal static IGateFactory Current = new DefaultGateFactory();
                    }
                }
                """,
            _ => "",
        };

        return $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using RetroDownfall.Arcanum.Infrastructure.Data;

            public class Worker : IHostedService
            {
                private readonly IGrimoireConnectionAdmissionGate gate = {{gateExpression}};

                public async Task StartAsync(CancellationToken token)
                {
                    await using IGrimoireWorkLease lease = Acquire(token);

                    await Task.CompletedTask;
                }

                public Task StopAsync(CancellationToken token) => Task.CompletedTask;

                private IGrimoireWorkLease Acquire(CancellationToken ct)
                {
                    if (!gate.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease lease))
                    {
                        ct.ThrowIfCancellationRequested();

                        throw new OperationCanceledException(ct);
                    }

                    return lease;
                }
            }

            public static class Composition
            {
                public static void Configure(IServiceCollection services)
                {
                    services.AddHostedService<Worker>();
                }
            }

            namespace RetroDownfall.Arcanum.Api
            {
                public static class ApiBootstrapper
                {
                    public static IServiceCollection AddArcanumApiServices(this IServiceCollection services)
                    {
                        global::Composition.Configure(services);

                        return services;
                    }
                }
            }

            namespace RetroDownfall.Arcanum.Infrastructure.Data
            {
                public enum GrimoireWorkKind { WorkspaceIndexing = 4, LongRestSweep = 17 }

                public interface IGrimoireWorkLease : IDisposable, IAsyncDisposable
                {
                    bool TryBeginExternalEffectGroup(out IDisposable group);
                }

                {{gateContract}}

                // The one gate the fixture's source declares is the reviewed gate (see the Single Gate Census).
                internal sealed class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate
                {
                    {{gateBody}}
                }

                internal sealed class CleanupLease : IGrimoireWorkLease
                {
                    public bool TryBeginExternalEffectGroup(out IDisposable group)
                    {
                        group = new CleanupGroup();

                        return true;
                    }

                    public void Dispose() { }

                    public ValueTask DisposeAsync()
                    {
                        System.IO.File.Delete("{{ReturnedLeaseCleanupFile}}");

                        return ValueTask.CompletedTask;
                    }

                    private sealed class CleanupGroup : IDisposable
                    {
                        public void Dispose() { }
                    }
                }

                {{extras}}
            }
            """;
    }

    [Theory]
    [InlineData("readonly-gate", false)]
    [InlineData("forwarded-readonly-gate", false)]
    [InlineData("literal-reference-conversion", false)]
    [InlineData("default-body-gate", false)]
    [InlineData("unknown-gate", true)]
    [InlineData("literal-authored-conversion", true)]
    [InlineData("default-body-unknown-current", true)]
    [InlineData("default-body-unknown-factory", true)]
    [InlineData("unknown-default-factory", true)]
    public void ReturnedLeaseCleanupFollowsTheGateTheWorkerAcquiresFromAndRefusesOneItCannotName(string shape, bool unresolvedGate)
    {
        Microsoft.CodeAnalysis.CSharp.CSharpCompilation compilation = Compile(ReturnedLeaseCleanupFixture(shape));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<string> hosted = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]);

        Assert.Empty(hosted.Diagnostics);

        HostedProducerOperationEntry root = OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], hosted, [new("Worker", [root])], []);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"RETURNED_LEASE_CLEANUP_DIAGNOSTIC {shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        bool unresolved = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.DisposeAsync", StringComparison.Ordinal));

        HostedProducerSite[] cleanup = [.. result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Data.CleanupLease"
            && site.Member == "DisposeAsync" && site.Callee == "System.IO.File.Delete"
            && site.Kind == HostedProducerSiteKind.FileSystemEffect
            && site.AuthorityOperationId == "Worker.StartAsync")];

        Assert.Equal(unresolvedGate, unresolved);

        if (unresolvedGate) Assert.Empty(cleanup);
        else Assert.NotEmpty(cleanup);
    }
}
