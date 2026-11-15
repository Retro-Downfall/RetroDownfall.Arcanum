using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("no-monitor")]
    [InlineData("read-only-monitor")]
    [InlineData("disposing-monitor")]
    [InlineData("wrong-task-join")]
    [InlineData("unjoined-monitor")]
    [InlineData("scalar-generation-guard")]
    [InlineData("literal-generation-guard")]
    [InlineData("lease-storing-guard")]
    [InlineData("unreviewed-property-guard")]
    [InlineData("writable-generation-guard")]
    public void NestedHeartbeatKeepsPhysicalWorkUntilAnActualInvalidation(string shape)
    {
        string publication = shape switch
        {
            "no-monitor" => "Task heartbeat = Task.CompletedTask;",
            "unjoined-monitor" => "_ = MonitorAsync(work, attempt, stopHeartbeat.Token); Task heartbeat = Task.CompletedTask;",
            _ => "Task heartbeat = MonitorAsync(work, attempt, stopHeartbeat.Token);",
        };

        string join = shape == "wrong-task-join" ? "Task.CompletedTask" : "heartbeat";

        string monitorLifetime = shape == "disposing-monitor"
            ? "await work.DisposeAsync().ConfigureAwait(false);"
            : "_ = work.Generation; _ = work.MaintenanceRevocationPending;";

        bool hasGuard = shape.EndsWith("-guard", StringComparison.Ordinal);

        string guardArgument = shape switch
        {
            "scalar-generation-guard" => "work.Generation",
            "writable-generation-guard" => "work.Generation",
            "unreviewed-property-guard" => "work.OtherGeneration",
            "lease-storing-guard" => "work",
            _ => "1L",
        };

        string guardOwner = hasGuard ? "using HeartbeatGuard guard = new(" + guardArgument + ");" : string.Empty;

        string guardParameter = shape == "lease-storing-guard" ? "IGrimoireWorkLease value" : "long value";

        string guardCurrent = shape == "lease-storing-guard" ? "value.Generation >= 0" : "value >= 0";

        string guardCleanup = shape == "lease-storing-guard" ? "_ = value.DisposeAsync();" : string.Empty;

        string observe = hasGuard ? "ObserveGuardAsync(guard)" : "ObserveAsync()";

        string generationAccessors = shape == "writable-generation-guard" ? "get; set;" : "get;";

        string source = $$"""
            #nullable enable
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Hosting;
            using RetroDownfall.Arcanum.Infrastructure.Data;

            public sealed class Worker : IHostedService
            {
                public async Task StartAsync(CancellationToken token)
                {
                    IGrimoireConnectionAdmissionGate gate = null!;

                    if (!gate.TryAcquireWorkLease(GrimoireWorkKind.LongRunningOperationRecovery, out IGrimoireWorkLease? acquired) || acquired is null) return;

                    await using IGrimoireWorkLease root = acquired;

                    if (!root.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group) || group is null) return;

                    await using IGrimoireExternalEffectGroup held = group;

                    await NestedAsync(gate, token).ConfigureAwait(false);
                }

                private static async Task NestedAsync(IGrimoireConnectionAdmissionGate gate, CancellationToken token)
                {
                    if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null) return;

                    await using IGrimoireWorkLease work = acquired;

                    if (!work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group) || group is null) return;

                    await using IGrimoireExternalEffectGroup heldGroup = group;

                    {{guardOwner}}

                    using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(token, work.MaintenanceRevocation);

                    using CancellationTokenSource stopHeartbeat = new();

                    {{publication}}

                    try
                    {
                        try
                        {
                            await {{observe}}.ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                        }
                        finally
                        {
                            await stopHeartbeat.CancelAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        await {{join}}.ConfigureAwait(false);
                    }
                }

                private static async Task MonitorAsync(IGrimoireWorkLease work, CancellationTokenSource attempt, CancellationToken stop)
                {
                    try
                    {
                        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

                        while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
                        {
                            {{monitorLifetime}}

                            System.IO.File.Delete("heartbeat-effect");

                            await attempt.CancelAsync().ConfigureAwait(false);

                            return;
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                    }
                    catch (Exception)
                    {
                        await attempt.CancelAsync().ConfigureAwait(false);
                    }
                }

                private static async Task ObserveAsync()
                {
                    await Task.Yield();

                    _ = System.IO.File.Exists("nested-heartbeat-read");

                    System.IO.File.Delete("nested-heartbeat-send");
                }

                private static async Task ObserveGuardAsync(HeartbeatGuard guard)
                {
                    if (!guard.IsCurrent()) return;

                    await Task.Yield();

                    _ = System.IO.File.Exists("nested-guard-read");

                    System.IO.File.Delete("nested-guard-send");
                }

                public Task StopAsync(CancellationToken token) => Task.CompletedTask;
            }

            public sealed class HeartbeatGuard({{guardParameter}}) : IDisposable
            {
                internal bool IsCurrent() => {{guardCurrent}};

                public void Dispose() { {{guardCleanup}} }
            }

            namespace RetroDownfall.Arcanum.Infrastructure.Data
            {
                public enum GrimoireWorkKind : byte { LongRunningOperationRecovery = 14, GuildStorage = 20 }

                public interface IGrimoireConnectionAdmissionGate
                {
                    bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease? lease);
                }

                public interface IGrimoireWorkLease : IAsyncDisposable
                {
                    long Generation { {{generationAccessors}} }

                    long OtherGeneration { get; }

                    CancellationToken MaintenanceRevocation { get; }

                    bool MaintenanceRevocationPending { get; }

                    bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup);
                }

                public interface IGrimoireExternalEffectGroup : IAsyncDisposable { }
            }
            """;

        CSharpCompilation compilation = Compile(source).WithAssemblyName("RetroDownfall.Arcanum.Infrastructure");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxNode syntax = compilation.SyntaxTrees.Single().GetRoot();

        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());

        InvocationExpressionSyntax[] acquisitions = [.. syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static call => call.Expression.ToString() == "gate.TryAcquireWorkLease")];

        Assert.Equal(2, acquisitions.Length);

        Assert.All(acquisitions, call =>
        {
            IInvocationOperation selected = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(call));

            Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate", selected.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal(RefKind.Out, selected.TargetMethod.Parameters[1].RefKind);

            Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease?", selected.TargetMethod.Parameters[1].Type.ToDisplayString());
        });

        MethodDeclarationSyntax nested = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(static method => method.Identifier.ValueText == "NestedAsync");

        TryStatementSyntax lifetime = Assert.Single(nested.Body!.Statements.OfType<TryStatementSyntax>());

        TryStatementSyntax cancellation = Assert.IsType<TryStatementSyntax>(Assert.Single(lifetime.Block.Statements));

        Assert.Equal("await stopHeartbeat.CancelAsync().ConfigureAwait(false);", Assert.Single(cancellation.Finally!.Block.Statements).ToString());

        Assert.Equal("await " + join + ".ConfigureAwait(false);", Assert.Single(lifetime.Finally!.Block.Statements).ToString());

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []),
            [new("Worker", [OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRunningOperationRecovery }])], []);

        HostedProducerSite outer = Assert.Single(result.Items, static site => site.AdmittedWorkKind == GrimoireWorkKind.LongRunningOperationRecovery);

        HostedProducerSite inner = Assert.Single(result.Items, static site => site.AdmittedWorkKind == GrimoireWorkKind.GuildStorage);

        HostedProducerSite group = Assert.Single(result.Items, static site => site.Member == "NestedAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        string observationMember = hasGuard ? "ObserveGuardAsync" : "ObserveAsync";

        HostedProducerSite read = Assert.Single(result.Items, site => site.Member == observationMember && site.Callee == "System.IO.File.Exists");

        HostedProducerSite send = Assert.Single(result.Items, site => site.Member == observationMember && site.Callee == "System.IO.File.Delete");

        output.WriteLine(HostedGrimoireProducerInventory.RenderDiagnosticReport(result.Diagnostics));

        if (shape is "disposing-monitor" or "lease-storing-guard" or "unreviewed-property-guard" or "writable-generation-guard")
        {
            Assert.False(send.WorkFrontierCapsuleId == inner.CapsuleId && send.EffectFrontierCapsuleId == group.CapsuleId);

            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                or "HOSTED_SITE_EFFECT_FRONTIER_MISSING" or "HOSTED_ADMISSION_EFFECT_LEASE_MISMATCH");
        }
        else if (shape is "no-monitor" or "read-only-monitor" or "scalar-generation-guard" or "literal-generation-guard")
        {
            foreach (HostedProducerSite site in new[] { read, send })
            {
                Assert.Equal(inner.CapsuleId, site.WorkFrontierCapsuleId);

                Assert.Equal(outer.CapsuleId, site.RegisteredRootWorkFrontierCapsuleId);

                Assert.Equal(group.CapsuleId, site.EffectFrontierCapsuleId);
            }
        }

        if (shape is "wrong-task-join" or "unjoined-monitor")
        {
            HostedProducerSite heartbeat = Assert.Single(result.Items, static site => site.Member == "MonitorAsync" && site.Callee == "System.IO.File.Delete");

            Assert.False(heartbeat.WorkFrontierCapsuleId is not null && heartbeat.EffectFrontierCapsuleId is not null);

            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Detail.StartsWith("Worker.MonitorAsync;", StringComparison.Ordinal));
        }
        else if (shape is "no-monitor" or "read-only-monitor" or "scalar-generation-guard" or "literal-generation-guard")
        {
            Assert.Empty(result.Diagnostics);
        }
    }
}
