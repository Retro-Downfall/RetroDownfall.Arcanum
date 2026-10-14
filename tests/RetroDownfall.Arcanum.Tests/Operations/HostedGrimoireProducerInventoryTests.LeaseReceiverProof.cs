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

    // The group is begun inside a lambda on a formal of the method that declares the lambda. `mutation` runs in that method.
    private static string LeaseReceiverCapturingFormal(string mutation) => $$"""
        private static async Task First(IGrimoireWorkLease outer, CancellationToken token)
        {
            {{mutation}}

            Func<Task> inside = async () =>
            {
                if (!outer.TryBeginExternalEffectGroup(out IDisposable group)) return;

                using var held = group;

                await Task.CompletedTask;

                System.IO.File.Delete("lease-receiver-send");
            };

            await inside();
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
        "second-acquisition-unused" => ("using IGrimoireWorkLease lease = admitted!;", """
            if (!gate.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? second)) return;

            using IGrimoireWorkLease other = second!;

            """ + LeaseReceiverBody("lease"), ""),
        "designation-reassigned" => ("using IGrimoireWorkLease lease = admitted!;", "admitted = fake;\n\n" + LeaseReceiverBody("admitted!"), ""),
        "unguarded-acquisition" => ("using IGrimoireWorkLease lease = admitted!;", """
            gate.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? loose);

            """ + LeaseReceiverBody("loose!"), ""),
        "captured-designation" => ("using IGrimoireWorkLease lease = admitted!;", "Func<bool> peek = () => admitted is not null;\n\n" + LeaseReceiverBody("admitted!"), ""),
        "captured-copy" => ("using IGrimoireWorkLease lease = admitted!;", "Func<IGrimoireWorkLease> read = () => lease;\n\n" + LeaseReceiverBody("lease"), ""),
        "captured-formal" => ("using IGrimoireWorkLease lease = admitted!;", "await First(lease, token);", LeaseReceiverCapturingFormal("")),
        "captured-formal-written" => ("using IGrimoireWorkLease lease = admitted!;", "await First(lease, token);", LeaseReceiverCapturingFormal("outer = new FakeLease();")),
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

        return LeaseReceiverSource(lease, body, members);
    }

    private static string LeaseReceiverSource(string lease, string body, string members)
    {
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
    [InlineData("second-acquisition-unused")]
    [InlineData("captured-formal")]
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
    [InlineData("designation-reassigned")]
    [InlineData("unguarded-acquisition")]
    [InlineData("captured-designation")]
    [InlineData("captured-copy")]
    [InlineData("captured-formal-written")]
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

        // A group whose receiver is a sound lease that is merely not the one holding the Work is still found retained by the
        // send's own scan; the send then holds the group's identity but no capsule, and the mismatch is the refusal.
        bool secondLease = shape == "second-acquisition";

        Assert.Equal(!secondLease, run.Result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == LeaseReceiverSend));

        string[] codes = [.. run.Result.Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // An acquisition outside any guard is itself an unresolved call, a refusal that predates the receiver proof.
        Assert.Equal(shape switch
        {
            "opaque-return-of-lease" => [LeaseReceiverMismatch, "HOSTED_SITE_EFFECT_FRONTIER_MISSING", "HOSTED_SITE_WORK_FRONTIER_MISSING"],
            "unguarded-acquisition" => [LeaseReceiverMismatch, "HOSTED_CALL_TARGET_UNRESOLVED", "HOSTED_SITE_EFFECT_FRONTIER_MISSING"],
            "second-acquisition" => [LeaseReceiverMismatch],
            _ => [LeaseReceiverMismatch, "HOSTED_SITE_EFFECT_FRONTIER_MISSING"],
        }, codes);
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

    // The simplest shell, split in two: the worker acquires the lease and hands it to a helper that holds the recovery guard.
    // Every other proof holds (the formal is bound exactly and the worker's Work is that very lease), so only the rule that the
    // shell's group is begun on the shell's own acquisition remains.
    [Theory]
    [InlineData("acquired-in-the-shell")]
    [InlineData("handed-to-the-shell")]
    public void LeaseReceiverProofGrantsTheRecoveryEffectOnlyToALeaseTheShellAcquiredItself(string shape)
    {
        string source = RecoveryMatrixFixture();

        const string operation = " var operation = new RetroDownfall.Arcanum.Core.Operations.LongRunningOperation(RetroDownfall.Arcanum.Core.Operations.LongRunningOperationKinds.External, 0);";

        Assert.Equal(1, source.Split(operation, StringSplitOptions.None).Length - 1);

        if (shape == "handed-to-the-shell")
        {
            source = source.Replace(operation, " await Shell(lease); } private async Task Shell(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease) {" + operation, StringComparison.Ordinal);
        }

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        InvocationExpressionSyntax group = Assert.Single(compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TryBeginExternalEffectGroup" });

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture([compilation]);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"LEASE_RECEIVER_HANDED_DIAGNOSTIC {shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        HostedProducerSite external = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.ExternalRecoveryHandler" && site.Callee == LeaseReceiverSend);

        if (shape == "acquired-in-the-shell")
        {
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is LeaseReceiverMismatch or "HOSTED_RECOVERY_EFFECT_FRONTIER_UNPROVEN");

            Assert.NotNull(external.EffectFrontierCapsuleId);

            return;
        }

        HostedProducerInventoryDiagnostic mismatch = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == LeaseReceiverMismatch);

        Assert.EndsWith("/call@" + group.SyntaxTree.FilePath + ":" + group.SpanStart, mismatch.Identity, StringComparison.Ordinal);

        Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_RECOVERY_EFFECT_FRONTIER_UNPROVEN");

        Assert.Null(external.EffectFrontierCapsuleId);
    }

    // An optional lease: one core is reached once with the first argument and once with the worker's lease, and begins its group
    // under `workLease is not null` (or, in the unguarded row, unconditionally). A formal bound only to the null constant makes
    // that guard false, so the traversal that passes the null literal (the retention recovery path, whose shell already holds the
    // group) never reaches the dead call: it is neither an Effect nor a refusal. A lease that is not the null constant, and a
    // call the null cannot skip, keep the full receiver proof.
    private static string LeaseReceiverOptionalSource(bool guarded, string first)
    {
        string begin = """
            if (!workLease.TryBeginExternalEffectGroup(out System.IDisposable group)) return;

            using var held = group;

            await Task.CompletedTask;

            System.IO.File.Delete("lease-receiver-send");
            """;

        string core = guarded ? "if (workLease is not null)\n{\n" + begin + "\n}" : begin;

        return LeaseReceiverSource("using IGrimoireWorkLease lease = admitted!;", $"await Optional.Prune.RecoverPruneCoreAsync({first});\n\nawait Optional.Prune.RecoverPruneCoreAsync(lease);", "")
            + LeaseReceiverOptionalCore(core);
    }

    // The two-hop optional-lease core the retention recovery path has: the entry hands its formal on, the core tests it.
    private static string LeaseReceiverOptionalCore(string core) => $$"""

        namespace Optional
        {
            internal static class Prune
            {
                internal static async Task RecoverPruneCoreAsync(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease? workLease)
                {
                    await ApplyUnifiedPruneCoreAsync(workLease);
                }

                internal static async Task ApplyUnifiedPruneCoreAsync(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease? workLease)
                {
                    {{core}}
                }
            }
        }
        """;

    [Theory]
    [InlineData(true, "null")]
    [InlineData(false, "null")]
    [InlineData(true, "fake")]
    public void LeaseReceiverProofPrunesADeadGroupOnlyForAGuardedNullLease(bool guarded, string first)
    {
        string source = LeaseReceiverOptionalSource(guarded, first);

        LeaseReceiverRun run = RunLeaseReceiver(source, (guarded ? "guarded-" : "unguarded-") + first, OrdinaryRoot());

        if (guarded && first == "null")
        {
            Assert.Empty(run.Result.Diagnostics);

            // The null traversal never reaches the call: the group site and the send exist once, for the worker's lease.
            HostedProducerSite group = Assert.Single(run.Groups);

            Assert.Equal(group.CapsuleId, group.EffectFrontierCapsuleId);

            Assert.Equal(group.CapsuleId, run.Send.EffectFrontierCapsuleId);

            return;
        }

        HostedProducerInventoryDiagnostic mismatch = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == LeaseReceiverMismatch);

        Assert.EndsWith("/call@" + run.Coordinate, mismatch.Identity, StringComparison.Ordinal);

        // The refused traversal grants nothing: its group is the Effect of no send. The worker's own traversal still holds both.
        Assert.Contains(run.Groups, static group => group.EffectFrontierCapsuleId is null);

        Assert.Contains(run.Groups, static group => group.EffectFrontierCapsuleId is not null);
    }

    // The null test of an optional lease prunes the guarded region only when the traversal knows the lease IS the null
    // constant. A local written anywhere the evaluator does not model is not known: an out or ref argument, a ref local, `??=`,
    // a deconstruction, Interlocked, or a write after the use that a loop, a goto or a later-declared local function carries
    // back to it. The region is then traversed, and the raw send inside it is reported with the group's refusal. The same holds
    // for `is not null` and for `is { }`.
    private const string LeaseReceiverPrunedMembers = """
        private static void Fill(out IGrimoireWorkLease? target) => target = new FakeLease();
        """;

    private const string LeaseReceiverPrunedCall = "await Optional.Prune.RecoverPruneCoreAsync(maybe);";

    private static string LeaseReceiverPrunedBody(string shape) => shape switch
    {
        "literal-null" => "await Optional.Prune.RecoverPruneCoreAsync(null);",
        "local-null" => "IGrimoireWorkLease? maybe = null;\n\n" + LeaseReceiverPrunedCall,
        "local-default" => "IGrimoireWorkLease? maybe = default;\n\n" + LeaseReceiverPrunedCall,
        "fake" => "await Optional.Prune.RecoverPruneCoreAsync(fake);",
        "nested-block" => "IGrimoireWorkLease? maybe = null;\n\nif (token.CanBeCanceled) { maybe = fake; }\n\n" + LeaseReceiverPrunedCall,
        "lambda" => "IGrimoireWorkLease? maybe = null;\n\nAction set = () => maybe = fake;\n\nset();\n\n" + LeaseReceiverPrunedCall,
        "out" => "IGrimoireWorkLease? maybe = null;\n\nFill(out maybe);\n\n" + LeaseReceiverPrunedCall,
        "ref-local" => "IGrimoireWorkLease? maybe = null;\n\nref IGrimoireWorkLease? alias = ref maybe;\n\nalias = fake;\n\n" + LeaseReceiverPrunedCall,
        "coalesce-assign" => "IGrimoireWorkLease? maybe = null;\n\nmaybe ??= fake;\n\n" + LeaseReceiverPrunedCall,
        "deconstruct" => "IGrimoireWorkLease? maybe = null;\n\n(maybe, _) = (fake, 0);\n\n" + LeaseReceiverPrunedCall,
        "interlocked" => "IGrimoireWorkLease? maybe = null;\n\nInterlocked.Exchange(ref maybe, fake);\n\n" + LeaseReceiverPrunedCall,
        "loop-carried" => "IGrimoireWorkLease? maybe = null;\n\nfor (int round = 0; round < 2; round++)\n{\n    " + LeaseReceiverPrunedCall + "\n\n    maybe = fake;\n}",
        "foreach-carried" => "IGrimoireWorkLease? maybe = null;\n\nforeach (int round in new[] { 0, 1 })\n{\n    " + LeaseReceiverPrunedCall + "\n\n    maybe = fake;\n}",
        "foreach-deconstruct-carried" => "IGrimoireWorkLease? maybe = null;\n\nforeach (var (_, round) in new[] { (fake, 0), (fake, 1) })\n{\n    " + LeaseReceiverPrunedCall + "\n\n    maybe = fake;\n}",
        "goto-carried" => "IGrimoireWorkLease? maybe = null;\n\nint rounds = 0;\n\nagain:\n\n" + LeaseReceiverPrunedCall + "\n\nmaybe = fake;\n\nif (++rounds < 2) goto again;",
        "later-local-function" => "IGrimoireWorkLease? maybe = null;\n\nSet();\n\n" + LeaseReceiverPrunedCall + "\n\nvoid Set() => maybe = fake;",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static string LeaseReceiverPrunedSource(string shape, string test)
    {
        string core = $$"""
            if ({{test}})
            {
                System.IO.File.Delete("lease-receiver-raw");

                if (!workLease.TryBeginExternalEffectGroup(out System.IDisposable group)) return;

                using var held = group;

                await Task.CompletedTask;

                System.IO.File.Delete("lease-receiver-send");
            }
            """;

        return LeaseReceiverSource("using IGrimoireWorkLease lease = admitted!;", LeaseReceiverPrunedBody(shape), LeaseReceiverPrunedMembers)
            + LeaseReceiverOptionalCore(core);
    }

    public static TheoryData<string, string> LeaseReceiverPrunedProvablyNull() => Combine(["literal-null", "local-null", "local-default"]);

    public static TheoryData<string, string> LeaseReceiverPrunedNotProvablyNull() => Combine(
        ["fake", "nested-block", "lambda", "out", "ref-local", "coalesce-assign", "deconstruct", "interlocked", "loop-carried", "foreach-carried", "foreach-deconstruct-carried", "goto-carried", "later-local-function"]);

    private static TheoryData<string, string> Combine(string[] shapes)
    {
        TheoryData<string, string> rows = [];

        foreach (string shape in shapes)
        {
            foreach (string test in new[] { "workLease is not null", "workLease is { }" })
            {
                rows.Add(shape, test);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(LeaseReceiverPrunedProvablyNull))]
    public void LeaseReceiverProofPrunesTheRegionOfAProvablyNullLease(string shape, string test)
    {
        LeaseReceiverRun run = RunLeaseReceiver(LeaseReceiverPrunedSource(shape, test), shape + " " + test, OrdinaryRoot());

        // Neither the raw send nor the group nor the send behind it exists: the null traversal never enters the region.
        Assert.Empty(run.Result.Diagnostics);

        Assert.Empty(run.Groups);

        Assert.DoesNotContain(run.Result.Items, static site => site.Callee == LeaseReceiverSend);
    }

    [Theory]
    [MemberData(nameof(LeaseReceiverPrunedNotProvablyNull))]
    public void LeaseReceiverProofKeepsTheRegionOfALeaseThatIsNotProvablyNull(string shape, string test)
    {
        LeaseReceiverRun run = RunLeaseReceiver(LeaseReceiverPrunedSource(shape, test), shape + " " + test, OrdinaryRoot());

        // The region is traversed: the group's receiver is a formal bound to no exact acquisition, so it is refused once at its own
        // coordinate and the raw send and the send behind the refused group both report their missing Effect.
        HostedProducerInventoryDiagnostic mismatch = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == LeaseReceiverMismatch);

        Assert.EndsWith("/call@" + run.Coordinate, mismatch.Identity, StringComparison.Ordinal);

        Assert.Equal(2, run.Result.Diagnostics.Count(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == LeaseReceiverSend));

        Assert.Equal([LeaseReceiverMismatch, "HOSTED_SITE_EFFECT_FRONTIER_MISSING"], [.. run.Result.Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

        Assert.All(run.Groups, static group => Assert.Null(group.EffectFrontierCapsuleId));
    }

    // One lambda reads a formal of its declaring method, and is reached by two different callers. Each caller binds its own
    // formals: the first binds the declaring method's formal to the acquired lease, the second reaches the lambda from a local
    // function whose formals have the same ordinals and the same origins but are not the lambda's captured formal, which that
    // state binds to a fake. The proof of one state is never the proof of the other.
    private const string LeaseReceiverCollisionMembers = """
        private static async Task First(IGrimoireWorkLease outer, IGrimoireWorkLease real, bool direct)
        {
            Func<Task> act = async () =>
            {
                if (!outer.TryBeginExternalEffectGroup(out IDisposable group)) return;

                using var held = group;

                await Task.CompletedTask;

                System.IO.File.Delete("lease-receiver-send");
            };

            if (direct)
            {
                await act();

                return;
            }

            async Task Helper(IGrimoireWorkLease h, IGrimoireWorkLease h2, bool b)
            {
                await act();
            }

            await Helper(real, real, false);
        }
        """;

    // The same two callers, but the lambda also has a parameter of its own at ordinal 0, beside the declaring method's captured
    // formal at ordinal 0.
    private static readonly string LeaseReceiverCollisionOwnParameterMembers = LeaseReceiverCollisionMembers
        .Replace("Func<Task> act = async () =>", "Func<IGrimoireWorkLease, Task> act = async received =>", StringComparison.Ordinal)
        .Replace("await act();", "await act(real);", StringComparison.Ordinal);

    [Theory]
    [InlineData("exact-only", "await First(lease, lease, true);", false, false)]
    [InlineData("poisoned", "await First(lease, lease, true);\n\nawait First(fake, lease, false);", true, false)]
    [InlineData("fake-only", "await First(fake, lease, false);", true, false)]
    [InlineData("own-parameter-exact-only", "await First(lease, lease, true);", false, true)]
    [InlineData("own-parameter-poisoned", "await First(lease, lease, true);\n\nawait First(fake, lease, false);", true, true)]
    [InlineData("own-parameter-fake-only", "await First(fake, lease, false);", true, true)]
    public void LeaseReceiverProofNeverReusesTheProofOfAnotherBindingState(string label, string body, bool refused, bool ownParameter)
    {
        string members = ownParameter ? LeaseReceiverCollisionOwnParameterMembers : LeaseReceiverCollisionMembers;

        LeaseReceiverRun run = RunLeaseReceiver(LeaseReceiverSource("using IGrimoireWorkLease lease = admitted!;", body, members), label, OrdinaryRoot());

        if (!refused)
        {
            Assert.Empty(run.Result.Diagnostics);

            Assert.All(run.Groups, static group => Assert.Equal(group.CapsuleId, group.EffectFrontierCapsuleId));

            return;
        }

        // Every state that reaches the group through a fake is refused at the group, and no group of such a state is an Effect.
        HostedProducerInventoryDiagnostic mismatch = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == LeaseReceiverMismatch);

        Assert.EndsWith("/call@" + run.Coordinate, mismatch.Identity, StringComparison.Ordinal);

        Assert.Contains(run.Groups, static group => group.EffectFrontierCapsuleId is null);

        Assert.Contains(run.Result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == LeaseReceiverSend);

        Assert.Equal(label.EndsWith("poisoned", StringComparison.Ordinal), run.Groups.Any(static group => group.EffectFrontierCapsuleId is not null));
    }

    // A variable handed out by reference can be overwritten by the callee, spelled or implicit: an `in` or `ref readonly` argument
    // is no promise that nothing writes it (Unsafe.AsRef writes through it). The grammar counts every by-reference use as a write.
    private const string LeaseReceiverReadonlyMembers = """
        private static void Smash(in IGrimoireWorkLease target) => System.Runtime.CompilerServices.Unsafe.AsRef(in target) = new FakeLease();

        private static void SmashNullable(ref readonly IGrimoireWorkLease? target) => System.Runtime.CompilerServices.Unsafe.AsRef(in target) = new FakeLease();
        """;

    private static string LeaseReceiverReadonlyFormals(string mutation) => LeaseReceiverFormals.Replace("if (!inner.TryBeginExternalEffectGroup",
        mutation + "\n\n    if (!inner.TryBeginExternalEffectGroup", StringComparison.Ordinal) + "\n\n" + LeaseReceiverReadonlyMembers;

    private static (string Lease, string Body, string Members) LeaseReceiverReadonlyShapeOf(string shape) => shape switch
    {
        "designation-asref" => ("using IGrimoireWorkLease lease = admitted!;", "System.Runtime.CompilerServices.Unsafe.AsRef(in admitted) = fake;\n\n" + LeaseReceiverBody("admitted!"), ""),
        "designation-readonly-helper" => ("using IGrimoireWorkLease lease = admitted!;", "SmashNullable(in admitted);\n\n" + LeaseReceiverBody("admitted!"), LeaseReceiverReadonlyMembers),
        "copy-asref" => ("using IGrimoireWorkLease lease = admitted!;", "IGrimoireWorkLease current = admitted!;\n\nSystem.Runtime.CompilerServices.Unsafe.AsRef(in current) = fake;\n\n" + LeaseReceiverBody("current"), ""),
        "copy-implicit-in" => ("using IGrimoireWorkLease lease = admitted!;", "IGrimoireWorkLease current = admitted!;\n\nSmash(current);\n\n" + LeaseReceiverBody("current"), LeaseReceiverReadonlyMembers),
        "formal-asref" => ("using IGrimoireWorkLease lease = admitted!;", "await First(lease, token);", LeaseReceiverReadonlyFormals("System.Runtime.CompilerServices.Unsafe.AsRef(in inner) = new FakeLease();")),
        "formal-implicit-in" => ("using IGrimoireWorkLease lease = admitted!;", "await First(lease, token);", LeaseReceiverReadonlyFormals("Smash(inner);")),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    [Theory]
    [InlineData("designation-asref")]
    [InlineData("designation-readonly-helper")]
    [InlineData("copy-asref")]
    [InlineData("copy-implicit-in")]
    [InlineData("formal-asref")]
    [InlineData("formal-implicit-in")]
    public void LeaseReceiverProofCountsAByReferenceUseOfTheReceiverAsAWrite(string shape)
    {
        (string lease, string body, string members) = LeaseReceiverReadonlyShapeOf(shape);

        LeaseReceiverRun run = RunLeaseReceiver(LeaseReceiverSource(lease, body, members), shape, OrdinaryRoot());

        HostedProducerInventoryDiagnostic mismatch = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == LeaseReceiverMismatch);

        Assert.EndsWith("/call@" + run.Coordinate, mismatch.Identity, StringComparison.Ordinal);

        Assert.Null(Assert.Single(run.Groups).EffectFrontierCapsuleId);

        Assert.Null(run.Send.EffectFrontierCapsuleId);
    }

    // A group begun while the traversal holds no Work is judged by its receiver alone, as the Lease Spine does for a paid door whose
    // lease departed: the group on the departed lease stands and each send under it reports its own missing Work. A counterfeit
    // receiver is refused whether or not Work is held, and a group begun while no Work is held may not meet a Work the same member
    // starts afterwards, because a send under both would pair the Effect of one lease with the Work of another.
    [Theory]
    [InlineData("departed-lease")]
    [InlineData("departed-fake")]
    [InlineData("departed-then-second-work")]
    public void LeaseReceiverProofJudgesAGroupBegunWithoutWorkByItsReceiverAndKeepsItFromALaterWork(string shape)
    {
        string receiver = shape == "departed-fake" ? "fake" : "admitted!";

        string later = shape == "departed-then-second-work"
            ? """
                if (!gate.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? second)) return;

                using IGrimoireWorkLease other = second!;
                """
            : "";

        string body = "admitted!.Dispose();\n\nif (!" + receiver + ".TryBeginExternalEffectGroup(out IDisposable group)) return;\n\nusing var held = group;\n\n" + later
            + "\n\nawait Task.CompletedTask;\n\nSystem.IO.File.Delete(\"lease-receiver-send\");";

        LeaseReceiverRun run = RunLeaseReceiver(LeaseReceiverSource("using IGrimoireWorkLease lease = admitted!;", body, ""), shape, OrdinaryRoot());

        HostedProducerSite group = Assert.Single(run.Groups);

        if (shape == "departed-lease")
        {
            // The receiver is the acquired lease, so the group stands and is the send's Effect; the send has no Work, which is its refusal.
            Assert.DoesNotContain(run.Result.Diagnostics, static diagnostic => diagnostic.Code == LeaseReceiverMismatch);

            Assert.Equal(group.CapsuleId, group.EffectFrontierCapsuleId);

            Assert.Null(run.Send.WorkFrontierCapsuleId);

            Assert.Equal(group.CapsuleId, run.Send.EffectFrontierCapsuleId);

            Assert.Contains(run.Result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == LeaseReceiverSend);

            return;
        }

        HostedProducerInventoryDiagnostic mismatch = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == LeaseReceiverMismatch);

        Assert.EndsWith("/call@" + run.Coordinate, mismatch.Identity, StringComparison.Ordinal);

        Assert.Null(group.EffectFrontierCapsuleId);

        Assert.Null(run.Send.EffectFrontierCapsuleId);

        // The later Work is the second acquisition's, and the send holds it without the dead lease's group.
        Assert.Equal(shape == "departed-then-second-work" ? run.Acquisitions[1].CapsuleId : null, run.Send.WorkFrontierCapsuleId);
    }

    // The holder-era receiver witnesses (the per-unit recovery handoff, the ordinary scoped holder, the readiness source and the
    // scoped lease instance) each prove their own receiver exactly and are retired with the holder, so a member that carries one is
    // not required to prove a must-alias receiver. That is no licence for a counterfeit one: in each exempt context a group on a
    // fake lease or on a ternary of the holder's lease and a fake gets no Effect from the witness, and a group on a must-alias of a
    // second acquisition is still refused here, because a receiver that IS a must-alias is judged whatever the member carries. The
    // readiness context's counterfeit receivers are pinned by `Readiness_physical_group_requires_stable_exact_lease_value`
    // (reassigned, ref-replaced, out-replaced, authored-cast, foreign-holder and ternary-fake) and the scoped lease instance's by
    // the `foreign-instance-group` row of `DrainedMaintenanceInterfaceDispatchRequiresTheActualBoundLease`.
    private const string LeaseReceiverHolderGroup = "if (!lease.TryBeginExternalEffectGroup(out IDisposable group)) return; using var owned = group;";

    private const string LeaseReceiverHolderFakeTypes = """

        public sealed class HolderEraFakeLease : RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease
        {
            public bool TryBeginExternalEffectGroup(out System.IDisposable group) { group = new HolderEraFakeGroup(); return true; }

            public void Dispose() { }

            public System.Threading.Tasks.ValueTask DisposeAsync() => default;
        }

        public sealed class HolderEraFakeGroup : System.IDisposable
        {
            public void Dispose() { }
        }
        """;

    private HostedProducerDiscovery<HostedProducerSite> RunLeaseReceiverHolderEra(string context, string label, string counterfeit)
    {
        string source = context == "ordinary-holder" ? OrdinaryScopedHolderFixture("same-scoped-lease") : PerUnitRecoveryMatrixFixture("same-lease");

        // The holder under test is the last member of the fixture that spells the group this way (earlier ones belong to the
        // shared matrix the fixture is built from).
        int index = source.LastIndexOf(LeaseReceiverHolderGroup, StringComparison.Ordinal);

        Assert.True(index >= 0);

        source = source[..index] + counterfeit + source[(index + LeaseReceiverHolderGroup.Length)..] + LeaseReceiverHolderFakeTypes;

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result;

        if (context == "ordinary-holder")
        {
            HostedProducerDiscovery<string> hosted = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]);

            result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], hosted, [new("Worker", [OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep }])], []);
        }
        else
        {
            result = DiscoverRecoveryMatrixFixture([compilation]);
        }

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"LEASE_RECEIVER_HOLDER_DIAGNOSTIC {context} {label} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        return result;
    }

    private static HostedProducerSite[] LeaseReceiverHolderSites(HostedProducerDiscovery<HostedProducerSite> result, string member) =>
        [.. result.Items.Where(site => site.RootType == "Worker" && site.Member == member && site.Callee is LeaseReceiverGroup or LeaseReceiverSend)];

    [Theory]
    [InlineData("ordinary-holder", "fake")]
    [InlineData("ordinary-holder", "ternary")]
    [InlineData("per-unit", "fake")]
    [InlineData("per-unit", "ternary")]
    public void LeaseReceiverProofLeavesNoHolderEraContextACounterfeitReceiver(string context, string receiver)
    {
        string counterfeit = receiver == "fake"
            ? "if (!new HolderEraFakeLease().TryBeginExternalEffectGroup(out IDisposable group)) return; using var owned = group;"
            : "if (!(System.DateTime.UtcNow.Ticks > 0 ? lease : new HolderEraFakeLease()).TryBeginExternalEffectGroup(out IDisposable group)) return; using var owned = group;";

        HostedProducerDiscovery<HostedProducerSite> result = RunLeaseReceiverHolderEra(context, receiver, counterfeit);

        // The counterfeit group is nobody's Effect and the send behind it reports its missing Effect.
        HostedProducerSite[] first = LeaseReceiverHolderSites(result, "SendFirstAsync");

        Assert.Equal(2, first.Length);

        Assert.All(first, static site => Assert.Null(site.EffectFrontierCapsuleId));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == LeaseReceiverSend);

        // The sibling method, whose group is the holder's own lease, keeps its Effect: the witness is exact, not blanket.
        HostedProducerSite[] second = LeaseReceiverHolderSites(result, "SendSecondAsync");

        Assert.Equal(2, second.Length);

        Assert.All(second, static site => Assert.NotNull(site.EffectFrontierCapsuleId));

        // The holder-era witness refuses it with its own code; the receiver rule adds nothing for a receiver it cannot prove alias.
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == LeaseReceiverMismatch && diagnostic.Identity.Contains("SendFirstAsync", StringComparison.Ordinal));
    }

    // A receiver that IS a must-alias is judged whatever the member carries: a group begun on a second acquisition inside the
    // ordinary scoped holder's member is not the lease whose Work the traversal holds, so the rule refuses it at its coordinate.
    [Fact]
    public void LeaseReceiverProofJudgesAMustAliasReceiverInAHolderEraMember()
    {
        const string counterfeit = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; "
            + "if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var second)) return; using var other = second; "
            + "if (!other.TryBeginExternalEffectGroup(out IDisposable group)) return; using var owned = group;";

        HostedProducerDiscovery<HostedProducerSite> result = RunLeaseReceiverHolderEra("ordinary-holder", "second-acquisition", counterfeit);

        HostedProducerSite group = Assert.Single(LeaseReceiverHolderSites(result, "SendFirstAsync"), static site => site.Callee == LeaseReceiverGroup);

        Assert.Null(group.EffectFrontierCapsuleId);

        Assert.Null(Assert.Single(LeaseReceiverHolderSites(result, "SendFirstAsync"), static site => site.Callee == LeaseReceiverSend).EffectFrontierCapsuleId);

        string coordinate = group.OperationId[(group.OperationId.LastIndexOf("/site@", StringComparison.Ordinal) + "/site@".Length)..];

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == LeaseReceiverMismatch
            && diagnostic.Identity.EndsWith("/call@" + coordinate, StringComparison.Ordinal));
    }
}
