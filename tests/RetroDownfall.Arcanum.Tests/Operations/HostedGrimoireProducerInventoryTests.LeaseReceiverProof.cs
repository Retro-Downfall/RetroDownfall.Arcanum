using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // Lease Receiver Proof (CK-3). A group is the Effect of a send only when it is begun on the very lease whose Work
    // the send holds. The receiver must be a must-alias of that lease: an identifier of a parameter or of a
    // never-reassigned, uncaptured local whose one reaching definition is the guarded acquisition, a copy of it, or a
    // formal every one of whose bindings is itself such an alias. A ternary, a coalesce, a loop-carried value, a
    // reassigned or captured local, an opaque return, a holder read, a delegate-received lease or a second acquisition
    // is refused with HOSTED_ADMISSION_RECEIVER_MISMATCH, and the refused group grants no Effect.
    private const string LeaseReceiverMismatch = "HOSTED_ADMISSION_RECEIVER_MISMATCH";

    private const string LeaseReceiverGroup = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup";

    private const string LeaseReceiverAcquisition = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease";

    private const string LeaseReceiverSend = "System.IO.File.Delete";

    // The paid body every row shares: begin a group on the row's receiver, hold it, then send.
    private static string LeaseReceiverBody(string receiver) => $$"""
        if (!{{receiver}}.TryBeginExternalEffectGroup(out IDisposable group)) return;

        using var held = group;

        await Task.CompletedTask;

        System.IO.File.Delete("lease-receiver-send");
        """;

    private const string LeaseReceiverFormals = """
        private static async Task First(IGrimoireWorkLease outer, CancellationToken token)
        {
            await Second(outer, token);
        }

        private static async Task Second(IGrimoireWorkLease inner, CancellationToken token)
        {
            if (!inner.TryBeginExternalEffectGroup(out IDisposable group)) return;

            using var held = group;

            await Task.CompletedTask;

            System.IO.File.Delete("lease-receiver-send");
        }
        """;

    private static (string Lease, string Body, string Members) LeaseReceiverShapeOf(string shape) => shape switch
    {
        // Positives: the group is begun on a lease the checker can prove is the acquired one.
        "designation" => ("using IGrimoireWorkLease owner = admitted!;", LeaseReceiverBody("admitted!"), ""),
        "alias" => ("using IGrimoireWorkLease lease = admitted!;", LeaseReceiverBody("lease"), ""),
        "alias-plain" => ("using var lease = admitted;", LeaseReceiverBody("lease"), ""),
        "two-formals" => ("using IGrimoireWorkLease lease = admitted!;", "await First(lease, token);", LeaseReceiverFormals),
        // Negatives: every row keeps the real Work lease alive and begins the group on something else or on a value
        // the checker cannot prove is that lease.
        "ternary-fake" => ("using IGrimoireWorkLease lease = admitted!;", LeaseReceiverBody("(token.CanBeCanceled ? lease : fake)"), ""),
        "coalesce-fake" => ("using IGrimoireWorkLease lease = admitted!;", LeaseReceiverBody("(lease ?? fake)"), ""),
        "loop-carried" => ("using IGrimoireWorkLease lease = admitted!;", """
            IGrimoireWorkLease previous = fake;

            for (int round = 0; round < 2; round++)
            {
                if (!previous.TryBeginExternalEffectGroup(out IDisposable group)) return;

                using var held = group;

                await Task.CompletedTask;

                System.IO.File.Delete("lease-receiver-send");

                previous = lease;
            }
            """, ""),
        "reassigned-local" => ("using IGrimoireWorkLease lease = admitted!;", "IGrimoireWorkLease current = lease;\n\nif (token.CanBeCanceled) current = fake;\n\n" + LeaseReceiverBody("current"), ""),
        "null-forgiven" => ("using IGrimoireWorkLease lease = admitted!;", "IGrimoireWorkLease current = null!;\n\n" + LeaseReceiverBody("current"), ""),
        "opaque-return" => ("using IGrimoireWorkLease lease = admitted!;", LeaseReceiverBody("Make()"),
            "private static IGrimoireWorkLease Make() => new FakeLease();"),
        "opaque-return-of-lease" => ("using IGrimoireWorkLease lease = admitted!;", LeaseReceiverBody("Pick(lease)"),
            "private static IGrimoireWorkLease Pick(IGrimoireWorkLease candidate) => candidate;"),
        "holder-property" => ("using IGrimoireWorkLease lease = admitted!;", "var holder = new Holder();\n\n" + LeaseReceiverBody("holder.Lease"),
            "private sealed class Holder { public IGrimoireWorkLease Lease { get; } = new FakeLease(); }"),
        "delegate-received" => ("using IGrimoireWorkLease lease = admitted!;", """
            Func<IGrimoireWorkLease, Task> use = async received =>
            {
                if (!received.TryBeginExternalEffectGroup(out IDisposable group)) return;

                using var held = group;

                await Task.CompletedTask;

                System.IO.File.Delete("lease-receiver-send");
            };

            await use(fake);
            """, ""),
        "delegate-passed" => ("using IGrimoireWorkLease lease = admitted!;", """
            Func<IGrimoireWorkLease, Task> use = async received =>
            {
                if (!received.TryBeginExternalEffectGroup(out IDisposable group)) return;

                using var held = group;

                await Task.CompletedTask;

                System.IO.File.Delete("lease-receiver-send");
            };

            await use(lease);
            """, ""),
        "delegate-supplied" => ("using IGrimoireWorkLease lease = admitted!;", "Func<IGrimoireWorkLease> supply = () => lease;\n\n" + LeaseReceiverBody("supply()"), ""),
        "second-acquisition" => ("using IGrimoireWorkLease lease = admitted!;", """
            if (!gate.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? second)) return;

            using IGrimoireWorkLease other = second!;

            """ + LeaseReceiverBody("other"), ""),
        "inexact-formal" => ("using IGrimoireWorkLease lease = admitted!;", "await First(token.CanBeCanceled ? lease : fake, token);", LeaseReceiverFormals),
        "reassigned-formal" => ("using IGrimoireWorkLease lease = admitted!;", "await First(lease, token);",
            LeaseReceiverFormals.Replace("if (!inner.TryBeginExternalEffectGroup", "inner = new FakeLease();\n\n    if (!inner.TryBeginExternalEffectGroup", StringComparison.Ordinal)),
        "lambda-written-local" => ("using IGrimoireWorkLease lease = admitted!;", "IGrimoireWorkLease current = lease;\n\nAction swap = () => current = fake;\n\nswap();\n\n" + LeaseReceiverBody("current"), ""),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static string LeaseReceiverSource(string shape)
    {
        (string lease, string body, string members) = LeaseReceiverShapeOf(shape);

        return $$"""
            #nullable enable
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using RetroDownfall.Arcanum.Infrastructure.Data;

            public class Worker(IGrimoireConnectionAdmissionGate gate) : IHostedService
            {
                public async Task StartAsync(CancellationToken token)
                {
                    if (!gate.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? admitted)) return;

                    {{lease}}

                    IGrimoireWorkLease fake = new FakeLease();

                    {{body}}
                }

                public Task StopAsync(CancellationToken token) => Task.CompletedTask;

                {{members}}
            }

            public sealed class FakeLease : IGrimoireWorkLease
            {
                public bool TryBeginExternalEffectGroup(out IDisposable group)
                {
                    group = new FakeGroup();

                    return true;
                }

                public void Dispose() { }

                public ValueTask DisposeAsync() => default;
            }

            public sealed class FakeGroup : IDisposable
            {
                public void Dispose() { }
            }

            public static class Composition
            {
                public static void Configure(IServiceCollection services) { services.AddHostedService<Worker>(); }
            }
            """ + AdmissionTypes;
    }

    private sealed record LeaseReceiverRun(SyntaxNode GroupCall, HostedProducerDiscovery<HostedProducerSite> Result)
    {
        internal string Coordinate => GroupCall.SyntaxTree.FilePath + ":" + GroupCall.SpanStart;

        // The acquisitions in document order: the first is the one the Work belongs to.
        internal HostedProducerSite[] Acquisitions => [.. Result.Items.Where(static site => site.Callee == LeaseReceiverAcquisition)
            .OrderBy(static site => int.Parse(site.OperationId[(site.OperationId.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture))];

        internal HostedProducerSite Acquisition => Acquisitions[0];

        internal HostedProducerSite[] Groups => [.. Result.Items.Where(static site => site.Callee == LeaseReceiverGroup)];

        internal HostedProducerSite Send => Assert.Single(Result.Items, static site => site.Callee == LeaseReceiverSend);
    }

    private LeaseReceiverRun RunLeaseReceiver(string source, string label, params HostedProducerOperationEntry[] roots)
    {
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        InvocationExpressionSyntax group = Assert.Single(compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TryBeginExternalEffectGroup" });

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", roots)], []);

        foreach (HostedProducerSite site in result.Items)
        {
            output.WriteLine($"LEASE_RECEIVER_SITE {label} {site.EnclosingType}.{site.Member} {site.Callee} work={(site.WorkFrontierCapsuleId is null ? "null" : "set")} effect={(site.EffectFrontierCapsuleId is null ? "null" : "set")}");
        }

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"LEASE_RECEIVER_DIAGNOSTIC {label} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        return new(group, result);
    }

    [Theory]
    [InlineData("designation")]
    [InlineData("alias")]
    [InlineData("alias-plain")]
    [InlineData("two-formals")]
    [InlineData("delegate-passed")]
    public void LeaseReceiverProofGrantsTheGroupsEffectToAMustAliasOfTheAcquiredLease(string shape)
    {
        LeaseReceiverRun run = RunLeaseReceiver(LeaseReceiverSource(shape), shape, OrdinaryRoot());

        Assert.Empty(run.Result.Diagnostics);

        HostedProducerSite group = Assert.Single(run.Groups);

        // The group's Effect is itself, the Work is the acquisition, and the send holds both exactly.
        Assert.Equal(group.CapsuleId, group.EffectFrontierCapsuleId);

        Assert.Equal(run.Acquisition.CapsuleId, run.Send.WorkFrontierCapsuleId);

        Assert.Equal(group.CapsuleId, run.Send.EffectFrontierCapsuleId);
    }

    [Theory]
    [InlineData("ternary-fake")]
    [InlineData("coalesce-fake")]
    [InlineData("loop-carried")]
    [InlineData("reassigned-local")]
    [InlineData("null-forgiven")]
    [InlineData("opaque-return")]
    [InlineData("opaque-return-of-lease")]
    [InlineData("holder-property")]
    [InlineData("delegate-received")]
    [InlineData("delegate-supplied")]
    [InlineData("second-acquisition")]
    [InlineData("inexact-formal")]
    [InlineData("reassigned-formal")]
    [InlineData("lambda-written-local")]
    public void LeaseReceiverProofRefusesAGroupWhoseReceiverIsNotAMustAliasOfTheAcquiredLease(string shape)
    {
        LeaseReceiverRun run = RunLeaseReceiver(LeaseReceiverSource(shape), shape, OrdinaryRoot());

        // The refusal names the group's own coordinate, once, and nothing else about the group is believed.
        HostedProducerInventoryDiagnostic mismatch = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == LeaseReceiverMismatch);

        Assert.EndsWith("/call@" + run.Coordinate, mismatch.Identity, StringComparison.Ordinal);

        HostedProducerSite group = Assert.Single(run.Groups);

        Assert.Null(group.EffectFrontierCapsuleId);

        // The send keeps the real Work but loses its Effect: the unproven group is not an Effect of anything. Handing the
        // lease itself to a callee that returns it is an escape the checker already ends the Work at.
        bool workEnded = shape == "opaque-return-of-lease";

        Assert.Equal(workEnded ? null : run.Acquisition.CapsuleId, run.Send.WorkFrontierCapsuleId);

        Assert.Null(run.Send.EffectFrontierCapsuleId);

        Assert.Contains(run.Result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == LeaseReceiverSend);

        string[] codes = [.. run.Result.Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Assert.Equal(workEnded
            ? [LeaseReceiverMismatch, "HOSTED_SITE_EFFECT_FRONTIER_MISSING", "HOSTED_SITE_WORK_FRONTIER_MISSING"]
            : [LeaseReceiverMismatch, "HOSTED_SITE_EFFECT_FRONTIER_MISSING"], codes);
    }

    // The ordinary external-effect recovery shell is the production text of SettleAdmittedAsync inside the recovery
    // matrix, with four more external-effect tuples. The shell begins its group on the lease it acquires inline, and the
    // recovery effect every tuple's handler inherits is that group. A group on a second acquisition is not the shell's own.
    private static readonly string[] LeaseReceiverRecoveryTuples = ["BlobEncryption", "Backup", "DataRetention", "A2A"];

    private static string LeaseReceiverRecoverySource(string shape)
    {
        string source = SettleAdmittedGrammarFixture("inline-acquisition");

        string Replace(string original, string replacement)
        {
            Assert.Equal(1, source.Split(original, StringSplitOptions.None).Length - 1);

            return source = source.Replace(original, replacement, StringComparison.Ordinal);
        }

        string registrations = string.Concat(LeaseReceiverRecoveryTuples.Select(static tuple =>
            $"services.AddScoped<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler, RetroDownfall.Arcanum.Infrastructure.Operations.{tuple}RecoveryHandler>(); "));

        const string external = "services.AddScoped<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler, RetroDownfall.Arcanum.Infrastructure.Operations.ExternalRecoveryHandler>();";

        _ = Replace(external, external + " " + registrations.TrimEnd());

        _ = Replace("public const string External = \"external\";", "public const string External = \"external\"; "
            + string.Concat(LeaseReceiverRecoveryTuples.Select(static tuple => $"public const string {tuple} = \"{tuple}\"; ")).TrimEnd());

        _ = Replace("new(LongRunningOperationKinds.External, 0, 0),", "new(LongRunningOperationKinds.External, 0, 0), "
            + string.Concat(LeaseReceiverRecoveryTuples.Select(static tuple => $"new(LongRunningOperationKinds.{tuple}, 0, 0), ")).TrimEnd());

        _ = Replace("(LongRunningOperationKinds.External, 0) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect,", "(LongRunningOperationKinds.External, 0) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect, "
            + string.Concat(LeaseReceiverRecoveryTuples.Select(static tuple => $"(LongRunningOperationKinds.{tuple}, 0) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect, ")).TrimEnd());

        _ = Replace("internal sealed class OwnerRecoveryHandler : ILongRunningOperationRecoveryHandler", string.Concat(LeaseReceiverRecoveryTuples.Select(static tuple => $$"""
            internal sealed class {{tuple}}RecoveryHandler : ILongRunningOperationRecoveryHandler
            {
                public string Kind => LongRunningOperationKinds.{{tuple}};

                public int SupportedCheckpointVersion => 0;

                public Task RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
                {
                    System.IO.File.Delete("{{tuple}}");

                    return Task.CompletedTask;
                }
            }

            """)) + "internal sealed class OwnerRecoveryHandler : ILongRunningOperationRecoveryHandler");

        if (shape == "second-acquisition")
        {
            // The second acquisition sits inside the protected block, so the shell's own lease keeps its exact ownership and
            // its Work; the group is begun on the other lease, which the shell never disposes and no Work belongs to.
            _ = Replace("if (decision.Kind is LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect",
                "if (!admissionGate.TryAcquireWorkLease(GrimoireWorkKind.LongRunningOperationRecovery, out IGrimoireWorkLease? admittedSecond)) { continue; } "
                + "IGrimoireWorkLease second = admittedSecond!; "
                + "if (decision.Kind is LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect");

            _ = Replace("!lease.TryBeginExternalEffectGroup(out group)", "!second.TryBeginExternalEffectGroup(out group)");
        }

        return source;
    }

    [Theory]
    [InlineData("own-acquisition")]
    [InlineData("second-acquisition")]
    public void LeaseReceiverProofGrantsTheRecoveryEffectOnlyToTheShellsOwnAcquisition(string shape)
    {
        CSharpCompilation compilation = Compile(LeaseReceiverRecoverySource(shape));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        // The background pass is ordinary hosted work whose declared kind is the recovery kind, exactly as the catalog says.
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation],
            new(["Worker"], []),
            [new("Worker", [OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRunningOperationRecovery }])],
            [new("RetroDownfall.Arcanum.Infrastructure.Operations.OwnerRecoveryRoot.Run", "src/Fixture.cs",
                "RetroDownfall.Arcanum.Infrastructure.Operations.OwnerRecoveryRoot", "Run", HostedProducerAuthorityKind.OwnerBoundRecovery, "exact owner fixture", [])]);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"LEASE_RECEIVER_RECOVERY_DIAGNOSTIC {shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        HostedProducerSite group = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Callee == LeaseReceiverGroup);

        HostedProducerSite[] sends = [.. LeaseReceiverRecoveryTuples.Select(tuple => Assert.Single(result.Items, site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations." + tuple + "RecoveryHandler" && site.Callee == LeaseReceiverSend))];

        if (shape == "own-acquisition")
        {
            // The fixture's gate keeps a default interface method the checker cannot bind; that is the one diagnostic the
            // shell fixture has always carried, and nothing else is reported.
            Assert.All(result.Diagnostics, static diagnostic => Assert.True(diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
                && diagnostic.Detail.EndsWith(".WaitForOpenGenerationAfterRefusalAsync", StringComparison.Ordinal), diagnostic.Code + " " + diagnostic.Detail));

            Assert.All(sends, send => Assert.Equal(group.CapsuleId, send.EffectFrontierCapsuleId));

            return;
        }

        // The group is on a lease the shell acquired a second time, so it is nobody's recovery effect: each tuple's
        // handler is refused for want of an effect frontier, and the refusal names the group.
        HostedProducerInventoryDiagnostic mismatch = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == LeaseReceiverMismatch);

        Assert.Null(group.EffectFrontierCapsuleId);

        Assert.All(sends, static send => Assert.Null(send.EffectFrontierCapsuleId));

        // Every external-effect tuple of the matrix: the four above and the fixture's own.
        Assert.Equal(LeaseReceiverRecoveryTuples.Length + 1, result.Diagnostics.Count(static diagnostic => diagnostic.Code == "HOSTED_RECOVERY_EFFECT_FRONTIER_UNPROVEN"));

        Assert.StartsWith("Worker.StartAsync", mismatch.Identity, StringComparison.Ordinal);
    }
}
