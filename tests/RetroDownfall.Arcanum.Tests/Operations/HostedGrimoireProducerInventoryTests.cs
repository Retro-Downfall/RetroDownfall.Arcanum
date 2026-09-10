using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Api;

using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Infrastructure.Data;

using System.Collections.Immutable;

using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed class HostedGrimoireProducerInventoryTests(ITestOutputHelper output)
{
    private static string R2Source(string body, string extra = "") => RegistrationSource("services.AddHostedService<Worker>();").Replace("public Task StartAsync(CancellationToken token) => Task.CompletedTask;", "public async Task StartAsync(CancellationToken token) { " + body + " }", StringComparison.Ordinal) + AdmissionTypes + extra;

    private static string R2Admission => AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal) + " if (!lease.TryBeginExternalEffectGroup(out var group)) return; using var held = group; ";

    private static HostedProducerDiscovery<HostedProducerSite> R2Discover(string source, params HostedProducerOperationEntry[] roots)
    {
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", roots.Length == 0 ? [OrdinaryRoot()] : roots)], []);
    }

    private static string RecoveryMatrixFixture() =>
        RegistrationSource(
                "services.AddScoped<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler, RetroDownfall.Arcanum.Infrastructure.Operations.DbRecoveryHandler>(); "
                + "services.AddScoped<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler, RetroDownfall.Arcanum.Infrastructure.Operations.ExternalRecoveryHandler>(); "
                + "services.AddScoped<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler, RetroDownfall.Arcanum.Infrastructure.Operations.OwnerRecoveryHandler>(); "
                + "services.AddScoped<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler, RetroDownfall.Arcanum.Infrastructure.Operations.UnsupportedRecoveryHandler>();")
            .Replace(
                "public Task StartAsync(CancellationToken token) => Task.CompletedTask;",
                "public async Task StartAsync(CancellationToken token) { "
                + AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal)
                + " var operation = new RetroDownfall.Arcanum.Core.Operations.LongRunningOperation(RetroDownfall.Arcanum.Core.Operations.LongRunningOperationKinds.External, 0);"
                + " var decision = RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationRecoveryAdmission.Classify(operation, null);"
                + " IDisposable effectGroup = null!; try {"
                + " if (decision.Kind is RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect && !lease.TryBeginExternalEffectGroup(out effectGroup)) return;"
                + " else { await new RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler().SettleDiscoveredRuntimeAsync(operation); }"
                + " } finally { if (effectGroup is not null) effectGroup.Dispose(); } }",
                StringComparison.Ordinal)
            + AdmissionTypes
            + """
                namespace RetroDownfall.Arcanum.Core.Operations
                {
                    public sealed record LongRunningOperation(string Kind, int CheckpointVersion);

                    public sealed record LongRunningOperationRecoveryDescriptor(
                        string Kind,
                        int MinCheckpointVersion,
                        int MaxCheckpointVersion);

                    public interface ILongRunningOperationRecoveryHandler
                    {
                        string Kind { get; }

                        int SupportedCheckpointVersion { get; }

                        Task RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken);
                    }

                    public static class LongRunningOperationKinds
                    {
                        public const string Db = "db";

                        public const string External = "external";

                        public const string Owner = "owner";

                        public const string Unsupported = "unsupported";
                    }

                    public static class LongRunningOperationRecoveryRegistry
                    {
                        private static readonly LongRunningOperationRecoveryDescriptor[] Matrix =
                        [
                            new(LongRunningOperationKinds.Db, 0, 0),
                            new(LongRunningOperationKinds.External, 0, 0),
                            new(LongRunningOperationKinds.Owner, 0, 0),
                            new(LongRunningOperationKinds.Unsupported, 0, 0),
                        ];
                    }
                }

                namespace RetroDownfall.Arcanum.Infrastructure.Operations
                {
                    using RetroDownfall.Arcanum.Core.Operations;

                    internal sealed class LongRunningRecoveryOwnerEvidence { }

                    internal enum LongRunningRecoveryAdmissionKind : byte
                    {
                        OrdinaryDbOnly = 1,
                        OrdinaryExternalEffect = 2,
                        OwnerBoundOffline = 3,
                        OwnerBoundAwaitingExactOwner = 4,
                        UnsupportedCheckpointVersion = 5,
                    }

                    internal readonly record struct LongRunningRecoveryAdmissionDecision(
                        LongRunningRecoveryAdmissionKind Kind);

                    internal static class LongRunningOperationRecoveryAdmission
                    {
                        internal static LongRunningRecoveryAdmissionDecision Classify(
                            LongRunningOperation operation,
                            LongRunningRecoveryOwnerEvidence? ownerEvidence)
                        {
                            LongRunningRecoveryAdmissionKind kind =
                                (operation.Kind, operation.CheckpointVersion) switch
                                {
                                    (LongRunningOperationKinds.Db, 0) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
                                    (LongRunningOperationKinds.External, 0) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect,
                                    (LongRunningOperationKinds.Owner, 0) => OwnerBoundKind(ownerEvidence),
                                    _ => LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion,
                                };

                            return new(kind);
                        }

                        private static LongRunningRecoveryAdmissionKind OwnerBoundKind(object? evidence) =>
                            evidence is null
                                ? LongRunningRecoveryAdmissionKind.OwnerBoundAwaitingExactOwner
                                : LongRunningRecoveryAdmissionKind.OwnerBoundOffline;
                    }

                    internal sealed class LongRunningOperationReconciler(
                        object? discovery = null,
                        object? classifiedLeaseAcquisition = null,
                        object? scopeFactory = null)
                    {
                        internal async Task SettleDiscoveredRuntimeAsync(LongRunningOperation discovered)
                        {
                            LongRunningRecoveryAdmissionDecision before =
                                LongRunningOperationRecoveryAdmission.Classify(discovered, null);

                            if (before.Kind is LongRunningRecoveryAdmissionKind.OwnerBoundAwaitingExactOwner)
                            {
                                return;
                            }

                            LongRunningRecoveryAdmissionDecision after =
                                LongRunningOperationRecoveryAdmission.Classify(discovered, null);

                            if (after.Kind is LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion)
                            {
                                /*unsupported-rejection*/ return;
                            }

                            await SettleLeasedAsync(discovered);
                        }

                        internal async Task SettleExactlyAsync(
                            LongRunningOperation operation,
                            LongRunningRecoveryOwnerEvidence ownerEvidence)
                        {
                            LongRunningRecoveryAdmissionDecision decision =
                                LongRunningOperationRecoveryAdmission.Classify(operation, ownerEvidence);

                            if (decision.Kind is not LongRunningRecoveryAdmissionKind.OwnerBoundOffline)
                            {
                                return;
                            }

                            await SettleLeasedAsync(operation);
                        }

                        internal async Task ReconcileAsync(LongRunningOperation discovered)
                        {
                            async Task SettleAsync(LongRunningOperation operation)
                            {
                                LongRunningRecoveryAdmissionDecision admission =
                                    LongRunningOperationRecoveryAdmission.Classify(operation, null);

                                if (admission.Kind is LongRunningRecoveryAdmissionKind.OwnerBoundAwaitingExactOwner)
                                {
                                    return;
                                }

                                await (admission.Kind is LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion
                                    ? Task.CompletedTask
                                    : SettleLeasedAsync(operation));
                            }

                            await SettleAsync(discovered);
                        }

                        private static async Task SettleLeasedAsync(LongRunningOperation operation) =>
                            await RecoverOneAsync(operation);

                        private static async Task RecoverOneAsync(LongRunningOperation operation)
                        {
                            ILongRunningOperationRecoveryHandler handler = null!;

                            await handler.RecoverAsync(operation, CancellationToken.None);
                        }
                    }

                    internal sealed class DbRecoveryHandler : ILongRunningOperationRecoveryHandler
                    {
                        public string Kind => LongRunningOperationKinds.Db;

                        public int SupportedCheckpointVersion => 0;

                        public Task RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
                        {
                            _ = System.IO.File.Exists("db");

                            return Task.CompletedTask;
                        }
                    }

                    internal sealed class ExternalRecoveryHandler : ILongRunningOperationRecoveryHandler
                    {
                        public string Kind => LongRunningOperationKinds.External;

                        public int SupportedCheckpointVersion => 0;

                        public Task RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
                        {
                            System.IO.File.Delete("external");

                            return Task.CompletedTask;
                        }
                    }

                    internal sealed class OwnerRecoveryHandler : ILongRunningOperationRecoveryHandler
                    {
                        public string Kind => LongRunningOperationKinds.Owner;

                        public int SupportedCheckpointVersion => 0;

                        public Task RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
                        {
                            System.IO.File.Delete("owner");

                            return Task.CompletedTask;
                        }
                    }

                    internal sealed class UnsupportedRecoveryHandler : ILongRunningOperationRecoveryHandler
                    {
                        public string Kind => LongRunningOperationKinds.Unsupported;

                        public int SupportedCheckpointVersion => 0;

                        public Task RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
                        {
                            System.IO.File.Delete("unsupported");

                            return Task.CompletedTask;
                        }
                    }

                    internal sealed class CliOwnerRecoveryHandler : ILongRunningOperationRecoveryHandler
                    {
                        public string Kind => /*cli-owner-kind*/ LongRunningOperationKinds.Owner;

                        public int SupportedCheckpointVersion => 0;

                        public Task RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
                        {
                            System.IO.File.Delete("cli-owner");

                            return Task.CompletedTask;
                        }
                    }

                    internal static class StoppedHostComposition
                    {
                        internal static void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
                        {
                            services.AddScoped<ILongRunningOperationRecoveryHandler, CliOwnerRecoveryHandler>();

                            _ = new LongRunningOperationReconciler(
                                discovery: null,
                                classifiedLeaseAcquisition: null,
                                scopeFactory: null);
                        }
                    }

                    internal static class OwnerRecoveryRoot
                    {
                        internal static async Task Run()
                        {
                            await new LongRunningOperationReconciler().SettleExactlyAsync(
                                new LongRunningOperation(LongRunningOperationKinds.Owner, 0),
                                new LongRunningRecoveryOwnerEvidence());
                        }
                    }
                }
                """;

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverRecoveryMatrixFixture(
        string source,
        bool includeOwnerRoot = true,
        HostedProducerAuthorityKind hostedAuthority = HostedProducerAuthorityKind.OrdinaryHostedWork)
    {
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerOperationEntry hosted = OrdinaryRoot() with
        {
            Authority = hostedAuthority,
            WorkKind = hostedAuthority == HostedProducerAuthorityKind.OrdinaryHostedWork
                ? GrimoireWorkKind.WorkspaceIndexing
                : null,
            Proof = hostedAuthority == HostedProducerAuthorityKind.PreReadinessStartup
                ? "bounded readiness recovery"
                : null,
        };

        NonHostedProducerChainEntry[] ownerRoots = includeOwnerRoot
            ?
            [
                new(
                    "RetroDownfall.Arcanum.Infrastructure.Operations.OwnerRecoveryRoot.Run",
                    "src/Fixture.cs",
                    "RetroDownfall.Arcanum.Infrastructure.Operations.OwnerRecoveryRoot",
                    "Run",
                    HostedProducerAuthorityKind.OwnerBoundRecovery,
                    "exact owner fixture",
                    []),
            ]
            : [];

        return HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation],
            new(["Worker"], []),
            [new("Worker", [hosted])],
            ownerRoots);
    }

    [Fact]
    public void ClosedRecoveryMatrixSeparatesDbExternalOwnerUnsupportedAndCliOnlyHandlers()
    {
        HostedProducerDiscovery<HostedProducerSite> result =
            DiscoverRecoveryMatrixFixture(RecoveryMatrixFixture());

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code.StartsWith("HOSTED_RECOVERY_", StringComparison.Ordinal)
            || diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        HostedProducerSite[] genericEffects = result.Items
            .Where(static site => site.RootType == "Worker"
                && site.Callee == "System.IO.File.Delete")
            .ToArray();

        Assert.Single(genericEffects);

        Assert.Equal(
            "RetroDownfall.Arcanum.Infrastructure.Operations.ExternalRecoveryHandler",
            genericEffects[0].EnclosingType);

        Assert.DoesNotContain(result.Items, static site =>
            site.EnclosingType is
                "RetroDownfall.Arcanum.Infrastructure.Operations.OwnerRecoveryHandler"
                or "RetroDownfall.Arcanum.Infrastructure.Operations.UnsupportedRecoveryHandler");

        Assert.Contains(result.Items, static site =>
            site.RootType
                == "RetroDownfall.Arcanum.Infrastructure.Operations.OwnerRecoveryRoot"
            && site.EnclosingType
                == "RetroDownfall.Arcanum.Infrastructure.Operations.CliOwnerRecoveryHandler"
            && site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void ReadinessRecoveryUsesTheSameClosedMatrixWithoutOrdinaryFrontiers()
    {
        string source = RecoveryMatrixFixture().Replace(
            "SettleDiscoveredRuntimeAsync(operation)",
            "ReconcileAsync(operation)",
            StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result =
            DiscoverRecoveryMatrixFixture(
                source,
                hostedAuthority: HostedProducerAuthorityKind.PreReadinessStartup);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code.StartsWith("HOSTED_RECOVERY_", StringComparison.Ordinal));

        Assert.Single(result.Items, static site =>
            site.RootType == "Worker"
            && site.EnclosingType
                == "RetroDownfall.Arcanum.Infrastructure.Operations.ExternalRecoveryHandler"
            && site.Callee == "System.IO.File.Delete");
    }

    [Theory]
    [InlineData("db-effect", "HOSTED_RECOVERY_DB_EFFECT")]
    [InlineData("unsupported-fallthrough", "HOSTED_RECOVERY_DISPATCH_UNPROVEN")]
    [InlineData("missing-owner-root", "HOSTED_RECOVERY_OWNER_ROOT_UNPROVEN")]
    [InlineData("cli-external", "HOSTED_RECOVERY_HANDLER_UNPROVEN")]
    [InlineData("missing-external-frontier", "HOSTED_RECOVERY_EFFECT_FRONTIER_UNPROVEN")]
    [InlineData("owner-fallthrough", "HOSTED_RECOVERY_OWNER_DISPATCH_UNPROVEN")]
    [InlineData("readiness-unsupported-fallthrough", "HOSTED_RECOVERY_DISPATCH_UNPROVEN")]
    public void ClosedRecoveryMatrixFailsClosedOnIndependentAuthorityMutation(
        string mutation,
        string expectedDiagnostic)
    {
        string source = RecoveryMatrixFixture();

        bool includeOwnerRoot = mutation != "missing-owner-root";

        HostedProducerAuthorityKind hostedAuthority =
            mutation == "readiness-unsupported-fallthrough"
                ? HostedProducerAuthorityKind.PreReadinessStartup
                : HostedProducerAuthorityKind.OrdinaryHostedWork;

        if (mutation == "db-effect")
        {
            source = source.Replace(
                "_ = System.IO.File.Exists(\"db\");",
                "System.IO.File.Delete(\"db\");",
                StringComparison.Ordinal);
        }
        else if (mutation == "unsupported-fallthrough")
        {
            source = source.Replace(
                "/*unsupported-rejection*/ return;",
                "_ = 0;",
                StringComparison.Ordinal);
        }
        else if (mutation == "cli-external")
        {
            source = source.Replace(
                "/*cli-owner-kind*/ LongRunningOperationKinds.Owner",
                "/*cli-owner-kind*/ LongRunningOperationKinds.External",
                StringComparison.Ordinal);
        }
        else if (mutation == "missing-external-frontier")
        {
            source = source.Replace(
                "&& !lease.TryBeginExternalEffectGroup(out effectGroup)",
                "&& false",
                StringComparison.Ordinal);
        }
        else if (mutation == "owner-fallthrough")
        {
            source = source.Replace(
                "decision.Kind is not LongRunningRecoveryAdmissionKind.OwnerBoundOffline",
                "decision.Kind is LongRunningRecoveryAdmissionKind.OwnerBoundOffline",
                StringComparison.Ordinal);
        }
        else if (mutation == "readiness-unsupported-fallthrough")
        {
            source = source
                .Replace(
                    "SettleDiscoveredRuntimeAsync(operation)",
                    "ReconcileAsync(operation)",
                    StringComparison.Ordinal)
                .Replace(
                    "admission.Kind is LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion",
                    "admission.Kind is not LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion",
                    StringComparison.Ordinal);
        }

        HostedProducerDiscovery<HostedProducerSite> result =
            DiscoverRecoveryMatrixFixture(
                source,
                includeOwnerRoot,
                hostedAuthority);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == expectedDiagnostic);
    }

    [Theory]
    [InlineData("await pending;", true)]
    [InlineData("await pending.ConfigureAwait(false);", true)]
    [InlineData("await (DateTime.UtcNow.Ticks < 0 ? pending : Task.CompletedTask);", false)]
    [InlineData("await Task.WhenAny(pending, Task.CompletedTask);", false)]
    [InlineData("if (DateTime.UtcNow.Ticks < 0) await pending;", false)]
    public void R3TaskLocalRequiresExactUnconditionalJoin(string join, bool owned)
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "var pending = Task.Run(() => System.IO.File.Delete(\"path\")); " + join));

        Assert.Equal(owned, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(owned, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("missing-normal-join", false)]
    [InlineData("missing-exceptional-join", false)]
    [InlineData("conditional-exceptional-join", false)]
    [InlineData("task-escape", false)]
    [InlineData("inert-local-between-task-and-try", true)]
    public void TrackedCallbackTaskMustJoinOnNormalAndExceptionalExit(
        string shape,
        bool owned)
    {
        string normalJoin = shape == "missing-normal-join"
            ? ""
            : "await task.ConfigureAwait(false);";

        string exceptionalJoin = shape switch
        {
            "missing-exceptional-join" => "",
            "conditional-exceptional-join" =>
                "if (DateTime.UtcNow.Ticks < 0) await ObserveAsync(task).ConfigureAwait(false);",
            _ => "await ObserveAsync(task).ConfigureAwait(false);",
        };

        string escape = shape switch
        {
            "task-escape" => "GC.KeepAlive(task);",
            "inert-local-between-task-and-try" => "Task? pendingHeartbeatDelay = null;",
            _ => "",
        };

        string helper = """
            static class TrackedRunner
            {
                public static async Task RunAsync(
                    Func<CancellationToken, Task> action,
                    CancellationToken cancellationToken)
                {
                    using CancellationTokenSource cancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    Task task = action(cancellation.Token);
                    ESCAPE

                    try
                    {
                        while (!task.IsCompleted)
                        {
                            Task delay = Task.Delay(1, cancellationToken);
                            Task completed = await Task.WhenAny(task, delay).ConfigureAwait(false);

                            if (ReferenceEquals(completed, task))
                            {
                                break;
                            }

                            await delay.ConfigureAwait(false);

                            if (DateTime.UtcNow.Ticks < 0)
                            {
                                throw new InvalidOperationException();
                            }
                        }

                        NORMAL_JOIN
                    }
                    catch
                    {
                        try
                        {
                            cancellation.Cancel();
                        }
                        finally
                        {
                            EXCEPTIONAL_JOIN
                        }

                        throw;
                    }
                }

                private static async Task ObserveAsync(Task task)
                {
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
            """
            .Replace("NORMAL_JOIN", normalJoin, StringComparison.Ordinal)
            .Replace("EXCEPTIONAL_JOIN", exceptionalJoin, StringComparison.Ordinal)
            .Replace("ESCAPE", escape, StringComparison.Ordinal);

        string body = R2Admission
            + "await TrackedRunner.RunAsync(async cancellationToken => { "
            + "System.IO.File.Delete(\"path\"); await Task.Yield(); }, token);";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body, helper));

        Assert.Equal(
            owned,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && !diagnostic.Detail.StartsWith(
                    "System.Threading.Tasks.Task.Delay;",
                    StringComparison.Ordinal)));

        Assert.Equal(
            owned,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("exact", false)]
    [InlineData("missing-cancel", true)]
    [InlineData("missing-observer", true)]
    [InlineData("wrong-token", true)]
    [InlineData("overwrite-before-join", true)]
    public void LosingTimerTaskRequiresCancellationAndExactFinallyObservation(
        string shape,
        bool expectedUnowned)
    {
        string token = shape == "wrong-token"
            ? "CancellationToken.None"
            : "heartbeatCancellation.Token";

        string assignment = "pending = Task.Delay(1, " + token + ");";

        if (shape == "overwrite-before-join")
        {
            assignment += " pending = Task.Delay(1, " + token + ");";
        }

        string cancel = shape == "missing-cancel"
            ? string.Empty
            : "heartbeatCancellation.Cancel();";

        string observation = shape == "missing-observer"
            ? string.Empty
            : "if (pending is not null) { await ObserveAsync(pending).ConfigureAwait(false); }";

        string helper = $$"""
            static class TimerRace
            {
                internal static async Task RunAsync(CancellationToken cancellationToken)
                {
                    using CancellationTokenSource heartbeatCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    Task action = Task.CompletedTask;

                    Task? pending = null;

                    try
                    {
                        while (!action.IsCompleted)
                        {
                            {{assignment}}

                            Task completed = await Task.WhenAny(action, pending).ConfigureAwait(false);

                            if (ReferenceEquals(completed, action))
                            {
                                break;
                            }

                            await pending.ConfigureAwait(false);

                            pending = null;
                        }

                        await action.ConfigureAwait(false);
                    }
                    finally
                    {
                        try
                        {
                            {{cancel}}
                        }
                        finally
                        {
                            {{observation}}
                        }
                    }
                }

                private static async Task ObserveAsync(Task task)
                {
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source("await TimerRace.RunAsync(token);", helper));

        Assert.Equal(
            expectedUnowned,
            result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Detail.StartsWith(
                    "System.Threading.Tasks.Task.Delay;",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public void CompletionConfigurationAndTerminalDisposalDoNotEscapeAdmissionHandles()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "var pending = Task.Run(() => System.IO.File.Delete(\"path\")); await pending.ConfigureAwait(false); held.Dispose();"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE");
    }

    [Theory]
    [InlineData("held.Release();", false)]
    [InlineData("held.ReleaseAndUse();", false)]
    [InlineData("held.Keep();", true)]
    [InlineData("held.ReleaseOther(other);", true)]
    [InlineData("other.ReleaseOther(held);", false)]
    [InlineData("Helper.Release(other); Helper.Release(held);", false)]
    [InlineData("Helper.Keep(held); Helper.Release(other);", true)]
    [InlineData("Helper.Release(condition ? held : other);", false)]
    [InlineData("Helper.Release(other ?? held);", false)]
    [InlineData("Helper.Release(condition switch { true => held, _ => other });", false)]
    [InlineData("Helper.Release(new[] { held, other }[condition ? 0 : 1]);", false)]
    [InlineData("Helper.Release((held, other).Item1);", false)]
    [InlineData("GC.KeepAlive(condition ? held : other);", false)]
    [InlineData("GC.KeepAlive(from item in new[] { held, other } select item);", false)]
    [InlineData("held?.Release();", false)]
    [InlineData("new Box(held).Release();", false)]
    [InlineData("held.GetHashCode();", false)]
    public void R4AdmissionOriginsIncludeReceiversAndCompositeArguments(string operation, bool retained)
    {
        string helpers = "static class Helper { public static void Release(this IDisposable handle) => handle.Dispose(); public static void ReleaseAndUse(this IDisposable handle) { handle.Dispose(); System.IO.File.Delete(\"inside\"); } public static void Keep(this IDisposable handle) {} public static void ReleaseOther(this IDisposable handle, IDisposable other) => other.Dispose(); } class Box(IDisposable handle) { public void Release() => handle.Dispose(); }";

        string source = R2Source(R2Admission + "IDisposable other=null!; bool condition=DateTime.UtcNow.Ticks<0; " + operation + " System.IO.File.Delete(\"after\");", helpers);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(operation.StartsWith("GC.KeepAlive(from", StringComparison.Ordinal) ? "using System.Linq;" + source : source);

        Assert.Equal(retained, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Fact]
    public void R4OpaqueCallbackCaptureRemainsAnExplicitOwnershipRefusal()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "GC.KeepAlive((Action)(() => held.Dispose()));"));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void ScalarValueReadFromAdmissionHandleDoesNotBecomeTheHandle()
    {
        const string helper = "sealed record Entry(bool Present);";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "var entries = new System.Collections.Generic.List<Entry>(); entries.Add(new Entry(lease is not null));", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE");
    }

    [Fact]
    public void OpaqueCollectionStillRejectsAnObjectThatActuallyCapturesTheHandle()
    {
        const string helper = "sealed class Box(IDisposable handle) { public IDisposable Handle => handle; }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "var entries = new System.Collections.Generic.List<Box>(); entries.Add(new Box(held));", helper));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE");
    }

    private static string R4SafeCarrierFactory(
        string beforeReturn = "",
        string arguments = "a,b",
        string extraAcquisition = "") =>
        "public static Carrier Create(RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store) { "
        + "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter? a=null,b=null; try { "
        + "a=store.CreateWriterAsync(); b=store.CreateWriterAsync(); "
        + extraAcquisition
        + beforeReturn
        + " return new Carrier(" + arguments + "); } catch { "
        + "if (b is not null) { try { b.Dispose(); } catch {} } "
        + "if (a is not null) { try { a.Dispose(); } catch {} } throw; } }";

    private static string R4CarrierSource(
        string constructor,
        string completion = "await first.CompleteAsync(); await second.CompleteAsync();",
        string disposal = "first.Dispose(); second.Dispose();",
        bool readOnly = true,
        string beforeReturn = "",
        string arguments = "a,b") =>
        R2Source(
            R2Admission
            + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; "
            + "using var writers=Carrier.Create(store); await writers.CompleteAsync();",
            R2Blobs
            + "class Carrier : IDisposable { private "
            + (readOnly ? "readonly " : "")
            + "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter first,second; "
            + "private Carrier(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter a, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter b) { "
            + constructor
            + " } "
            + R4SafeCarrierFactory(beforeReturn, arguments)
            + " public async Task CompleteAsync() { "
            + completion
            + " } public void Dispose() { "
            + disposal
            + " } }");

    [Theory]
    [InlineData("stable", true)]
    [InlineData("overwrite", false)]
    [InlineData("conditional", false)]
    [InlineData("mutable", false)]
    [InlineData("parameter-write", false)]
    [InlineData("local-write", false)]
    [InlineData("named-arguments", true)]
    [InlineData("ref-field", false)]
    public void R4CarrierMappingRequiresOneStableWriterPerReadonlyField(string shape, bool complete)
    {
        string constructor = shape switch { "overwrite" => "first=a; second=b; second=a;", "conditional" => "first=a; if (DateTime.UtcNow.Ticks<0) second=b;", "parameter-write" => "b=a; first=a; second=b;", "ref-field" => "first=a; second=b; System.Threading.Interlocked.Exchange(ref second,a);", _ => "first=a; second=b;" };

        string source = R4CarrierSource(constructor, readOnly: shape != "mutable", beforeReturn: shape == "local-write" ? "b=a;" : "", arguments: shape == "named-arguments" ? "b:b,a:a" : "a,b");

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Equal(complete, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData("await first.CompleteAsync(); await second.CompleteAsync();", true)]
    [InlineData("try { await first.CompleteAsync(); } catch { await second.CompleteAsync(); }", false)]
    [InlineData("try { await first.CompleteAsync(); } finally { await second.CompleteAsync(); }", false)]
    [InlineData("if (DateTime.UtcNow.Ticks < 0) { await first.CompleteAsync(); await second.CompleteAsync(); }", false)]
    [InlineData("switch (DateTime.UtcNow.Ticks) { case < 0: await first.CompleteAsync(); await second.CompleteAsync(); break; default: break; }", false)]
    [InlineData("for (var index = 0; index < 1; index++) { await first.CompleteAsync(); await second.CompleteAsync(); }", false)]
    [InlineData("await first.CompleteAsync(); goto done; await second.CompleteAsync(); done:;", false)]
    [InlineData("lock (this) { first.CompleteAsync().GetAwaiter().GetResult(); second.CompleteAsync().GetAwaiter().GetResult(); }", false)]
    [InlineData("using (var resource = new System.IO.MemoryStream()) { await first.CompleteAsync(); await second.CompleteAsync(); }", false)]
    [InlineData("checked { await first.CompleteAsync(); await second.CompleteAsync(); }", false)]
    public void R4CarrierTerminalsRequireOneSupportedExecutablePath(string terminals, bool complete)
    {
        foreach (bool disposal in new[] { false, true })
        {
            string cleanup = terminals.Replace("await ", "", StringComparison.Ordinal).Replace(".GetAwaiter().GetResult()", "", StringComparison.Ordinal).Replace("CompleteAsync", "Dispose", StringComparison.Ordinal);

            HostedProducerDiscovery<HostedProducerSite> result = R2Discover(disposal ? R4CarrierSource("first=a; second=b;", disposal: cleanup) : R4CarrierSource("first=a; second=b;", completion: terminals));

            Assert.Equal(complete, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
        }
    }

    [Theory]
    [InlineData("stable", true)]
    [InlineData("factory-authored-dispose", false)]
    [InlineData("factory-authored-store", false)]
    [InlineData("factory-opaque", false)]
    [InlineData("constructor-authored-dispose", false)]
    [InlineData("constructor-authored-store", false)]
    [InlineData("constructor-opaque", false)]
    [InlineData("completion-authored-store", false)]
    [InlineData("completion-opaque", false)]
    [InlineData("disposal-authored-store", false)]
    [InlineData("disposal-opaque", false)]
    public void R5CarrierOwnershipAllowsOnlyTheExactDirectTransferAndTerminals(string shape, bool complete)
    {
        const string helper = "static class WriterSink { public static RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter Saved=null!; public static void Dispose(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) => writer.Dispose(); public static void Store(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) { Saved=writer; } }";

        string constructor = shape switch { "constructor-authored-dispose" => "WriterSink.Dispose(a); first=a; second=b;", "constructor-authored-store" => "WriterSink.Store(a); first=a; second=b;", "constructor-opaque" => "GC.KeepAlive(a); first=a; second=b;", _ => "first=a; second=b;" };

        string beforeReturn = shape switch { "factory-authored-dispose" => "WriterSink.Dispose(a);", "factory-authored-store" => "WriterSink.Store(a);", "factory-opaque" => "GC.KeepAlive(a);", _ => "" };

        string completion = shape switch { "completion-authored-store" => "await first.CompleteAsync(); WriterSink.Store(first); await second.CompleteAsync();", "completion-opaque" => "await first.CompleteAsync(); GC.KeepAlive(first); await second.CompleteAsync();", _ => "await first.CompleteAsync(); await second.CompleteAsync();" };

        string disposal = shape switch { "disposal-authored-store" => "first.Dispose(); WriterSink.Store(first); second.Dispose();", "disposal-opaque" => "first.Dispose(); GC.KeepAlive(first); second.Dispose();", _ => "first.Dispose(); second.Dispose();" };

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R4CarrierSource(constructor, completion, disposal, beforeReturn: beforeReturn) + helper);

        Assert.Equal(complete, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void R5AdmissionCachesSeparateDistinctTreesWithTheSamePathAndSpan(bool firstAdmitted)
    {
        static string Source(string worker, bool admitted)
        {
            string gate = admitted ? "TryBeginExternalEffectGroup" : "TryBeginExternalEffectGrouq";

            string helper = "static class SharedHelper { public static void Run(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease) { if (!lease." + gate + "(out var group)) return; using var held=group; System.IO.File.Delete(\"path\"); } } static class SimilarGate { public static bool TryBeginExternalEffectGrouq(this RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease, out IDisposable group) { group=null!; return true; } }";

            return R2Source(AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal) + " SharedHelper.Run(lease);", helper).Replace("Worker", worker, StringComparison.Ordinal);
        }

        CSharpCompilation first = Compile(Source("WorkerA", firstAdmitted)).WithAssemblyName("CacheContextA");

        CSharpCompilation second = Compile(Source("WorkerB", !firstAdmitted)).WithAssemblyName("CacheContextB");

        SyntaxTree firstTree = first.SyntaxTrees.Single();

        SyntaxTree secondTree = second.SyntaxTrees.Single();

        Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax firstHelper = firstTree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Run");

        Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax secondHelper = secondTree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Run");

        Assert.NotSame(firstTree, secondTree);

        Assert.Equal(firstTree.FilePath, secondTree.FilePath);

        Assert.Equal(firstHelper.Span, secondHelper.Span);

        HostedProducerOperationEntry FirstRoot(string worker) => new(worker + ".StartAsync", "src/Fixture.cs", worker, "StartAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.WorkspaceIndexing, null, []);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([first, second], new(["WorkerA", "WorkerB"], []), [new("WorkerA", [FirstRoot("WorkerA")]), new("WorkerB", [FirstRoot("WorkerB")])], []);

        string admitted = firstAdmitted ? "WorkerA.StartAsync" : "WorkerB.StartAsync";

        string unadmitted = firstAdmitted ? "WorkerB.StartAsync" : "WorkerA.StartAsync";

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Identity.StartsWith(admitted, StringComparison.Ordinal));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Identity.StartsWith(unadmitted, StringComparison.Ordinal));
    }

    [Fact]
    public void R7GraphIdentitySeparatesAliasedSamePathSameSpanHelpers()
    {
        static CSharpCompilation Helper(string assembly, string operation) => Compile("namespace RetroDownfall.Collision { public static class Shared { public static void Run() { System.IO.File." + operation + "(\"path\"); } } }").WithAssemblyName(assembly);

        CSharpCompilation first = Helper("Collision.First", "Exists");

        CSharpCompilation second = Helper("Collision.Second", "Delete");

        MetadataReference firstReference = first.ToMetadataReference(ImmutableArray.Create("first"));

        MetadataReference secondReference = second.ToMetadataReference(ImmutableArray.Create("second"));

        string source = "extern alias first; extern alias second; " + FixtureSource("first::RetroDownfall.Collision.Shared.Run(); second::RetroDownfall.Collision.Shared.Run();");

        CSharpCompilation producer = Compile(source).AddReferences(firstReference, secondReference).WithAssemblyName("Collision.Producer");

        Assert.Empty(first.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(second.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(producer.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        MethodDeclarationSyntax firstRun = first.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        MethodDeclarationSyntax secondRun = second.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        Assert.Equal(firstRun.Span, secondRun.Span);

        Assert.Equal(firstRun.SyntaxTree.FilePath, secondRun.SyntaxTree.FilePath);

        Assert.Equal(first.GetSemanticModel(firstRun.SyntaxTree).GetDeclaredSymbol(firstRun)!.GetDocumentationCommentId(), second.GetSemanticModel(secondRun.SyntaxTree).GetDeclaredSymbol(secondRun)!.GetDocumentationCommentId());

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([first, second, producer], new(["Worker"], []), [new("Worker", [new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, null, [])])], []);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    [Fact]
    public void R7LifecycleMembershipCannotHideSameKeyNonLifecycleCaller()
    {
        const string prefix = "using Microsoft.Extensions.Hosting; using System.Threading; using System.Threading.Tasks; namespace RetroDownfall.Collision { public static class Shared { public static void Caller() { ";

        string lifecycleSource = prefix + "Lifecycle.Run(); } } public static class Lifecycle { public static void Run() { System.IO.File.Exists(\"path\"); } } public class Worker : IHostedService { public Task StartAsync(CancellationToken token) { Shared.Caller(); return Task.CompletedTask; } public Task StopAsync(CancellationToken token) => Task.CompletedTask; } }";

        string nonLifecycleSource = prefix + "HostedJob.Run(); } } public static class HostedJob { public static void Run() { System.IO.File.Delete(\"path\"); } } }";

        CSharpCompilation lifecycle = Compile(lifecycleSource).WithAssemblyName("Collision.Lifecycle");

        CSharpCompilation nonLifecycle = Compile(nonLifecycleSource).WithAssemblyName("Collision.NonLifecycle");

        Assert.Empty(lifecycle.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(nonLifecycle.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        MethodDeclarationSyntax lifecycleCaller = lifecycle.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Caller");

        MethodDeclarationSyntax nonLifecycleCaller = nonLifecycle.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Caller");

        Assert.Equal(lifecycleCaller.Span, nonLifecycleCaller.Span);

        Assert.Equal(lifecycle.GetSemanticModel(lifecycleCaller.SyntaxTree).GetDeclaredSymbol(lifecycleCaller)!.GetDocumentationCommentId(), nonLifecycle.GetSemanticModel(nonLifecycleCaller.SyntaxTree).GetDeclaredSymbol(nonLifecycleCaller)!.GetDocumentationCommentId());

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([lifecycle, nonLifecycle], new(["Worker", "HostedJob"], []), [], []);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.OperationId.StartsWith("RetroDownfall.Collision.HostedJob.Run/", StringComparison.Ordinal));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_EXTERNAL_OPERATION_UNCATALOGUED" && diagnostic.Detail == "RetroDownfall.Collision.HostedJob.Run");
    }

    [Fact]
    public void R7AmbiguousFirstPartySameKeyCallEmitsUnresolved()
    {
        const string helper = "namespace RetroDownfall.Collision { public static class Shared { public static void Run() { System.IO.File.Exists(\"path\"); } } }";

        CSharpCompilation first = Compile(helper).WithAssemblyName("Collision.Twin");

        CSharpCompilation second = Compile(helper).WithAssemblyName("Collision.Twin");

        using MemoryStream image = new();

        Assert.True(first.Emit(image).Success);

        MetadataReference reference = MetadataReference.CreateFromImage(image.ToArray(), MetadataReferenceProperties.Assembly.WithAliases(ImmutableArray.Create("twin")));

        CSharpCompilation producer = Compile("extern alias twin; " + FixtureSource("twin::RetroDownfall.Collision.Shared.Run();")).AddReferences(reference).WithAssemblyName("Collision.Producer");

        Assert.Empty(second.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(producer.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([first, second, producer], new(["Worker"], []), [new("Worker", [new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, null, [])])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Detail == "RetroDownfall.Collision.Shared.Run");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R8AliasedInterfaceBindingsFollowTheirExactCompilationInEveryCacheOrder(bool reverse)
    {
        static CSharpCompilation Service(string assembly, string operation) => Compile("using Microsoft.Extensions.DependencyInjection; namespace RetroDownfall.Collision { public interface IWorker { void Run(); } public sealed class Worker : IWorker { public void Run() { System.IO.File." + operation + "(\"path\"); } } public static class Composition { public static void Configure(IServiceCollection services) { services.AddSingleton<IWorker, Worker>(); } } }").WithAssemblyName(assembly);

        CSharpCompilation first = Service("Collision.FirstService", "Exists");

        CSharpCompilation second = Service("Collision.SecondService", "Delete");

        MetadataReference firstReference = first.ToMetadataReference(ImmutableArray.Create("first"));

        MetadataReference secondReference = second.ToMetadataReference(ImmutableArray.Create("second"));

        string body = "first::RetroDownfall.Collision.IWorker firstWorker=null!; second::RetroDownfall.Collision.IWorker secondWorker=null!; firstWorker.Run(); secondWorker.Run(); firstWorker.Run();";

        CSharpCompilation producer = Compile("extern alias first; extern alias second; " + FixtureSource(body)).AddReferences(firstReference, secondReference).WithAssemblyName("Collision.Producer");

        Assert.Empty(first.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(second.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(producer.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        CSharpCompilation[] compilations = reverse ? [second, first, producer] : [first, second, producer];

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(compilations, new(["Worker"], []), [new("Worker", [new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, null, [])])], []);

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R8UnmappableInterfaceBindingIsUnresolvedAndNeverCached(bool reverse)
    {
        static CSharpCompilation Service(string operation) => Compile("using Microsoft.Extensions.DependencyInjection; namespace RetroDownfall.Collision { public interface IWorker { void Run(); } public sealed class Worker : IWorker { public void Run() { System.IO.File." + operation + "(\"path\"); } } public static class Composition { public static void Configure(IServiceCollection services) { services.AddSingleton<IWorker, Worker>(); } } }").WithAssemblyName("Collision.TwinService");

        CSharpCompilation first = Service("Exists");

        CSharpCompilation second = Service("Delete");

        using MemoryStream image = new();

        Assert.True(first.Emit(image).Success);

        MetadataReference reference = MetadataReference.CreateFromImage(image.ToArray(), MetadataReferenceProperties.Assembly.WithAliases(ImmutableArray.Create("twin")));

        string body = "twin::RetroDownfall.Collision.IWorker worker=null!; worker.Run(); worker.Run();";

        CSharpCompilation producer = Compile("extern alias twin; " + FixtureSource(body)).AddReferences(reference).WithAssemblyName("Collision.Producer");

        Assert.Empty(first.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(second.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(producer.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        CSharpCompilation[] compilations = reverse ? [second, first, producer] : [first, second, producer];

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(compilations, new(["Worker"], []), [new("Worker", [new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, null, [])])], []);

        Assert.Equal(2, result.Diagnostics.Count(static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Detail == "RetroDownfall.Collision.IWorker.Run"));

        Assert.DoesNotContain(result.Items, static site => site.Callee is "System.IO.File.Exists" or "System.IO.File.Delete");
    }

    [Fact]
    public void ReviewedClosedWholeProgramDispatchBindsItsExactImplementation()
    {
        const string contracts = "namespace RetroDownfall.Arcanum.Core.Backup { public interface IBackupRestoreEffectDigestCalculator { void Compute(); } public sealed class BackupRestoreEffectDigestCalculator : IBackupRestoreEffectDigestCalculator { public void Compute() { System.IO.File.Exists(\"path\"); } } }";

        string source = FixtureSource("RetroDownfall.Arcanum.Core.Backup.IBackupRestoreEffectDigestCalculator calculator = null!; calculator.Compute();", contracts);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CLOSED_DISPATCH_CHANGED" or "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    [Fact]
    public void ReviewedClosedWholeProgramDispatchRejectsAnAddedImplementation()
    {
        const string contracts = "namespace RetroDownfall.Arcanum.Core.Backup { public interface IBackupRestoreEffectDigestCalculator { void Compute(); } public sealed class BackupRestoreEffectDigestCalculator : IBackupRestoreEffectDigestCalculator { public void Compute() { } } public sealed class UnexpectedDigestCalculator : IBackupRestoreEffectDigestCalculator { public void Compute() { } } }";

        string source = FixtureSource("RetroDownfall.Arcanum.Core.Backup.IBackupRestoreEffectDigestCalculator calculator = null!; calculator.Compute();", contracts);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CLOSED_DISPATCH_CHANGED");
    }

    [Fact]
    public void ReviewedExclusiveRegistrationDispatchBindsItsExactSynchronousCallback()
    {
        const string contracts = "namespace RetroDownfall.Arcanum.Core.Covenant { public interface ICovenantExclusiveLeaseRegistration { void ExecuteWhileHeld(Action callback); } } namespace RetroDownfall.Arcanum.Infrastructure.Covenant { public sealed class CovenantOperationGate { private sealed class ExclusiveRegistration : RetroDownfall.Arcanum.Core.Covenant.ICovenantExclusiveLeaseRegistration { public void ExecuteWhileHeld(Action callback) { callback(); } } } }";

        string source = FixtureSource(
            "RetroDownfall.Arcanum.Core.Covenant.ICovenantExclusiveLeaseRegistration registration = null!; registration.ExecuteWhileHeld(() => System.IO.File.Delete(\"path\"));",
            contracts);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Single(
            result.Items,
            static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CLOSED_DISPATCH_CHANGED"
                    or "HOSTED_CALL_TARGET_UNRESOLVED"
                    or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void ReviewedHumanPromptReservationDispatchBindsItsOnlyRuntimeImplementation()
    {
        const string contracts = "namespace RetroDownfall.Arcanum.Core.Intelligence { public interface IHumanPromptReservation { Task<string> WaitAsync(CancellationToken token); } } namespace RetroDownfall.Arcanum.Api.Intelligence { public sealed class HumanPromptRegistry { private sealed class Reservation : RetroDownfall.Arcanum.Core.Intelligence.IHumanPromptReservation { public Task<string> WaitAsync(CancellationToken token) { System.IO.File.Delete(\"path\"); return Task.FromResult(string.Empty); } } } }";

        string source = FixtureSource(
            "RetroDownfall.Arcanum.Core.Intelligence.IHumanPromptReservation reservation = null!; await reservation.WaitAsync(default);",
            contracts);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Single(
            result.Items,
            static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CLOSED_DISPATCH_CHANGED"
                    or "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    [Fact]
    public void ReviewedHumanPromptEmitterDispatchBindsItsOnlyRuntimeImplementation()
    {
        const string contracts = "namespace RetroDownfall.Arcanum.Core.Intelligence { public interface IHumanPromptLiveEmitter { ValueTask EmitAsync(object value, CancellationToken token); } } namespace RetroDownfall.Arcanum.Api.Intelligence { public sealed class WizardIntelligenceProvider { private sealed class ChannelHumanPromptLiveEmitter : RetroDownfall.Arcanum.Core.Intelligence.IHumanPromptLiveEmitter { public ValueTask EmitAsync(object value, CancellationToken token) { System.IO.File.Delete(\"path\"); return ValueTask.CompletedTask; } } } }";

        string source = FixtureSource(
            "RetroDownfall.Arcanum.Core.Intelligence.IHumanPromptLiveEmitter emitter = null!; await emitter.EmitAsync(new object(), default);",
            contracts);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Single(
            result.Items,
            static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CLOSED_DISPATCH_CHANGED"
                    or "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    [Fact]
    public void ReviewedSessionTurnBeginDispatchBindsItsOnlyRuntimeImplementation()
    {
        const string contracts = "namespace RetroDownfall.Arcanum.Core.Storage { public interface ISessionTurnBeginStore { ValueTask CreateBoundSessionAsync(); } } namespace RetroDownfall.Arcanum.Infrastructure.Repositories { public sealed class GrimoireRepository : RetroDownfall.Arcanum.Core.Storage.ISessionTurnBeginStore { public ValueTask CreateBoundSessionAsync() { System.IO.File.Delete(\"path\"); return ValueTask.CompletedTask; } } }";

        string source = FixtureSource(
            "RetroDownfall.Arcanum.Core.Storage.ISessionTurnBeginStore store = null!; await store.CreateBoundSessionAsync();",
            contracts);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Single(
            result.Items,
            static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CLOSED_DISPATCH_CHANGED"
                    or "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    [Theory]
    [InlineData("IDisposable alias; alias = group; alias.Dispose(); System.IO.File.Delete(\"path\");", false)]
    [InlineData("Helper.Use(held);", false)]
    [InlineData("Helper.Release(held); System.IO.File.Delete(\"path\");", false)]
    [InlineData("Helper.Forward(held); System.IO.File.Delete(\"path\");", false)]
    [InlineData("Helper.Escape(ref group); System.IO.File.Delete(\"path\");", false)]
    [InlineData("Helper.Replace(out group); System.IO.File.Delete(\"path\");", false)]
    [InlineData("var alias = Helper.Return(held); alias.Dispose(); System.IO.File.Delete(\"path\");", false)]
    [InlineData("Helper.Store(held); Helper.Saved.Dispose(); System.IO.File.Delete(\"path\");", false)]
    [InlineData("Helper.Keep(held);", true)]
    [InlineData("IDisposable alias; alias = group; Helper.Keep(alias);", true)]
    public void R3AdmissionIdentitySurvivesAssignmentsAndFormalParameters(string body, bool retained)
    {
        string helper = "static class Helper { public static IDisposable Saved=null!; public static IDisposable Return(IDisposable handle) => handle; public static void Store(IDisposable handle) { Saved=handle; } public static void Use(IDisposable handle) { handle.Dispose(); System.IO.File.Delete(\"path\"); } public static void Release(IDisposable handle) { handle.Dispose(); } public static void Forward(IDisposable handle) { IDisposable alias; alias=handle; Release(alias); } public static void Escape(ref IDisposable handle) { handle = null!; } public static void Replace(out IDisposable handle) { handle = null!; } public static void Keep(IDisposable handle) { System.IO.File.Delete(\"path\"); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + body, helper));

        Assert.Equal(retained, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Fact]
    public void SynchronousValueTaskDisposalChainDoesNotEscapeTheAdmissionHandle()
    {
        string body = AcquireWork + " ValueTask disposal = lease.DisposeAsync(); if (!disposal.IsCompletedSuccessfully) disposal.AsTask().GetAwaiter().GetResult();";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(FixtureSource(body, AdmissionTypes), OrdinaryRoot());

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE");
    }

    [Fact]
    public void SynchronousCallerRetainsTransferredWorkLeaseThroughExactFinallyDisposal()
    {
        const string body = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var admitted)) return Task.CompletedTask; RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease = admitted; try { System.IO.File.Exists(\"path\"); } finally { ValueTask disposal = lease.DisposeAsync(); if (!disposal.IsCompletedSuccessfully) disposal.AsTask().GetAwaiter().GetResult(); }";

        HostedProducerDiscovery<HostedProducerSite> result =
            DiscoverWithRoots(
                FixtureSource(body, AdmissionTypes),
                OrdinaryRoot());

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData("both", false)]
    [InlineData("overload", false)]
    [InlineData("conditional", false)]
    [InlineData("unreachable", false)]
    [InlineData("detached", false)]
    [InlineData("joined", true)]
    public void R3EachWriterRequiresTheActuallyInvokedCarrierTerminals(string shape, bool complete)
    {
        string completion = shape switch { "both" or "detached" => "first.CompleteAsync(); second.CompleteAsync();", "joined" => "await first.CompleteAsync(); await second.CompleteAsync();", "overload" => "first.CompleteAsync();", "conditional" => "first.CompleteAsync(); if (DateTime.UtcNow.Ticks < 0) second.CompleteAsync();", _ => "first.CompleteAsync(); return; second.CompleteAsync();" };

        string disposal = shape switch { "both" or "detached" or "joined" => "first.Dispose(); second.Dispose();", "overload" => "first.Dispose();", "conditional" => "first.Dispose(); if (DateTime.UtcNow.Ticks < 0) second.Dispose();", _ => "first.Dispose(); return; second.Dispose();" };

        string helper = "class Carrier : IDisposable { private readonly RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter first, second; private Carrier(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter a, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter b) { first=a; second=b; } " + R4SafeCarrierFactory() + " public void CompleteAsync() { " + completion + " } public void CompleteAsync(bool unused) => second.CompleteAsync(); public void Dispose() { " + disposal + " } public void Dispose(bool unused) => second.Dispose(); }";

        bool asynchronous = shape is "detached" or "joined";

        if (asynchronous)
        {
            helper = helper.Replace("public void CompleteAsync()", "public async Task CompleteAsync()", StringComparison.Ordinal).Replace("public void CompleteAsync(bool unused) => second.CompleteAsync();", "public Task CompleteAsync(bool unused) => second.CompleteAsync();", StringComparison.Ordinal);
        }

        string blobs = R2Blobs;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; using var writers=Carrier.Create(store); " + (asynchronous ? "await " : "") + "writers.CompleteAsync();", blobs + helper));

        Assert.Equal(complete, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Interlocked.Exchange(ref background, Task.CompletedTask);", false)]
    [InlineData("Helper.Replace(ref background);", false)]
    [InlineData("Helper.Set(out background);", false)]
    [InlineData("ref Task? alias = ref background; alias = Task.CompletedTask;", false)]
    [InlineData("int ignored; (background, ignored) = (Task.CompletedTask, 0);", false)]
    public void R3HostHandoffJoinsTheExactDispatchedTask(string mutation, bool owned)
    {
        string source = R2Source("background = Task.Run(() => ContinueAsync(token)); " + mutation, "static class Helper { public static void Replace(ref Task? task) { task=Task.CompletedTask; } public static void Set(out Task? task) { task=Task.CompletedTask; } }")
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "private Task? background; private async Task ContinueAsync(CancellationToken token) { " + R2Admission + " System.IO.File.Delete(\"path\"); } public async Task StopAsync(CancellationToken token) { if (background is not null) await background; }", StringComparison.Ordinal);

        HostedProducerOperationEntry startup = OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: readiness" };

        HostedProducerOperationEntry runtime = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync" };

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source, startup, runtime);

        Assert.Equal(owned, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(owned ? 1 : 2, result.Items.Count(static site => site.Callee == "System.IO.File.Delete"));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void StartNewHandoffRequiresUnwrapSinglePublicationAndStoppedHostJoin(bool unwrap, bool overwrite, bool omitJoin, bool owned)
    {
        string dispatch = "background = Task.Factory.StartNew(ContinueAsync, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)" + (unwrap ? ".Unwrap()" : "") + "; " + (overwrite ? "background = Task.CompletedTask;" : "");

        string stop = omitJoin
            ? "public Task StopAsync(CancellationToken token) => Task.CompletedTask;"
            : "public async Task StopAsync(CancellationToken token) { Task? copy = background; if (copy is not null) await copy; }";

        string source = R2Source(dispatch)
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "private Task? background; private async Task ContinueAsync() { " + R2Admission + " System.IO.File.Delete(\"path\"); } " + stop, StringComparison.Ordinal);

        HostedProducerOperationEntry startup = OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: readiness" };

        HostedProducerOperationEntry runtime = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync" };

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source, startup, runtime);

        Assert.Equal(owned, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("background = Task.CompletedTask;", false)]
    public void StartNewHandoffAllowsOneMutuallyExclusiveSynchronousStartupAssignment(string overwrite, bool owned)
    {
        string body = "if (DateTime.UtcNow.Ticks < 0) { background = Task.CompletedTask; await background; return; } background = Task.Factory.StartNew(ContinueAsync, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap(); " + overwrite + " return;";

        string source = R2Source(body)
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "private Task? background; private async Task ContinueAsync() { " + R2Admission + " System.IO.File.Delete(\"path\"); } public async Task StopAsync(CancellationToken token) { Task? copy = background; if (copy is not null) await copy; }", StringComparison.Ordinal);

        HostedProducerOperationEntry startup = OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: readiness" };

        HostedProducerOperationEntry runtime = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync" };

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source, startup, runtime);

        Assert.Equal(owned, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));
    }

    [Fact]
    public void StartNewHandoffAllowsABlockingStartupBranchAndFinallyJoinedStopHelper()
    {
        string source = RegistrationSource("services.AddHostedService<Worker>();")
            .Replace(
                "public Task StartAsync(CancellationToken token) => Task.CompletedTask;",
                "private Task? background; public Task StartAsync(CancellationToken token) { bool blocksStartup = DateTime.UtcNow.Ticks < 0; lock (this) { if (background is not null) return blocksStartup ? background.WaitAsync(token) : Task.CompletedTask; if (blocksStartup) { background = Task.CompletedTask; return background.WaitAsync(token); } background = Task.Factory.StartNew(ContinueAsync, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap(); return Task.CompletedTask; } } private async Task ContinueAsync() { "
                    + R2Admission
                    + " System.IO.File.Delete(\"path\"); }",
                StringComparison.Ordinal)
            .Replace(
                "public Task StopAsync(CancellationToken token) => Task.CompletedTask;",
                "public Task StopAsync(CancellationToken token) => StopCoreAsync(); private async Task StopCoreAsync() { Task? copy; lock (this) copy = background; try { await Task.Yield(); } finally { if (copy is not null) { try { await copy.ConfigureAwait(false); } catch (Exception) { } } } }",
                StringComparison.Ordinal)
            + AdmissionTypes;

        HostedProducerOperationEntry startup = OrdinaryRoot() with
        {
            Authority = HostedProducerAuthorityKind.PreReadinessStartup,
            WorkKind = null,
            Proof = "Worker.StartAsync: readiness",
        };

        HostedProducerOperationEntry runtime = OrdinaryRoot("Worker.ContinueAsync") with
        {
            Member = "ContinueAsync",
        };

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            source,
            startup,
            runtime);

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void HostHandoffMayTargetOneReviewedCapsuleRootInsideItsWorker()
    {
        string source = R2Source("background = Task.Run(() => ContinueAsync(token));")
            .Replace(
                "public Task StopAsync(CancellationToken token) => Task.CompletedTask;",
                "private Task? background; private async Task ContinueAsync(CancellationToken token) { "
                    + R2Admission
                    + " System.IO.File.Delete(\"path\"); } public async Task StopAsync(CancellationToken token) { if (background is not null) await background; }",
                StringComparison.Ordinal);

        HostedProducerOperationEntry startup = OrdinaryRoot() with
        {
            Authority = HostedProducerAuthorityKind.PreReadinessStartup,
            WorkKind = null,
            Proof = "Worker.StartAsync: readiness",
        };

        string fingerprint = HostedGrimoireProducerInventory.Fingerprint(
            SyntaxFactory.ParseExpression("System.IO.File.Delete(\"path\")"));

        HostedProducerOperationEntry runtime = OrdinaryRoot(
            "Worker.ContinueAsync::call:System.IO.File.Delete#0~" + fingerprint) with
        {
            Member = "ContinueAsync",
        };

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            source,
            startup,
            runtime);

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Single(
            result.Items,
            static site => site.Callee == "System.IO.File.Delete");
    }

    [Theory]
    [InlineData("return background.WaitAsync(token);", true)]
    [InlineData("return Task.CompletedTask;", false)]
    public void LifecycleFieldPublicationRequiresAnImmediateReturnedJoin(string completion, bool owned)
    {
        string source = FixtureSource("background = Helper.RunAsync(); " + completion, "static class Helper { internal static async Task RunAsync() { await Task.Yield(); System.IO.File.Delete(\"path\"); } }")
            .Replace("public Task StartAsync", "private Task? background; public Task StartAsync", StringComparison.Ordinal);

        HostedProducerOperationEntry startup = OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: readiness" };

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source, startup);

        Assert.Equal(owned, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));
    }

    [Theory]
    [InlineData("_ = Task.Run(() => System.IO.File.Delete(\"path\"));", false)]
    [InlineData("_ = Task.Factory.StartNew(() => System.IO.File.Delete(\"path\"));", false)]
    [InlineData("await Task.Run(() => System.IO.File.Delete(\"path\"));", true)]
    [InlineData("var pending = Task.Run(() => System.IO.File.Delete(\"path\")); await pending;", true)]
    [InlineData("var pending = Task.Run(() => System.IO.File.Delete(\"path\")); held.Dispose(); await pending;", false)]
    [InlineData("var pending = Task.Run(() => System.IO.File.Delete(\"path\")); Helper.MayThrow(); await pending;", false)]
    [InlineData("Helper.Detach();", false)]
    [InlineData("await Helper.Join();", true)]
    [InlineData("Action callback = () => System.IO.File.Delete(\"path\"); callback();", true)]
    [InlineData("Action callback = () => System.IO.File.Delete(\"path\"); callback = () => {}; callback();", false)]
    [InlineData("Action callback = Helper.Factory(); callback();", false)]
    [InlineData("Action callback = async () => { await Task.Yield(); System.IO.File.Delete(\"path\"); }; callback();", false)]
    [InlineData("Func<int> callback = () => { System.IO.File.Delete(\"path\"); return 1; }; _ = callback();", true)]
    public void R2CallbacksRequireCompletionOwnership(string body, bool owned)
    {
        string helpers = "static class Helper { public static Action Factory() => () => System.IO.File.Delete(\"path\"); public static void MayThrow() {} public static void Detach() { _ = Task.Run(() => System.IO.File.Delete(\"path\")); } public static Task Join() => Task.Run(() => System.IO.File.Delete(\"path\")); }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + body, helpers));

        Assert.Equal(owned, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        if (owned)
        {
            Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
        }
    }

    [Fact]
    public void AuthoredCallbackParameterRetainsItsExactCallerBodyAndAdmission()
    {
        const string helper = "static class Helper { public static async Task InvokeAsync(Func<Task> action) { await action.Invoke(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "await Helper.InvokeAsync(() => { System.IO.File.Delete(\"path\"); return Task.CompletedTask; });", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void OmittedPrimaryConstructorCallbackRemainsAbsentAcrossHelperCalls()
    {
        const string helper = "static class Factory { public static Sink CreateDefault() => new(null); public static Sink CreateOther(Action callback) => new(callback); } sealed class Sink(Action? callback) { public void Invoke() => Dispatch(); private void Dispatch() => callback?.Invoke(); }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("Factory.CreateDefault().Invoke();", helper));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void ForwardedCallbackRetainsItsOriginalCallableBindings()
    {
        const string helpers = "static class First { public static Task InvokeAsync(Func<Task> action) => Second.InvokeAsync(() => action()); } static class Second { public static async Task InvokeAsync(Func<Task> action) { await action(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "await First.InvokeAsync(() => { System.IO.File.Delete(\"path\"); return Task.CompletedTask; });", helpers));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void ReforwardedBoundCallbackRetainsItsOriginalClosure()
    {
        const string helpers = "static class First { public static Task<T> InvokeAsync<T>(Func<int, Task<T>> action) => InvokeCoreAsync((value, _) => action(value)); private static async Task<T> InvokeCoreAsync<T>(Func<int, int, Task<T>> action) { return await Second.InvokeAsync(token => InvokeUnderBothAsync(action, token)); } private static async Task<T> InvokeUnderBothAsync<T>(Func<int, int, Task<T>> action, int token) { return await action(0, token); } } static class Second { public static async Task<T> InvokeAsync<T>(Func<int, Task<T>> action) { return await action(0); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "_ = await First.InvokeAsync(_ => { System.IO.File.Exists(\"first\"); return Task.FromResult(1); }); _ = await First.InvokeAsync(_ => { System.IO.File.Delete(\"second\"); return Task.FromResult(\"done\"); });", helpers));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void OverloadForwardedCallbackRetainsItsOriginalClosure()
    {
        const string helpers = "static class Retry { public static async Task ExecuteAsync(Func<Task> action, CancellationToken token = default, Func<int, Exception, CancellationToken, ValueTask>? retrying = null) { _ = await ExecuteAsync(async () => { await action(); return true; }, token, retrying); } public static async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken token = default, Func<int, Exception, CancellationToken, ValueTask>? retrying = null) { T result = await action(); if (retrying is not null) await retrying(1, new Exception(), token); return result; } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "await Retry.ExecuteAsync(() => { System.IO.File.Delete(\"path\"); return Task.CompletedTask; });", helpers));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Theory]
    [InlineData("explicit-field")]
    [InlineData("primary-constructor")]
    public void ClosedInstanceDelegateFollowsItsExactConstructorArgument(string shape)
    {
        string helper = shape == "explicit-field"
            ? "internal sealed class Runner { private readonly Action action; internal Runner(Action action) { this.action = action; } internal void Run() => action(); }"
            : "internal sealed class Runner(Action action) { internal void Run() => action(); }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "var runner = new Runner(() => System.IO.File.Delete(\"path\")); runner.Run();", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void ClosedOptionalDelegateFallbackFollowsItsExactMethodGroup()
    {
        const string helper = "internal sealed class Runner { private readonly Action action; internal Runner(Action? action = null) { this.action = action ?? Delete; } internal void Run() => action(); private static void Delete() => System.IO.File.Delete(\"path\"); }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "new Runner().Run();", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void OmittedClosedOptionalDelegateRemainsAbsentThroughMethodAndConstructorForwarding()
    {
        const string helper = "internal static class Reader { internal static void Read(Action? callback = null) => new Cursor(callback).Read(); } internal sealed class Cursor(Action? callback) { internal void Read() => callback?.Invoke(); }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "Reader.Read();", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Theory]
    [InlineData("internal sealed", "private Action action;")]
    [InlineData("public sealed", "private readonly Action action;")]
    public void MutableOrExternallyConstructibleDelegateStorageRemainsUnproven(string accessibility, string field)
    {
        string constructorAccessibility = accessibility.StartsWith("public", StringComparison.Ordinal) ? "public" : "internal";

        string mutation = field.Contains("readonly", StringComparison.Ordinal)
            ? string.Empty
            : " internal void Replace(Action replacement) { action = replacement; }";

        string helper = accessibility + " class Runner { " + field + " " + constructorAccessibility + " Runner(Action action) { this.action = action; } internal void Run() => action();" + mutation + " }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "new Runner(() => System.IO.File.Delete(\"path\")).Run();", helper));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void ClosedDelegateStorageWithDifferentConstructionValuesRemainsUnproven()
    {
        const string helper = "internal sealed class Runner { private readonly Action action; internal Runner(Action action) { this.action = action; } internal void Run() => action(); }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "new Runner(() => System.IO.File.Delete(\"first\")).Run(); new Runner(() => System.IO.File.Delete(\"second\")).Run();", helper));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("tracker.Changed += () => System.IO.File.Delete(\"path\");", false)]
    public void ClosedEventIsAbsentOnlyWithoutAnAuthoredSubscription(string subscription, bool absent)
    {
        const string helper = "internal sealed class Tracker { internal event Action? Changed; internal void Raise() => Changed?.Invoke(); }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "var tracker = new Tracker(); " + subscription + " tracker.Raise();", helper));

        Assert.Equal(absent, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));
    }

    [Fact]
    public void GenericCallbackJoinedThroughConfiguredAwaitRetainsItsCallerLifetime()
    {
        const string helper = "static class Boundary { public static async Task<T> RunAsync<T>(Func<Task<T>> action) { T value = await action().ConfigureAwait(false); return value; } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "_ = await Boundary.RunAsync(() => { System.IO.File.Delete(\"path\"); return Task.FromResult(1); });", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void GenericInterfaceCallbackJoinedThroughConfiguredAwaitRetainsItsCallerLifetime()
    {
        const string helper = "interface IBoundary { Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token); } sealed class Boundary : IBoundary { public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token) { T value = await action(token).ConfigureAwait(false); return value; } }";

        string source = R2Source(R2Admission + "IBoundary boundary = null!; _ = await boundary.RunAsync(_ => { System.IO.File.Delete(\"path\"); return Task.FromResult(1); }, token);", helper)
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); services.AddSingleton<IBoundary, Boundary>();", StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void RepeatedGenericInterfaceCallsRetainEachExactCallbackBinding()
    {
        const string helper = "interface IBoundary { Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token); } sealed class Boundary : IBoundary { public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token) { T value = await action(token).ConfigureAwait(false); return value; } }";

        string source = R2Source(R2Admission + "IBoundary boundary = null!; _ = await boundary.RunAsync(_ => { System.IO.File.Exists(\"first\"); return Task.FromResult(1); }, token); _ = await boundary.RunAsync(_ => { System.IO.File.Delete(\"second\"); return Task.FromResult(\"done\"); }, token);", helper)
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); services.AddSingleton<IBoundary, Boundary>();", StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void KnownSynchronousLinqCallbackRetainsItsCallersAdmission()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "_ = System.Linq.Enumerable.Sum(new[] { 1 }, _ => { System.IO.File.Delete(\"path\"); return 1; });"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void EffectFreeExternalMethodGroupNeedsNoCallbackOwnership()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource("_ = System.Linq.Enumerable.Any(new[] { \"value\" }, string.IsNullOrWhiteSpace);"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void LazySensitiveSelectorRemainsUnprovenUntilConsumptionIsModeled()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource("_ = System.Linq.Enumerable.Select(new[] { 1 }, value => { System.IO.File.Delete(\"path\"); return value; });"));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" && diagnostic.Detail.StartsWith("System.Linq.Enumerable.Select;", StringComparison.Ordinal));
    }

    [Fact]
    public void EagerlyConsumedLazySelectorRetainsItsCallersAdmission()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "_ = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(new[] { 1 }, value => { System.IO.File.Delete(\"path\"); return value; }));"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData("foreach (string file in System.IO.Directory.EnumerateFiles(\"path\")) { _ = file; }", true)]
    [InlineData("string[] files = [.. System.IO.Directory.EnumerateFiles(\"path\")];", true)]
    [InlineData("using System.Collections.Generic.IEnumerator<string> files = System.IO.Directory.EnumerateFiles(\"path\").GetEnumerator(); _ = files.MoveNext();", true)]
    [InlineData("try { using System.Collections.Generic.IEnumerator<string> files = System.IO.Directory.EnumerateFiles(\"path\").GetEnumerator(); _ = files.MoveNext(); } catch (System.IO.IOException) { }", true)]
    [InlineData("var files = System.IO.Directory.EnumerateFiles(\"path\"); lease.Dispose(); foreach (string file in files) { _ = file; }", false)]
    [InlineData("var files = System.IO.Directory.EnumerateFiles(\"path\"); lease.Dispose(); string[] materialized = [.. files];", false)]
    [InlineData("_ = System.IO.Directory.EnumerateFiles(\"path\");", false)]
    public void LazyDirectoryEnumerationMustBeConsumedInsideTheWorkLease(string body, bool retained)
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + body));

        Assert.Equal(retained, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(retained, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("foreach (string file in Helper.Enumerate()) { _ = file; }", true)]
    [InlineData("_ = Helper.Enumerate();", false)]
    [InlineData("var files = Helper.Enumerate(); lease.Dispose(); foreach (string file in files) { _ = file; }", false)]
    public void AuthoredLazySequenceMustBeConsumedInsideTheCallersWorkLease(string body, bool retained)
    {
        const string helper = "static class Helper { internal static System.Collections.Generic.IEnumerable<string> Enumerate() { foreach (string file in System.IO.Directory.EnumerateFiles(\"path\")) yield return file; } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + body, helper));

        Assert.Equal(retained, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(retained, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("_ = nameof(values); foreach (string value in values) _ = value;", true)]
    [InlineData("Saved = values;", false)]
    [InlineData("using var enumerator = values.GetEnumerator(); _ = enumerator.MoveNext();", false)]
    [InlineData("Action deferred = () => { foreach (string value in values) _ = value; }; deferred();", false)]
    public void AuthoredSynchronousConsumerMustEagerlyDrainLazyArgument(
        string consumerBody,
        bool consumed)
    {
        string helper = "static class Helper { private static System.Collections.Generic.IEnumerable<string>? Saved; "
            + "internal static System.Collections.Generic.IEnumerable<string> Enumerate() { System.IO.File.Exists(\"path\"); yield return \"value\"; } "
            + "internal static void Consume(System.Collections.Generic.IEnumerable<string> values) { ArgumentNullException.ThrowIfNull(values); "
            + consumerBody
            + " } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission + "Helper.Consume(Helper.Enumerate());",
                helper));

        Assert.Equal(
            consumed,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Detail.StartsWith(
                    "Helper.Enumerate",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public void UnassignedNullableProductionTestHookIsProvenAbsent()
    {
        string source = "#nullable enable\n" + FixtureSource("Hooks.BeforeForTesting?.Invoke();", "static class Hooks { internal static Action? BeforeForTesting { get; set; } }");

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void UnassignedInstanceProductionTestHookIsProvenAbsent()
    {
        string source = "#nullable enable\n" + FixtureSource("var hooks = new Hooks(); hooks.BeforeForTests?.Invoke();", "sealed class Hooks { internal Action? BeforeForTests { get; init; } }");

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void AssignedInstanceProductionTestHookRemainsUnproven()
    {
        string source = "#nullable enable\n" + FixtureSource("var hooks = new Hooks { BeforeForTests = () => System.IO.File.Delete(\"path\") }; hooks.BeforeForTests?.Invoke();", "sealed class Hooks { internal Action? BeforeForTests { get; init; } }");

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void InitializedInstanceProductionTestHookRemainsUnproven()
    {
        string source = "#nullable enable\n" + FixtureSource("var hooks = new Hooks(); hooks.BeforeForTests?.Invoke();", "sealed class Hooks { internal Action? BeforeForTests { get; } = () => System.IO.File.Delete(\"path\"); }");

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void RefAssignedInstanceProductionTestHookRemainsUnproven()
    {
        const string helpers = "sealed class Hooks { internal Action? BeforeForTests; } static class Installer { internal static void Install(ref Action? hook) => hook = () => System.IO.File.Delete(\"path\"); }";

        string source = "#nullable enable\n" + FixtureSource("var hooks = new Hooks(); Installer.Install(ref hooks.BeforeForTests); hooks.BeforeForTests?.Invoke();", helpers);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void KnownDelegateInspectionDoesNotPretendToExecuteTheDelegate()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "Action cold = () => System.IO.File.Delete(\"path\"); ArgumentNullException.ThrowIfNull(cold);"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.DoesNotContain(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Theory]
    [InlineData("ArgumentNullException.ThrowIfNull(lease);", true)]
    [InlineData("GC.KeepAlive(lease);", false)]
    public void ExactNullInspectionDoesNotEndAdmissionAuthority(
        string inspection,
        bool retained)
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + inspection + " System.IO.File.Exists(\"path\");"));

        Assert.Equal(
            retained,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Exists"));
    }

    [Fact]
    public void NameOfAdmissionHandleDoesNotEndAdmissionAuthority()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission
                + "_ = nameof(lease); System.IO.File.Exists(\"path\");"));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Exists");
    }

    [Fact]
    public void AuthoredHelperOnSensitiveTypeIsTraversedInsteadOfClassifiedAsOpaque()
    {
        const string helper = "namespace RetroDownfall.Arcanum.Infrastructure.Data { static class DataRetentionService { public static void Helper() { System.IO.File.Exists(\"path\"); } } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.Helper();", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");
    }

    [Fact]
    public void OmittedOptionalCallbackIsProvenAbsent()
    {
        const string helper = "static class Helper { public static async Task InvokeAsync(Func<Task> action, Func<Task>? retrying = null) { await action(); if (retrying is not null) await retrying(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "await Helper.InvokeAsync(() => { System.IO.File.Delete(\"path\"); return Task.CompletedTask; });", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void ProvenAbsentClosedPropertyStaysAbsentWhenForwardedToAHelper()
    {
        const string helper = "public sealed class Runner { internal Func<Task>? Hook { get; init; } public async Task RunAsync() { await Helper.InvokeAsync(Hook); } } static class Helper { internal static async Task InvokeAsync(Func<Task>? callback) { if (callback is not null) await callback(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "await new Runner().RunAsync();", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void ProvenAbsentClosedPropertyStaysAbsentThroughAPatternLocal()
    {
        const string helper = "internal sealed class Runner { internal Func<Task>? Hook { get; set; } internal async Task RunAsync() { if (Hook is { } callback) await callback(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "await new Runner().RunAsync();", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void SuppliedUnresolvedOptionalCallbackRemainsUnproven()
    {
        const string helper = "static class Helper { public static async Task InvokeAsync(Func<Task>? retrying = null) { if (retrying is not null) await retrying(); } } static class Hooks { public static Func<Task> Callback { get; } = () => Task.CompletedTask; }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "await Helper.InvokeAsync(Hooks.Callback);", helper));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void NullableOutAdmissionAliasRetainsTheExactWorkFrontier()
    {
        const string body = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease? admitted)) return; using RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease = admitted!; System.IO.File.Exists(\"path\");";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");
    }

    [Theory]
    [InlineData("new Helper().Value = 1;", false)]
    [InlineData("new Helper().Value = 1;", true)]
    [InlineData("_ = new Helper { Initial = 1 };", true)]
    [InlineData("using (new Helper()) { }", true)]
    [InlineData("using (new Helper()) { held.Dispose(); }", false)]
    [InlineData("await using (new Helper()) { }", true)]
    [InlineData("await using (new Helper()) { held.Dispose(); }", false)]
    public void R2ExecutedAccessorsAndExpressionDisposalAreInventoried(string body, bool protectedSite)
    {
        string helpers = "class Helper : IDisposable, IAsyncDisposable { public int Value { get { System.IO.File.Exists(\"read\"); return 0; } set { System.IO.File.Delete(\"write\"); } } public int Initial { init { System.IO.File.Delete(\"init\"); } } public void Dispose() { System.IO.File.Delete(\"dispose\"); } public ValueTask DisposeAsync() { System.IO.File.Delete(\"async\"); return ValueTask.CompletedTask; } }";

        string admission = protectedSite || body.Contains("held.Dispose", StringComparison.Ordinal) ? R2Admission : "";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(admission + body, helpers));

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Equal(protectedSite, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Fact]
    public void R2LogicalNegationReadsButDoesNotSetAProperty()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "_ = !new Helper().Flag;", "class Helper { public bool Flag { get => System.IO.File.Exists(\"read\"); set => System.IO.File.Delete(\"write\"); } }"));

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.DoesNotContain(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    private const string R2Blobs = "namespace RetroDownfall.Arcanum.Core.Storage { public sealed class EncryptedBlobDescriptor {} public interface IEncryptedBlobStore { EncryptedBlobWriter CreateWriterAsync(); } public class EncryptedBlobWriter : IDisposable { public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor()); public void Dispose() {} } }";

    private static string R6BlobTypes(string completion, string disposal = "ValueTask")
    {
        string completionBody = completion switch
        {
            "void" => "public void CompleteAsync() {} public void CompleteAsync(bool unused) {}",
            "ValueTask" => "public ValueTask<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => ValueTask.FromResult(new EncryptedBlobDescriptor()); public ValueTask<EncryptedBlobDescriptor> CompleteAsync(bool unused) => ValueTask.FromResult(new EncryptedBlobDescriptor());",
            _ => "public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor()); public Task<EncryptedBlobDescriptor> CompleteAsync(bool unused) => Task.FromResult(new EncryptedBlobDescriptor());",
        };

        string disposalBody = disposal == "Task"
            ? "public Task WriteAsync() => Task.CompletedTask; public void Dispose() {} public Task DisposeAsync() => Task.CompletedTask;"
            : "public Task WriteAsync() => Task.CompletedTask; public void Dispose() {} public ValueTask DisposeAsync() => ValueTask.CompletedTask;";

        return "namespace RetroDownfall.Arcanum.Core.Storage { public sealed class EncryptedBlobDescriptor {} public interface IEncryptedBlobStore { EncryptedBlobWriter CreateWriterAsync(); } public class EncryptedBlobWriter : IDisposable" + (disposal == "ValueTask" ? ", IAsyncDisposable" : "") + " { " + completionBody + disposalBody + " } }";
    }

    [Theory]
    [InlineData("straight", "void", "ValueTask", false)]
    [InlineData("await-task", "Task", "ValueTask", true)]
    [InlineData("wrong-value-task-completion", "ValueTask", "ValueTask", false)]
    [InlineData("completion-configure-await", "Task", "ValueTask", true)]
    [InlineData("using", "Task", "ValueTask", true)]
    [InlineData("await-using", "Task", "ValueTask", true)]
    [InlineData("try-finally-task", "Task", "ValueTask", true)]
    [InlineData("try-finally-dispose-task", "Task", "Task", false)]
    [InlineData("try-finally-dispose-value-task", "Task", "ValueTask", true)]
    [InlineData("catch-cleanup-rethrow", "Task", "ValueTask", true)]
    [InlineData("catch-cleanup-fallthrough", "Task", "ValueTask", false)]
    [InlineData("conditional-completion", "Task", "ValueTask", false)]
    [InlineData("loop-before-completion", "Task", "ValueTask", false)]
    [InlineData("unreachable-completion", "Task", "ValueTask", false)]
    [InlineData("dispose-before-completion", "Task", "ValueTask", false)]
    [InlineData("discarded-completion-task", "Task", "ValueTask", false)]
    [InlineData("discarded-completion-value-task", "ValueTask", "ValueTask", false)]
    [InlineData("discarded-disposal-task", "Task", "Task", false)]
    [InlineData("discarded-disposal-value-task", "Task", "ValueTask", false)]
    [InlineData("duplicate-completion", "Task", "ValueTask", false)]
    [InlineData("duplicate-disposal", "Task", "ValueTask", false)]
    [InlineData("wrong-completion-overload", "Task", "ValueTask", false)]
    [InlineData("explicit-completion-argument", "Task", "ValueTask", false)]
    [InlineData("wrong-disposal-overload", "Task", "ValueTask", false)]
    [InlineData("completion-after-group-loss", "Task", "ValueTask", false)]
    [InlineData("disposal-after-group-loss", "Task", "ValueTask", false)]
    [InlineData("cross-writer-completion", "Task", "ValueTask", false)]
    [InlineData("cross-writer-disposal", "Task", "ValueTask", false)]
    [InlineData("conditional-create", "Task", "ValueTask", false)]
    [InlineData("unsupported-create-path", "Task", "ValueTask", false)]
    public void R6LocalWriterPublicationRequiresOneExactSupportedLifetime(string shape, string completion, string disposal, bool valid)
    {
        string one = shape switch
        {
            "straight" => "var writer=store.CreateWriterAsync(); writer.CompleteAsync(); writer.Dispose();",
            "await-task" or "wrong-value-task-completion" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync();",
            "completion-configure-await" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync().ConfigureAwait(false);",
            "using" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync();",
            "await-using" => "await using var writer=store.CreateWriterAsync(); await writer.CompleteAsync();",
            "try-finally-dispose-task" or "try-finally-dispose-value-task" => "var writer=store.CreateWriterAsync(); try { await writer.CompleteAsync(); } finally { await writer.DisposeAsync(); }",
            "catch-cleanup-rethrow" => "var writer=store.CreateWriterAsync(); try { await writer.CompleteAsync(); } catch { await writer.DisposeAsync(); throw; } writer.Dispose();",
            "catch-cleanup-fallthrough" => "var writer=store.CreateWriterAsync(); try { await writer.CompleteAsync(); } catch { await writer.DisposeAsync(); } writer.Dispose();",
            "conditional-completion" => "using var writer=store.CreateWriterAsync(); if (DateTime.UtcNow.Ticks<0) await writer.CompleteAsync();",
            "loop-before-completion" => "using var writer=store.CreateWriterAsync(); while (DateTime.UtcNow.Ticks<0) { } await writer.CompleteAsync();",
            "unreachable-completion" => "using var writer=store.CreateWriterAsync(); return; await writer.CompleteAsync();",
            "dispose-before-completion" => "var writer=store.CreateWriterAsync(); writer.Dispose(); await writer.CompleteAsync();",
            "discarded-completion-task" or "discarded-completion-value-task" => "using var writer=store.CreateWriterAsync(); _=writer.CompleteAsync();",
            "discarded-disposal-task" or "discarded-disposal-value-task" => "var writer=store.CreateWriterAsync(); await writer.CompleteAsync(); _=writer.DisposeAsync();",
            "duplicate-completion" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync(); await writer.CompleteAsync();",
            "duplicate-disposal" => "var writer=store.CreateWriterAsync(); await writer.CompleteAsync(); writer.Dispose(); writer.Dispose();",
            "wrong-completion-overload" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync(true);",
            "explicit-completion-argument" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync(CancellationToken.None);",
            "wrong-disposal-overload" => "var writer=store.CreateWriterAsync(); await writer.CompleteAsync(); writer.Dispose(true);",
            "completion-after-group-loss" => "using var writer=store.CreateWriterAsync(); held.Dispose(); await writer.CompleteAsync();",
            "disposal-after-group-loss" => "var writer=store.CreateWriterAsync(); await writer.CompleteAsync(); held.Dispose(); writer.Dispose();",
            "conditional-create" => "using var writer=DateTime.UtcNow.Ticks<0 ? store.CreateWriterAsync() : store.CreateWriterAsync(); await writer.CompleteAsync();",
            "unsupported-create-path" => "if (DateTime.UtcNow.Ticks<0) { using var writer=store.CreateWriterAsync(); await writer.CompleteAsync(); }",
            _ => "",
        };

        if (shape == "try-finally-task")
        {
            one = "var first=store.CreateWriterAsync(); try { var second=store.CreateWriterAsync(); try { await first.CompleteAsync(); await second.CompleteAsync(); } finally { await second.DisposeAsync(); } } finally { await first.DisposeAsync(); }";
        }
        else if (shape == "cross-writer-completion")
        {
            one = "var first=store.CreateWriterAsync(); var second=store.CreateWriterAsync(); await first.CompleteAsync(); await first.CompleteAsync(); first.Dispose(); second.Dispose();";
        }
        else if (shape == "cross-writer-disposal")
        {
            one = "var first=store.CreateWriterAsync(); try { var second=store.CreateWriterAsync(); try { await first.CompleteAsync(); await second.CompleteAsync(); } finally { first.Dispose(); } } finally { first.Dispose(); }";
        }

        string helpers = R6BlobTypes(completion, disposal);

        if (shape == "wrong-disposal-overload")
        {
            helpers = helpers.Replace("public void Dispose() {}", "public void Dispose() {} public void Dispose(bool unused) {}", StringComparison.Ordinal);
        }

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; " + one, helpers));

        Assert.Equal(valid, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData("exact", "sync", true)]
    [InlineData("exact", "async", true)]
    [InlineData("optional-overload", "sync", false)]
    [InlineData("wrong-task-result", "sync", false)]
    [InlineData("custom-awaitable", "sync", false)]
    [InlineData("exact", "optional-overload", false)]
    [InlineData("exact", "custom-awaitable", false)]
    public void R7WriterTerminalsUseClosedProductionContracts(string completion, string cleanup, bool valid)
    {
        string completionMember = completion switch
        {
            "optional-overload" => "public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default, bool unrelated=false) => Task.FromResult(new EncryptedBlobDescriptor());",
            "wrong-task-result" => "public Task<int> CompleteAsync(CancellationToken token=default) => Task.FromResult(0);",
            "custom-awaitable" => "public CustomAwaitable CompleteAsync(CancellationToken token=default) => new();",
            _ => "public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor());",
        };

        string interfaces = cleanup == "async" ? ", IAsyncDisposable" : "";

        string cleanupMember = cleanup switch
        {
            "async" => "public ValueTask DisposeAsync() => ValueTask.CompletedTask;",
            "optional-overload" => "public ValueTask DisposeAsync(bool unrelated=false) => ValueTask.CompletedTask;",
            "custom-awaitable" => "public CustomAwaitable DisposeAsync() => new();",
            _ => "",
        };

        string helpers = "namespace RetroDownfall.Arcanum.Core.Storage { public sealed class EncryptedBlobDescriptor {} public sealed class CustomAwaitable { public Awaiter GetAwaiter() => new(); public sealed class Awaiter : System.Runtime.CompilerServices.INotifyCompletion { public bool IsCompleted => true; public void OnCompleted(Action continuation) {} public void GetResult() {} } } public interface IEncryptedBlobStore { EncryptedBlobWriter CreateWriterAsync(); } public class EncryptedBlobWriter : IDisposable" + interfaces + " { " + completionMember + " public void Dispose() {} " + cleanupMember + " } }";

        string body = cleanup switch
        {
            "sync" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync();",
            "async" => "await using var writer=store.CreateWriterAsync(); await writer.CompleteAsync();",
            _ => "var writer=store.CreateWriterAsync(); try { await writer.CompleteAsync(); } finally { await writer.DisposeAsync(); }",
        };

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; " + body, helpers));

        Assert.Equal(valid, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData("production-sync", true)]
    [InlineData("production-async", true)]
    [InlineData("optional-completion-overload", false)]
    [InlineData("wrong-completion-result", false)]
    [InlineData("custom-completion-awaitable", false)]
    [InlineData("wrong-cleanup", false)]
    [InlineData("discarded-completion", false)]
    [InlineData("explicit-none-completion", true)]
    [InlineData("cancelable-completion", true)]
    public void R8CarrierFieldsUseTheSameExactCompletionJoinAndStreamCleanupContracts(string shape, bool valid)
    {
        string completion = shape switch
        {
            "optional-completion-overload" => "public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default, bool unrelated=false) => Task.FromResult(new EncryptedBlobDescriptor());",
            "wrong-completion-result" => "public Task<int> CompleteAsync(CancellationToken token=default) => Task.FromResult(0);",
            "custom-completion-awaitable" => "public CustomAwaitable CompleteAsync(CancellationToken token=default) => new();",
            _ => "public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor());",
        };

        bool inheritedStreamCleanup = shape is "production-sync" or "production-async" or "discarded-completion" or "explicit-none-completion" or "cancelable-completion";

        string writerBase = inheritedStreamCleanup ? " : System.IO.MemoryStream" : "";

        string writerCleanup = shape == "wrong-cleanup" ? "public Task DisposeAsync() => Task.CompletedTask; public void Dispose() {}" : inheritedStreamCleanup ? "" : "public void Dispose() {}";

        string completionCalls = shape switch
        {
            "discarded-completion" => "_ = first.CompleteAsync(); _ = second.CompleteAsync();",
            "explicit-none-completion" => "await first.CompleteAsync(CancellationToken.None); await second.CompleteAsync(CancellationToken.None);",
            "cancelable-completion" => "var token=new CancellationToken(true); await first.CompleteAsync(token); await second.CompleteAsync(token);",
            _ => "await first.CompleteAsync(); await second.CompleteAsync();",
        };

        bool asynchronousCleanup = shape is "production-async" or "wrong-cleanup";

        string carrierContract = asynchronousCleanup ? "IAsyncDisposable" : "IDisposable";

        string carrierCleanup = asynchronousCleanup
            ? "public async ValueTask DisposeAsync() { await first.DisposeAsync(); await second.DisposeAsync(); }"
            : "public void Dispose() { first.Dispose(); second.Dispose(); }";

        string caller = asynchronousCleanup
            ? "await using var writers=Carrier.Create(store); await writers.CompleteAsync();"
            : "using var writers=Carrier.Create(store); await writers.CompleteAsync();";

        string helpers = "namespace RetroDownfall.Arcanum.Core.Storage { public sealed class EncryptedBlobDescriptor {} public sealed class CustomAwaitable { public Awaiter GetAwaiter() => new(); public sealed class Awaiter : System.Runtime.CompilerServices.INotifyCompletion { public bool IsCompleted => true; public void OnCompleted(Action continuation) {} public void GetResult() {} } } public interface IEncryptedBlobStore { EncryptedBlobWriter CreateWriterAsync(); } public class EncryptedBlobWriter" + writerBase + " { " + completion + writerCleanup + " } } class Carrier : " + carrierContract + " { private readonly RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter first,second; private Carrier(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter a, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter b) { first=a; second=b; } " + R4SafeCarrierFactory() + " public async Task CompleteAsync() { " + completionCalls + " } " + carrierCleanup + " }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; " + caller, helpers));

        Assert.Equal(valid, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("missing-factory-cleanup", false)]
    [InlineData("missing-carrier-cleanup", false)]
    [InlineData("escaped-before-transfer", false)]
    [InlineData("cancelable-carrier-completion", false)]
    [InlineData("conditional-carrier-completion", false)]
    [InlineData("completion-after-throwing-loop", true)]
    public void R9CarrierProofAcceptsExceptionSafePartialConstructionAndIdempotentCleanup(
        string shape,
        bool valid)
    {
        string factoryCleanup = shape == "missing-factory-cleanup"
            ? ""
            : "if (second is not null) { try { await second.DisposeAsync(); } catch (Exception failure) { failures ??= new(); failures.Add(failure); } }";

        string carrierCleanup = shape == "missing-carrier-cleanup"
            ? ""
            : "try { await _second.DisposeAsync(); } catch (Exception failure) { failures ??= new(); failures.Add(failure); }";

        string escape = shape == "escaped-before-transfer"
            ? "GC.KeepAlive(first);"
            : "";

        string helpers = """
            namespace RetroDownfall.Arcanum.Core.Storage
            {
                public sealed class EncryptedBlobDescriptor {}

                public interface IEncryptedBlobStore
                {
                    Task<EncryptedBlobWriter> CreateWriterAsync();
                }

                public sealed class EncryptedBlobWriter : System.IO.MemoryStream, IAsyncDisposable
                {
                    public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token = default) =>
                        Task.FromResult(new EncryptedBlobDescriptor());

                    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
                }
            }

            sealed class Carrier : IAsyncDisposable
            {
                private readonly RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter _first;

                private readonly RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter _second;

                private readonly System.IO.StreamWriter _firstText;

                private readonly System.IO.StreamWriter _secondText;

                private int _disposeStarted;

                private Carrier(
                    RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter first,
                    RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter second,
                    System.IO.StreamWriter firstText,
                    System.IO.StreamWriter secondText)
                {
                    _first = first;
                    _second = second;
                    _firstText = firstText;
                    _secondText = secondText;
                }

                public static async Task<Carrier> CreateAsync(
                    RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store)
                {
                    RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter? first = null;
                    RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter? second = null;
                    System.IO.StreamWriter? firstText = null;
                    System.IO.StreamWriter? secondText = null;

                    try
                    {
                        first = await store.CreateWriterAsync();
                        second = await store.CreateWriterAsync();
                        firstText = Text(first);
                        secondText = Text(second);
                        ESCAPE

                        return new Carrier(first, second, firstText, secondText);
                    }
                    catch
                    {
                        System.Collections.Generic.List<Exception>? failures = null;

                        if (secondText is not null)
                        {
                            try { await secondText.DisposeAsync(); } catch (Exception failure) { failures ??= new(); failures.Add(failure); }
                        }

                        if (firstText is not null)
                        {
                            try { await firstText.DisposeAsync(); } catch (Exception failure) { failures ??= new(); failures.Add(failure); }
                        }

                        FACTORY_CLEANUP

                        if (first is not null)
                        {
                            try { await first.DisposeAsync(); } catch (Exception failure) { failures ??= new(); failures.Add(failure); }
                        }

                        throw;
                    }
                }

                private static System.IO.StreamWriter Text(
                    RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) =>
                    new(writer, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true);

                public async Task CompleteAsync(CancellationToken cancellationToken)
                {
                    await _firstText.DisposeAsync();
                    await _secondText.DisposeAsync();
                    await _first.CompleteAsync();
                    await _second.CompleteAsync();
                }

                public async ValueTask DisposeAsync()
                {
                    if (System.Threading.Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                    {
                        return;
                    }

                    System.Collections.Generic.List<Exception>? failures = null;

                    try { await _first.DisposeAsync(); } catch (Exception failure) { failures ??= new(); failures.Add(failure); }
                    CARRIER_CLEANUP
                }
            }
            """
            .Replace("ESCAPE", escape, StringComparison.Ordinal)
            .Replace("FACTORY_CLEANUP", factoryCleanup, StringComparison.Ordinal)
            .Replace("CARRIER_CLEANUP", carrierCleanup, StringComparison.Ordinal);

        string completion = shape switch
        {
            "cancelable-carrier-completion" =>
                "var completionToken = new CancellationToken(true); await writers.CompleteAsync(completionToken);",
            "conditional-carrier-completion" =>
                "if (DateTime.UtcNow.Ticks < 0) { await writers.CompleteAsync(CancellationToken.None); }",
            "completion-after-throwing-loop" =>
                "while (DateTime.UtcNow.Ticks < 0) { if (DateTime.UtcNow.Ticks < -1) throw new Exception(); break; } await writers.CompleteAsync(CancellationToken.None);",
            _ => "await writers.CompleteAsync(CancellationToken.None);",
        };

        string caller = R2Admission
            + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store = null!; "
            + "await using var writers = await Carrier.CreateAsync(store); "
            + completion;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(caller, helpers));

        bool proven = !result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE");

        Assert.True(
            proven == valid,
            string.Join(
                global::System.Environment.NewLine,
                result.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Identity}: {diagnostic.Detail}")));
    }

    [Theory]
    [InlineData("authored-store")]
    [InlineData("authored-dispose")]
    [InlineData("opaque")]
    [InlineData("alias")]
    [InlineData("field")]
    [InlineData("callback")]
    [InlineData("return")]
    [InlineData("local-function")]
    [InlineData("lambda")]
    [InlineData("detached-write")]
    public void R6LocalWriterPublicationRejectsEveryOwnershipEscape(string shape)
    {
        string use = shape switch
        {
            "authored-store" => "WriterSink.Store(writer);",
            "authored-dispose" => "WriterSink.Dispose(writer);",
            "opaque" => "GC.KeepAlive(writer);",
            "alias" => "var alias=writer; GC.KeepAlive(alias);",
            "field" => "WriterSink.Saved=writer;",
            "callback" => "Action callback=()=>GC.KeepAlive(writer); GC.KeepAlive(callback);",
            "local-function" => "void CompleteLater() => writer.CompleteAsync(); GC.KeepAlive((Action)CompleteLater);",
            "lambda" => "Action completeLater=()=>writer.CompleteAsync(); GC.KeepAlive(completeLater);",
            "detached-write" => "_=writer.WriteAsync();",
            _ => "_ = WriterSink.Return(writer);",
        };

        string helpers = R6BlobTypes("Task") + " static class WriterSink { public static RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter Saved=null!; public static void Store(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) { Saved=writer; } public static void Dispose(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) => writer.Dispose(); public static RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter Return(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) => writer; }";

        string body = R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; using var writer=store.CreateWriterAsync(); " + use + " await writer.CompleteAsync();";

        Assert.Contains(R2Discover(R2Source(body, helpers)).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE");
    }

    [Theory]
    [InlineData("Task", true)]
    [InlineData("ValueTask", true)]
    [InlineData("Task", false)]
    [InlineData("ValueTask", false)]
    public void R6WriterCreationMustCompleteThroughAnExactAwaitBeforeOwnershipStarts(string awaitable, bool joined)
    {
        string completed = awaitable == "Task" ? "Task.FromResult(new EncryptedBlobWriter())" : "ValueTask.FromResult(new EncryptedBlobWriter())";

        string helpers = "namespace RetroDownfall.Arcanum.Core.Storage { public sealed class EncryptedBlobDescriptor {} public interface IEncryptedBlobStore { " + awaitable + "<EncryptedBlobWriter> CreateWriterAsync(); } public class Store : IEncryptedBlobStore { public " + awaitable + "<EncryptedBlobWriter> CreateWriterAsync() => " + completed + "; } public class EncryptedBlobWriter : IDisposable { public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor()); public void Dispose() {} } }";

        string creation = joined
            ? "await store.CreateWriterAsync().ConfigureAwait(false)"
            : "store.CreateWriterAsync().GetAwaiter().GetResult()";

        string body = R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=new RetroDownfall.Arcanum.Core.Storage.Store(); using var writer=" + creation + "; await writer.CompleteAsync();";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body, helpers));

        Assert.Equal(joined, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Fact]
    public void R2UnknownExpressionDisposalFailsClosed()
    {
        Assert.Contains(R2Discover(R2Source(R2Admission + "IDisposable unknown = null!; using (unknown) { } ")).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
    }

    [Fact]
    public void R2OutDelegateIsNotExecutedByItsProducer()
    {
        Assert.DoesNotContain(R2Discover(R2Source(R2Admission + "var callbacks = new System.Collections.Concurrent.ConcurrentDictionary<string, Action>(); _ = callbacks.TryGetValue(\"key\", out var unused);")).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Theory]
    [InlineData("await Task.Run(() => { held.Dispose(); System.IO.File.Delete(\"path\"); });")]
    [InlineData("await Task.Run(() => { using (group) { } System.IO.File.Delete(\"path\"); });")]
    public void R2CapturedAdmissionDisposalEndsInheritedAuthority(string body)
    {
        Assert.Contains(R2Discover(R2Source(R2Admission + body)).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Fact]
    public void R2DiscardedAsyncHelperCannotInheritCallerLifetime()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "_ = Helper.Async();", "static class Helper { public static async Task Async() { await Task.Yield(); System.IO.File.Delete(\"path\"); } }"));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData("_ = Helper.Launch(runner);", false)]
    [InlineData("await Helper.Launch(runner);", true)]
    public void NonAsyncAwaitableHelperRequiresAnExactCallerJoin(string call, bool joined)
    {
        const string helpers = "namespace RetroDownfall.Arcanum.Infrastructure.Daemons { public interface IDaemonRunner { Task RunScheduledAsync(); } } static class Helper { public static Task Launch(RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonRunner runner) => runner.RunScheduledAsync(); }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonRunner runner = null!; " + call, helpers));

        Assert.Equal(joined, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(joined, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("_ = runner.RunScheduledAsync();", false)]
    [InlineData("await runner.RunScheduledAsync();", true)]
    public void AwaitableSensitiveBoundaryRequiresAnExactCallerJoin(string call, bool joined)
    {
        const string helpers = "namespace RetroDownfall.Arcanum.Infrastructure.Daemons { public interface IDaemonRunner { Task RunScheduledAsync(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonRunner runner = null!; " + call, helpers));

        Assert.Equal(joined, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(joined, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("action().GetAwaiter().GetResult();", true)]
    [InlineData("Task<bool> pending = action(); pending.GetAwaiter().GetResult();", true)]
    [InlineData("_ = action().GetAwaiter();", false)]
    [InlineData("_ = action().IsCompleted;", false)]
    [InlineData("_ = action();", false)]
    [InlineData("_ = action().ContinueWith(_ => { });", false)]
    [InlineData("Task<bool> pending = action(); MayThrow(); pending.GetAwaiter().GetResult();", false)]
    public void SynchronousCallbackCompletionRequiresExactGetAwaiterGetResultChain(
        string invocation,
        bool joined)
    {
        string helpers = "static class Helper { public static void Invoke(Func<Task<bool>> action) { "
            + invocation
            + " } private static void MayThrow() { } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + "Helper.Invoke(() => { System.IO.File.Delete(\"path\"); return Task.FromResult(true); });",
                helpers));

        Assert.Equal(
            joined,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(
            joined,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("closed", true)]
    [InlineData("throw-before-assignment", true)]
    [InlineData("return-before-assignment", false)]
    [InlineData("opaque-constructor", false)]
    [InlineData("nonreadonly-constructor-only", true)]
    [InlineData("later-write", false)]
    public void ReadonlyDelegateFieldFollowsEveryClosedConstructorTarget(
        string shape,
        bool resolved)
    {
        string field = shape is "later-write" or "nonreadonly-constructor-only"
            ? "private Func<Task> callback;"
            : "private readonly Func<Task> callback;";

        string extra = shape switch
        {
            "opaque-constructor" =>
                "public static Runner CreateUnknown(Func<Task> unknown) => new(unknown);",
            "later-write" =>
                "public void Replace(Func<Task> replacement) { callback = replacement; }",
            _ =>
                "public static Runner CreateOther() => new(() => { System.IO.File.Exists(\"other\"); return Task.CompletedTask; });",
        };

        string constructorGuard = shape switch
        {
            "throw-before-assignment" =>
                "if (callback is null) throw new ArgumentNullException(nameof(callback)); ",
            "return-before-assignment" =>
                "if (DateTime.UtcNow.Ticks < 0) return; ",
            _ =>
                "",
        };

        string helpers = "sealed class Runner { "
            + field
            + " public Runner(Func<Task> callback) { "
            + constructorGuard
            + "this.callback = callback; } "
            + "public Task RunAsync() => callback(); "
            + extra
            + " }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + "var runner = new Runner(() => { System.IO.File.Delete(\"path\"); return Task.CompletedTask; }); await runner.RunAsync();",
                helpers));

        Assert.Equal(
            resolved,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(
            resolved,
            result.Items.Any(static site => site.Callee == "System.IO.File.Delete"));

        Assert.Equal(
            shape is "closed" or "throw-before-assignment" or "nonreadonly-constructor-only",
            result.Items.Any(static site => site.Callee == "System.IO.File.Exists"));
    }

    [Fact]
    public void ForwardedOptionalCallbackFallbackPreservesItsClosedMethodGroup()
    {
        const string helper = "internal static class Limits { internal static void Scrub(Func<string, string?>? reader = null) => Build(reader); private static void Build(Func<string, string?>? supplied = null) { Func<string, string?> exact = supplied ?? Environment.GetEnvironmentVariable; _ = exact(\"name\"); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "Limits.Scrub();", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void ReachableOptionalCallbackBindingIsNotContaminatedByAnUncalledForwarder()
    {
        const string helper = "internal static class Limits { internal static void Scrub(Func<string, string?>? reader = null) => Build(reader); private static void Build(Func<string, string?>? supplied = null) { Func<string, string?> exact = supplied ?? Environment.GetEnvironmentVariable; _ = exact(\"name\"); } internal static void Uncalled(Func<string, string?> unknown) => Build(unknown); }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "Limits.Scrub();", helper));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void ReturnedValueTaskAsTaskPreservesCallbackCompletionOwnership()
    {
        const string helper = "static class Helper { public static async Task InvokeAsync(Func<Task> action) { await action(); } public static async ValueTask ApplyAsync() { await Task.Yield(); System.IO.File.Delete(\"path\"); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + "await Helper.InvokeAsync(() => Helper.ApplyAsync().AsTask());",
                helper));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void ReturnedTaskRunPreservesItsCapturedCallableBindingThroughSynchronousCompletion()
    {
        const string helper = "static class Boundary { public static Task RunAsync(Func<Task> operation) => Task.Run(() => RunOwned(operation)); private static void RunOwned(Func<Task> operation) => operation().GetAwaiter().GetResult(); }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + "await Boundary.RunAsync(() => { System.IO.File.Delete(\"path\"); return Task.CompletedTask; });",
                helper));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void CollectionSpreadEagerlyConsumesAnAuthoredLazyPredicate()
    {
        const string helpers = "static class Helper { public static bool Exists(string path) => System.IO.File.Exists(path); public static void Write(System.Collections.Generic.IReadOnlyList<string> values) { } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + "var paths = new[] { \"path\" }; Helper.Write([.. System.Linq.Enumerable.Where(paths, Helper.Exists)]);",
                helpers));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");
    }

    [Fact]
    public void ListAddRangeEagerlyConsumesAnAuthoredLazyPredicate()
    {
        const string helpers = "static class Helper { public static bool Exists(string path) => System.IO.File.Exists(path); }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + "var paths = new[] { \"path\" }; var retained = new System.Collections.Generic.List<string>(); retained.AddRange(System.Linq.Enumerable.Where(paths, Helper.Exists));",
                helpers));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");
    }

    [Theory]
    [InlineData("try { await pending; } catch (Exception) { }", true)]
    [InlineData("if (DateTime.UtcNow.Ticks < 0) await pending;", false)]
    [InlineData("_ = pending.IsCompleted;", false)]
    [InlineData("return;", false)]
    public void TaskLocalMayTransferToOneAwaitedExactJoinHelper(
        string joinBody,
        bool owned)
    {
        string helper = "static class Helper { public static async Task ProduceAsync() { await Task.Yield(); System.IO.File.Delete(\"path\"); } public static async Task JoinAsync(Task pending) { "
            + joinBody
            + " } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + "Task pending = Helper.ProduceAsync(); try { await Task.Yield(); } catch (Exception) { } await Helper.JoinAsync(pending);",
                helper));

        Assert.Equal(
            owned,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(
            owned,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("await Task.WhenAll(first, second);", true)]
    [InlineData("await Task.WhenAny(first, second);", false)]
    [InlineData("_ = Task.WhenAll(first, second);", false)]
    public void TaskWhenAllIsAnExactJoinForEveryInputTask(
        string join,
        bool owned)
    {
        const string helper = "static class Helper { public static async Task FirstAsync() { await Task.Yield(); System.IO.File.Delete(\"first\"); } public static async Task SecondAsync() { await Task.Yield(); System.IO.File.Delete(\"second\"); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + "Task first = Helper.FirstAsync(); Task second = Helper.SecondAsync(); "
                    + join,
                helper));

        Assert.Equal(
            owned,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(
            owned,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("await Task.WhenAll(first, second);", true)]
    [InlineData("await Task.WhenAll(first);", false)]
    public void TaskWhenAllMayBeFollowedByResultConsumptionWithoutLosingItsExactJoin(
        string join,
        bool owned)
    {
        const string helper = "static class Helper { public static async Task<string> FirstAsync() { await Task.Yield(); System.IO.File.Delete(\"first\"); return \"first\"; } public static async Task<string> SecondAsync() { await Task.Yield(); System.IO.File.Delete(\"second\"); return \"second\"; } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + "Task<string> first = Helper.FirstAsync(); Task<string> second = Helper.SecondAsync(); "
                    + join
                    + " string firstResult = await first; string secondResult = await second;",
                helper));

        Assert.Equal(
            owned,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(
            owned,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("try { await Task.WhenAll(first, second); } catch (Exception) { }", true)]
    [InlineData("try { await Task.WhenAll(first); } catch (Exception) { }", false)]
    public void TaskWhenAllRetainsItsExactJoinWhenTheObservedAggregateIsCaught(
        string join,
        bool owned)
    {
        const string helper = "static class Helper { public static async Task<string> FirstAsync() { await Task.Yield(); System.IO.File.Delete(\"first\"); return \"first\"; } public static async Task<string> SecondAsync() { await Task.Yield(); System.IO.File.Delete(\"second\"); return \"second\"; } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + "Task<string> first = Helper.FirstAsync(); Task<string> second = Helper.SecondAsync(); "
                    + join
                    + " string firstResult = await first; string secondResult = await second;",
                helper));

        Assert.Equal(
            owned,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(
            owned,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("return pending.WaitAsync(token);", true)]
    [InlineData("return pending;", true)]
    [InlineData("return Task.CompletedTask;", false)]
    [InlineData("_ = pending.IsCompleted; return pending;", false)]
    public void LifecycleMayReturnItsSingleLocalTaskWithoutLosingCompletion(
        string completion,
        bool owned)
    {
        const string helper = "static class Helper { public static async Task ApplyAsync() { await Task.Yield(); System.IO.File.Delete(\"path\"); } }";

        string body = AcquireWork
            + " if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group; Task pending = Helper.ApplyAsync(); "
            + completion;

        string source = RegistrationSource("services.AddHostedService<Worker>();")
            .Replace(
                "public Task StartAsync(CancellationToken token) => Task.CompletedTask;",
                "public Task StartAsync(CancellationToken token) { " + body + " }",
                StringComparison.Ordinal)
            + AdmissionTypes
            + helper;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Equal(
            owned,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(
            owned,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Fact]
    public void ExactlyAssignedTaskLocalMayBePublishedAndThenAwaited()
    {
        const string helper = "static class Helper { public static async Task ApplyAsync() { await Task.Yield(); System.IO.File.Delete(\"path\"); } }";

        string source = R2Source(
                R2Admission
                    + "Task pending; pending = Helper.ApplyAsync(); background = pending; await pending;")
            .Replace(
                "public Task StopAsync",
                "private Task? background; public Task StopAsync",
                StringComparison.Ordinal)
            + helper;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R2CarrierProofNeverCoversAnOrphanWriter(bool orphan)
    {
        string helper = "class Carrier : IDisposable { private readonly RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter first, second; private Carrier(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter a, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter b) { first=a; second=b; } " + R4SafeCarrierFactory(extraAcquisition: orphan ? "var orphan=store.CreateWriterAsync();" : "") + " public async Task CompleteAsync() { await first.CompleteAsync(); await second.CompleteAsync(); } public void Dispose() { first.Dispose(); second.Dispose(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; using var writers=Carrier.Create(store); await writers.CompleteAsync();", R2Blobs + helper));

        Assert.Equal(orphan ? 1 : 0, result.Diagnostics.Count(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData("using (group) { } using var resurrected = group;", false)]
    [InlineData("{ using var alias = group; } using var resurrected = group;", false)]
    [InlineData("var alias = group; alias.Dispose(); using var resurrected = group;", false)]
    [InlineData("using var retained = group;", true)]
    public void R2DisposedAdmissionAliasesCannotBeResurrected(string lifetime, bool valid)
    {
        string body = AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal) + " if (!lease.TryBeginExternalEffectGroup(out var group)) return; " + lifetime + " System.IO.File.Delete(\"path\");";

        Assert.Equal(valid, !R2Discover(R2Source(body)).Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("direct", true)]
    [InlineData("transferred", true)]
    [InlineData("admission-handle-guard", true)]
    [InlineData("shared-conditional-branch", true)]
    [InlineData("nested-cleanup", true)]
    [InlineData("missing-cleanup", false)]
    [InlineData("conditional-cleanup", false)]
    [InlineData("conditional-transfer", false)]
    [InlineData("overwritten-carrier", false)]
    [InlineData("cleanup-before-effect", false)]
    [InlineData("escaped-carrier", false)]
    public void ManualEffectGroupRequiresExactTransferAndExhaustiveFinallyCleanup(
        string shape,
        bool retained)
    {
        string lifetime = shape switch
        {
            "direct" => "IDisposable group = null!; try { if (!lease.TryBeginExternalEffectGroup(out group)) return; System.IO.File.Delete(\"path\"); } finally { if (group is not null) group.Dispose(); }",
            "transferred" => "IDisposable effectGroup = null!; try { if (!lease.TryBeginExternalEffectGroup(out var admittedGroup)) return; effectGroup = admittedGroup; System.IO.File.Delete(\"path\"); } finally { if (effectGroup is not null) effectGroup.Dispose(); }",
            "admission-handle-guard" => "IDisposable effectGroup = null!; try { if (lease is not null) { if (!lease.TryBeginExternalEffectGroup(out var admittedGroup)) return; effectGroup = admittedGroup; } System.IO.File.Delete(\"path\"); } finally { if (effectGroup is not null) effectGroup.Dispose(); }",
            "shared-conditional-branch" => "IDisposable effectGroup = null!; try { if (DateTime.UtcNow.Ticks < 0) { } else { if (lease is not null) { if (!lease.TryBeginExternalEffectGroup(out var admittedGroup)) return; effectGroup = admittedGroup; } System.IO.File.Delete(\"path\"); } } finally { if (effectGroup is not null) effectGroup.Dispose(); }",
            "nested-cleanup" => "IDisposable group = null!; try { if (!lease.TryBeginExternalEffectGroup(out group)) return; System.IO.File.Delete(\"path\"); } finally { try { try { } finally { if (group is not null) { try { group.Dispose(); } catch (Exception) { } } } } finally { } }",
            "missing-cleanup" => "IDisposable group = null!; try { if (!lease.TryBeginExternalEffectGroup(out group)) return; System.IO.File.Delete(\"path\"); } finally { }",
            "conditional-cleanup" => "IDisposable group = null!; try { if (!lease.TryBeginExternalEffectGroup(out group)) return; System.IO.File.Delete(\"path\"); } finally { if (DateTime.UtcNow.Ticks < 0 && group is not null) group.Dispose(); }",
            "conditional-transfer" => "IDisposable effectGroup = null!; try { if (!lease.TryBeginExternalEffectGroup(out var admittedGroup)) return; if (DateTime.UtcNow.Ticks < 0) effectGroup = admittedGroup; System.IO.File.Delete(\"path\"); } finally { if (effectGroup is not null) effectGroup.Dispose(); }",
            "overwritten-carrier" => "IDisposable effectGroup = null!; try { if (!lease.TryBeginExternalEffectGroup(out var admittedGroup)) return; effectGroup = admittedGroup; effectGroup = null!; System.IO.File.Delete(\"path\"); } finally { if (effectGroup is not null) effectGroup.Dispose(); }",
            "cleanup-before-effect" => "IDisposable group = null!; try { if (!lease.TryBeginExternalEffectGroup(out group)) return; group.Dispose(); System.IO.File.Delete(\"path\"); } finally { if (group is not null) group.Dispose(); }",
            _ => "IDisposable group = null!; try { if (!lease.TryBeginExternalEffectGroup(out group)) return; GC.KeepAlive(group); System.IO.File.Delete(\"path\"); } finally { if (group is not null) group.Dispose(); }",
        };

        string body = AcquireWork.Replace(
                "return Task.CompletedTask;",
                "return;",
                StringComparison.Ordinal)
            + lifetime;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body));

        Assert.Equal(
            retained,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Delete"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R2EveryLocalWriterNeedsItsOwnTerminalAndExceptionCleanup(bool completeBoth)
    {
        string body = R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; var first=store.CreateWriterAsync(); try { var second=store.CreateWriterAsync(); try { await first.CompleteAsync(); " + (completeBoth ? "await second.CompleteAsync();" : "") + " } finally { second.Dispose(); } } finally { first.Dispose(); }";

        Assert.Equal(completeBoth ? 0 : 1, R2Discover(R2Source(body, R2Blobs)).Diagnostics.Count(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    private const string AdmissionTypes = """
        namespace RetroDownfall.Arcanum.Infrastructure.Data
        {
            public enum GrimoireWorkKind { WorkspaceIndexing = 4 }
            public interface IGrimoireWorkLease : System.IDisposable, System.IAsyncDisposable { bool TryBeginExternalEffectGroup(out System.IDisposable group); }
            public interface IGrimoireConnectionAdmissionGate { bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease); }
        }
        """;

    private const string AcquireWork = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var work)) return Task.CompletedTask; using var lease = work;";

    [Theory]
    [InlineData("{ using var lease = work; System.IO.File.Exists(\"path\"); }", true)]
    [InlineData("try { using var lease = work; System.IO.File.Exists(\"path\"); } finally { }", true)]
    [InlineData("object marker; { using var lease = work; System.IO.File.Exists(\"path\"); }", true)]
    [InlineData("{ using var lease = work; } System.IO.File.Exists(\"path\");", false)]
    [InlineData("if (DateTime.UtcNow.Ticks < 0) { using var lease = work; System.IO.File.Exists(\"path\"); }", false)]
    [InlineData("MayThrow(); { using var lease = work; System.IO.File.Exists(\"path\"); }", false)]
    public void NestedLexicalLeaseMustTakeOwnershipBeforeAnyThrowingPath(
        string lifetime,
        bool retained)
    {
        string acquire = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; "
            + "if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var work)) return; ";

        const string helper = "static void MayThrow() => throw new InvalidOperationException();";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(acquire + lifetime).Replace(
                "public async Task StartAsync",
                helper + " public async Task StartAsync",
                StringComparison.Ordinal));

        Assert.Equal(
            retained,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Exists"));
    }

    [Theory]
    [InlineData("manual", true)]
    [InlineData("using", true)]
    [InlineData("alternative-return", false)]
    [InlineData("wrong-kind", false)]
    [InlineData("intervening-throw", false)]
    [InlineData("missing-cleanup", false)]
    [InlineData("conditional-cleanup", false)]
    [InlineData("unawaited", false)]
    public void ReturnedWorkLeaseRequiresExactAcquisitionAndExhaustiveCallerOwnership(
        string shape,
        bool retained)
    {
        string kind = shape == "wrong-kind"
            ? "(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind)0"
            : "RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing";

        string alternative = shape == "alternative-return"
            ? "if (DateTime.UtcNow.Ticks < 0) return new OtherLease();"
            : "";

        string body = shape switch
        {
            "using" => "using var lease = await AcquireAsync(gate); System.IO.File.Exists(\"path\");",
            "intervening-throw" => "var lease = await AcquireAsync(gate); MayThrow(); try { System.IO.File.Exists(\"path\"); } finally { lease.Dispose(); }",
            "missing-cleanup" => "var lease = await AcquireAsync(gate); System.IO.File.Exists(\"path\");",
            "conditional-cleanup" => "var lease = await AcquireAsync(gate); try { System.IO.File.Exists(\"path\"); } finally { if (DateTime.UtcNow.Ticks < 0) lease.Dispose(); }",
            "unawaited" => "Task<RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease> pending = AcquireAsync(gate); System.IO.File.Exists(\"path\"); await pending;",
            _ => "var lease = await AcquireAsync(gate); try { System.IO.File.Exists(\"path\"); } finally { lease.Dispose(); }",
        };

        string members = "private sealed class OtherLease : RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease { "
            + "public bool TryBeginExternalEffectGroup(out IDisposable group) { group = null!; return false; } "
            + "public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; } "
            + "private static void MayThrow() => throw new InvalidOperationException(); "
            + "private static async Task<RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease> AcquireAsync("
            + "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate) { await Task.Yield(); "
            + alternative
            + " if (gate.TryAcquireWorkLease(" + kind + ", out var lease)) { return lease; } throw new OperationCanceledException(); } ";

        string source = R2Source(
                "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; "
                + body)
            .Replace(
                "public async Task StartAsync",
                members + " public async Task StartAsync",
                StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Equal(
            retained,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Exists"));
    }

    [Fact]
    public void CompoundFailureGuardDominatesTheRetainedWorkFrontier()
    {
        const string body = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; if (DateTime.UtcNow.Ticks < 0 || !gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var work)) return Task.CompletedTask; using var lease = work; System.IO.File.Exists(\"path\");";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(FixtureSource(body, AdmissionTypes), OrdinaryRoot());

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

        Assert.Contains(result.Items, static site => site.Kind == HostedProducerSiteKind.EffectFrontier && site.Callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal));
    }

    [Fact]
    public void SoleSuccessfulLoopExitDominatesTheRetainedWorkFrontier()
    {
        const string body = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease work; while (true) { if (gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out work)) { break; } } using var lease = work; System.IO.File.Exists(\"path\");";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(FixtureSource(body, AdmissionTypes), OrdinaryRoot());

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

        Assert.Contains(result.Items, static site => site.Kind == HostedProducerSiteKind.EffectFrontier && site.Callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R2DeclaredAuthorityDoesNotOverrideTheCallingAuthority(bool admitted)
    {
        string source = R2Source((admitted ? R2Admission : "") + "await ContinueAsync(token);").Replace("public Task StopAsync", "private Task ContinueAsync(CancellationToken token) { System.IO.File.Delete(\"path\"); return Task.CompletedTask; } public Task StopAsync", StringComparison.Ordinal);

        HostedProducerOperationEntry startup = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync", Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.ContinueAsync: readiness" };

        HostedProducerDiscovery<HostedProducerSite> discovery = R2Discover(source, OrdinaryRoot(), startup);

        Assert.Equal(2, discovery.Items.Count(static site => site.Callee == "System.IO.File.Delete"));

        Assert.Equal(admitted, !discovery.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"));

        Assert.Contains(
            discovery.Items,
            static site => site.Callee == "System.IO.File.Delete"
                && site.Member == "ContinueAsync"
                && site.OperationId.StartsWith("Worker.StartAsync/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("if (background is not null) await background.WaitAsync(token);", true)]
    [InlineData("if (DateTime.UtcNow.Ticks < 0) await background!;", false)]
    public void R2HostHandoffRequiresAnExactTaskFieldAndStopJoin(string stopBody, bool joined)
    {
        string source = R2Source("background = Task.Run(() => ContinueAsync(token));")
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "private Task? background; private async Task ContinueAsync(CancellationToken token) { " + R2Admission + " System.IO.File.Delete(\"path\"); } public async Task StopAsync(CancellationToken token) { " + stopBody + " }", StringComparison.Ordinal);

        HostedProducerOperationEntry startup = OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: readiness" };

        HostedProducerOperationEntry runtime = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync" };

        HostedProducerDiscovery<HostedProducerSite> discovery = R2Discover(source, startup, runtime);

        Assert.Equal(joined, !discovery.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(joined ? 1 : 2, discovery.Items.Count(static site => site.Callee == "System.IO.File.Delete"));

        if (joined)
        {
            Assert.DoesNotContain(discovery.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
        }
    }

    [Fact]
    public void UncalledLocalFunctionDoesNotInheritLexicalAuthority()
    {
        Assert.DoesNotContain(Discover(FixtureSource("void Cold() { System.IO.File.Delete(\"path\"); }")).Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void GeneratorOutputSuppliesSymbolsWithoutInventingAuthoredProducerSites()
    {
        CSharpCompilation compilation = Compile(FixtureSource("Generated.Read();")).AddSyntaxTrees(CSharpSyntaxTree.ParseText("static class Generated { public static void Read() { System.IO.File.Exists(\"generated\"); } }", path: "ExampleGenerator/Generated.g.cs"));

        Assert.Empty(HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [], []).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyJoinedCallbacksExecuteUnderTheirCallers(bool joined)
    {
        string body = joined ? "_ = Task.Run(() => System.IO.File.Delete(\"path\"));" : "Action cold = () => System.IO.File.Delete(\"path\");";

        Assert.Equal(joined, Discover(FixtureSource(body)).Items.Any(static site => site.Callee == "System.IO.File.Delete"));
    }

    [Fact]
    public void AmbiguousSensitiveInterfaceSlotsFailClosed()
    {
        string helpers = "namespace RetroDownfall.Arcanum.Core.Storage { public interface IEncryptedBlobStore { void OpenReadAsync(); } public interface ISessionAttachmentStore { void OpenReadAsync(); } } class Both : RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore, RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore { public void OpenReadAsync() {} }";

        Assert.Contains(Discover(FixtureSource("new Both().OpenReadAsync();", helpers)).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    [Fact]
    public void NestedTypesAndUnqualifiedGettersKeepTheirExactIdentity()
    {
        string source = FixtureSource("_ = Bytes;")
            .Replace("public Task StartAsync", "private long Bytes => Outer.Helper.Read(); public Task StartAsync", StringComparison.Ordinal)
            + "static class Outer { public static class Helper { public static long Read() => new System.IO.FileInfo(\"path\").Length; } }";

        Assert.Contains(Discover(source).Items, static site => site.Callee == "System.IO.FileInfo.Length" && site.EnclosingType == "Outer.Helper");
    }

    [Fact]
    public void ImplicitStreamDisposalAfterGroupLossFailsTheSharedFrontierCheck()
    {
        string body = AcquireWork + " if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group; using var writer = new System.IO.StreamWriter(System.IO.Stream.Null); held.Dispose();";

        Assert.Contains(DiscoverWithRoots(FixtureSource(body, AdmissionTypes), OrdinaryRoot()).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.StreamWriter.Dispose");
    }

    [Fact]
    public void InheritedBlobWriterDisposalIsStillAnExplicitPublicationEffect()
    {
        Assert.Contains(Discover(FixtureSource("RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer = null!; _ = writer.DisposeAsync();")).Items, static site => site.Callee == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.DisposeAsync" && site.Kind == HostedProducerSiteKind.FileSystemEffect);
    }

    [Fact]
    public void ValidatorRejectsPublicationEndpointsWithDifferentGroupIdentities()
    {
        HostedProducerSite create = new("Worker", "Worker.StartAsync/effect@one/site@src/Fixture.cs:10", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.FileSystemEffect, "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync");

        HostedProducerSite complete = create with { OperationId = "Worker.StartAsync/effect@two/site@src/Fixture.cs:20", Callee = "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.CompleteAsync" };

        HostedProducerSite dispose = create with { OperationId = "Worker.StartAsync/effect@two/site@src/Fixture.cs:30", Callee = "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.Dispose" };

        Assert.Contains(HostedGrimoireProducerInventory.Validate([new("Worker", [OrdinaryRoot() with { Sites = [create, complete, dispose] }])], [], new(["Worker"], []), new([create, complete, dispose], [])).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE");
    }

    [Theory]
    [InlineData("work", "HOSTED_SITE_WORK_FRONTIER_MISSING")]
    [InlineData("effect-free", "HOSTED_SITE_AUTHORITY_INVALID")]
    [InlineData("root", "HOSTED_SITE_ROOT_MISMATCH")]
    public void ValidatorDoesNotTrustUnrelatedFrontiersOrAuthorityLabels(string mutation, string expected)
    {
        HostedProducerSite read = new("Worker", "Worker.StartAsync/work@other/site@src/Fixture.cs:20", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.FileSystemRead, "System.IO.File.Exists");

        HostedProducerSite frontier = read with { OperationId = "Worker.StartAsync/work@one/workKind=WorkspaceIndexing/site@src/Fixture.cs:10", Kind = HostedProducerSiteKind.EffectFrontier, Callee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease" };

        HostedProducerOperationEntry operation = OrdinaryRoot();

        if (mutation == "effect-free") operation = operation with { Authority = HostedProducerAuthorityKind.EffectFree, WorkKind = null, Proof = "Worker.StartAsync: claimed no work" };

        if (mutation == "root") read = read with { OperationId = "Other.StartAsync/work@one/site@src/Fixture.cs:20" };

        Assert.Contains(HostedGrimoireProducerInventory.Validate([new("Worker", [operation with { Sites = [read, frontier] }])], [], new(["Worker"], []), new([read, frontier], [])).Diagnostics, diagnostic => diagnostic.Code == expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PositiveAdmissionGuardProtectsOnlyItsSuccessfulBranch(bool success)
    {
        string body = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; if (gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var work)) { using var lease = work; " + (success ? "System.IO.File.Exists(\"path\");" : "") + " } else { " + (success ? "" : "System.IO.File.Exists(\"path\");") + " }";

        Assert.Equal(success, !DiscoverWithRoots(FixtureSource(body, AdmissionTypes), OrdinaryRoot()).Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("int renewed = 0;", true)]
    [InlineData("string? label = null;", true)]
    [InlineData("Helper.MayThrow();", false)]
    public void NonThrowingLocalPreludeCanImmediatelyTransferWorkLeaseOwnership(string prelude, bool retained)
    {
        string body = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var work)) return Task.CompletedTask; " + prelude + " using var lease = work; System.IO.File.Exists(\"path\");";
        string helpers = AdmissionTypes + " static class Helper { public static void MayThrow() { } }";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(FixtureSource(body, helpers), OrdinaryRoot());

        Assert.Equal(retained, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"));
    }

    [Fact]
    public void ProductionCompilationsResolveGeneratedJsonSymbols()
    {
        foreach (CSharpCompilation compilation in HostedGrimoireProducerInventory.ProductionCompilations)
        {
            INamedTypeSymbol[] contexts = compilation.SyntaxTrees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().Select(declaration => compilation.GetSemanticModel(tree).GetDeclaredSymbol(declaration))).OfType<INamedTypeSymbol>().Where(static type => type.BaseType?.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext").ToArray();

            if (compilation.AssemblyName != "RetroDownfall.Arcanum.Secrets")
            {
                Assert.NotEmpty(contexts);
            }

            Assert.All(contexts, static context => Assert.NotEmpty(context.GetMembers("Default")));

            Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
    }

    [Theory]
    [InlineData("sp => new Helper(sp.GetRequiredService<Dependency>())", true)]
    [InlineData("sp => DateTime.Now.Ticks > 0 ? new Helper(null!) : new Helper(null!)", true)]
    [InlineData("sp => DateTime.Now.Ticks > 0 ? new Helper(null!) : new Other()", true)]
    [InlineData("sp => Make(sp)", true)]
    [InlineData("Make", true)]
    [InlineData("factory", true)]
    [InlineData("sp => { IHelper item = new Helper(null!); return item; }", true)]
    [InlineData("sp => Unknown(sp)", false)]
    public void FactoryBindingFollowsReturnedImplementation(string factory, bool resolved)
    {
        string helpers = "interface IHelper { void Run(); } class Dependency {} class Helper(Dependency dependency) : IHelper { public void Run() { System.IO.File.Delete(\"path\"); } } class Other : IHelper { public void Run() { System.IO.File.Exists(\"other\"); } }";

        string source = FixtureSource("IHelper helper = null!; helper.Run();", helpers)
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); Func<IServiceProvider,IHelper> factory = Make; services.AddSingleton<IHelper>(" + factory + ");", StringComparison.Ordinal)
            .Replace("public static void Configure", "static IHelper Make(IServiceProvider sp) => new Helper(sp.GetRequiredService<Dependency>()); static IHelper Unknown(IServiceProvider sp) => throw new NotImplementedException(); public static void Configure", StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Equal(resolved, result.Items.Any(static site => site.Callee == "System.IO.File.Delete"));

        Assert.Equal(!resolved, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Detail == "IHelper.Run"));
    }

    [Fact]
    public void FactoryBindingFollowsAnExplicitConcreteCastOfAnAliasedService()
    {
        const string helpers = "interface IBroad { } interface INarrow { void Run(); } sealed class Helper : IBroad, INarrow { public void Run() { System.IO.File.Delete(\"path\"); } }";

        string source = FixtureSource(
                "INarrow helper = null!; helper.Run();",
                helpers)
            .Replace(
                "services.AddHostedService<Worker>();",
                "services.AddHostedService<Worker>(); services.AddSingleton<IBroad, Helper>(); services.AddSingleton<INarrow>(sp => (Helper)sp.GetRequiredService<IBroad>());",
                StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Single(
            result.Items,
            static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
                && diagnostic.Detail == "INarrow.Run");
    }

    [Fact]
    public void SingletonPropertyBindingFollowsItsConcreteImplementation()
    {
        string source = FixtureSource("IHelper helper = null!; helper.Run();", "interface IHelper { void Run(); } sealed class Helper : IHelper { public static Helper Instance { get; } = new(); public void Run() { System.IO.File.Exists(\"path\"); } }")
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); services.AddSingleton<IHelper>(Helper.Instance);", StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Contains(result.Items, static site => site.EnclosingType == "Helper" && site.Callee == "System.IO.File.Exists");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Detail == "IHelper.Run");
    }

    [Fact]
    public void ConcreteArgumentBindsAnInterfaceReceiverInsideTheExactAuthoredCall()
    {
        const string helpers = "interface IWriter { void Write(); } sealed class Writer : IWriter { public void Write() { System.IO.File.Delete(\"path\"); } } static class Helper { public static void Run(IWriter writer) { writer.Write(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource("var writer = new Writer(); Helper.Run(writer);", helpers));

        Assert.Single(result.Items, static site => site.EnclosingType == "Writer" && site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Detail == "IWriter.Write");
    }

    [Fact]
    public void ExactBatchSpillObserverIsEffectFreeButUnknownInterfacesRemainUnresolved()
    {
        const string helpers = "namespace RetroDownfall.Arcanum.Api.Intelligence { internal interface IBatchJsonlRecordObserver { void SpillCreated(string path); } } interface IUnknownObserver { void Observe(); }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(
                "RetroDownfall.Arcanum.Api.Intelligence.IBatchJsonlRecordObserver spill = null!; spill.SpillCreated(\"path\"); IUnknownObserver unknown = null!; unknown.Observe();",
                helpers));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
                && diagnostic.Detail == "RetroDownfall.Arcanum.Api.Intelligence.IBatchJsonlRecordObserver.SpillCreated");

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
                && diagnostic.Detail == "IUnknownObserver.Observe");
    }

    [Fact]
    public void CallbackMemoizationIncludesItsCapturedConcreteBindings()
    {
        const string helpers = "interface IWriter { void Write(); } sealed class First : IWriter { public void Write() { System.IO.File.Exists(\"first\"); } } sealed class Second : IWriter { public void Write() { System.IO.Directory.Exists(\"second\"); } } static class Outer { public static void Run(IWriter writer) { Helper.Invoke(() => writer.Write()); } } static class Helper { public static void Invoke(Action callback) { callback(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + "Outer.Run(new First()); Outer.Run(new Second());", helpers));

        Assert.Contains(result.Items, static site => site.EnclosingType == "First" && site.Callee == "System.IO.File.Exists");

        Assert.Contains(result.Items, static site => site.EnclosingType == "Second" && site.Callee == "System.IO.Directory.Exists");
    }

    [Fact]
    public void NestedLambdaRetainsItsCapturedExactCallbackBinding()
    {
        const string helpers = "static class Outer { public static Task RunAsync(Func<Task> operation) => Task.Run(() => Inner(operation)); private static void Inner(Func<Task> operation) => operation().GetAwaiter().GetResult(); }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + " await Outer.RunAsync(() => { System.IO.File.Delete(\"path\"); return Task.CompletedTask; });",
                helpers));

        Assert.Single(
            result.Items,
            static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Theory]
    [InlineData("bound", true)]
    [InlineData("missing", false)]
    [InlineData("empty", false)]
    [InlineData("drifted", false)]
    public void ExactAggregateBoundaryExecutesItsBoundSourceContract(string shape, bool proven)
    {
        const string boundary = "RetroDownfall.Arcanum.Infrastructure.InstallationReset.IInstallationResetStartupRecovery";

        string helpers = "namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset { public interface IInstallationResetStartupRecovery { void RecoverBeforeBootstrapAsync(); } public class Recovery : IInstallationResetStartupRecovery { public void RecoverBeforeBootstrapAsync() { " + (shape == "empty" ? "" : "System.IO.File.Delete(\"stranded\");") + " } } public class Unrelated { public void RecoverBeforeBootstrapAsync() { System.IO.File.Delete(\"decoy\"); } } }";

        string binding = shape == "missing" ? "" : "services.AddSingleton<" + boundary + ", RetroDownfall.Arcanum.Infrastructure.InstallationReset." + (shape == "drifted" ? "Unrelated" : "Recovery") + ">();";

        string source = FixtureSource(boundary + " recovery = null!; recovery.RecoverBeforeBootstrapAsync();", helpers).Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>();" + binding, StringComparison.Ordinal);

        HostedProducerOperationEntry root = OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: awaited recovery" };

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(source, root);

        Assert.Equal(proven, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_AGGREGATE_PROOF_MISSING"));

        if (proven)
        {
            Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.InstallationReset.Recovery" && site.OperationId.StartsWith("Worker.StartAsync/", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("different", false)]
    [InlineData("implicit-after-loss", false)]
    [InlineData("explicit-after-loss", false)]
    [InlineData("explicit", false)]
    public void BlobPublicationRequiresOneContinuousGroupThroughDisposal(string shape, bool valid)
    {
        string declarations = """
            namespace RetroDownfall.Arcanum.Core.Storage
            {
                public interface IEncryptedBlobStore { EncryptedBlobWriter CreateWriterAsync(); }
                public sealed class EncryptedBlobDescriptor {}
                public class EncryptedBlobWriter : System.IDisposable { public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor()); public void Dispose() {} }
            }
            """;

        string create = shape.StartsWith("explicit", StringComparison.Ordinal) ? "var writer = store.CreateWriterAsync();" : "using var writer = store.CreateWriterAsync();";

        string switchGroup = shape == "different" ? "held.Dispose(); if (!lease.TryBeginExternalEffectGroup(out var second)) return; using var next = second;" : "";

        string loss = shape.EndsWith("after-loss", StringComparison.Ordinal) ? "held.Dispose();" : "";

        string dispose = shape.StartsWith("explicit", StringComparison.Ordinal) ? "writer.Dispose();" : "";

        string body = R2Admission + " RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store = null!; " + create + switchGroup + " await writer.CompleteAsync(); " + loss + dispose;

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(R2Source(body, declarations), OrdinaryRoot());

        Assert.Equal(valid, !result.Diagnostics.Any(static d => d.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));

        if (valid)
        {
            Assert.Contains(result.Items, static site => site.Callee == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.Dispose");
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void BatchWriterFieldsRemainInTheCallersContinuousPublicationRegion(bool loseGroup, bool valid)
    {
        string helpers = AdmissionTypes + """
            namespace RetroDownfall.Arcanum.Core.Storage
            {
                public interface IEncryptedBlobStore { EncryptedBlobWriter CreateWriterAsync(); }
                public sealed class EncryptedBlobDescriptor {}
                public class EncryptedBlobWriter : System.IDisposable { public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor()); public void Dispose() {} }
            }
            sealed class BatchJsonlWriters : System.IDisposable
            {
                private readonly RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter _writer;
                private BatchJsonlWriters(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) { _writer = writer; }
                public static BatchJsonlWriters CreateAsync(RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store)
                {
                    var writer = store.CreateWriterAsync();
                    return new BatchJsonlWriters(writer);
                }

                public async Task CompleteAsync() { await _writer.CompleteAsync(); }
                public void Dispose() { _writer.Dispose(); }
            }
            """;

        string body = AcquireWork + " if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group; RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store = null!; using var writers = BatchJsonlWriters.CreateAsync(store); await writers.CompleteAsync(); " + (loseGroup ? "held.Dispose();" : "");

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(FixtureSource(body, helpers), OrdinaryRoot());

        Assert.Equal(valid, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));

        Assert.Contains(result.Items, static site => site.Callee == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.Dispose");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PropertyReadsAndExpressionGettersRequireRetainedAuthority(bool throughGetter, bool admitted)
    {
        string guard = admitted ? AcquireWork : "";

        string read = throughGetter ? "_ = Helper.Bytes;" : "_ = new System.IO.FileInfo(\"path\").Length;";

        string source = FixtureSource(guard + read, AdmissionTypes + "static class Helper { public static long Bytes => new System.IO.FileInfo(\"path\").Length; }");

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(source, OrdinaryRoot());

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.FileInfo.Length");

        Assert.DoesNotContain(result.Diagnostics, static d => d.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && d.Detail == "System.IO.FileInfo.Length");

        Assert.Equal(admitted, !result.Diagnostics.Any(static d => d.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && d.Detail == "System.IO.FileInfo.Length"));
    }

    [Fact]
    public void ValidatorAllowsWorkAdmittedFilesystemReadsWithoutEffectGroup()
    {
        HostedProducerSite read = new("Worker", "Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.FileSystemRead, "System.IO.FileInfo.Length");

        HostedProducerSite lease = read with { Kind = HostedProducerSiteKind.EffectFrontier, Callee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease", OperationId = "Worker.StartAsync/workKind=WorkspaceIndexing" };

        HostedProducerOperationEntry operation = OrdinaryRoot() with { Sites = [read, lease] };

        Assert.DoesNotContain(HostedGrimoireProducerInventory.Validate([new("Worker", [operation])], [], new(["Worker"], []), new([read, lease], [])).Diagnostics, static d => d.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Fact]
    public void DeepSiblingCallAfterGroupDisposalIsDiscoveredAndRejected()
    {
        string helpers = """
            static class Outer
            {
                public static void Run(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease)
                {
                    if (!lease.TryBeginExternalEffectGroup(out var group)) return;
                    using (group) { Middle.Run(); }
                    Middle.Run();
                }
            }
            static class Middle { public static void Run() { Leaf.Run(); } }
            static class Leaf { public static void Run() { System.IO.File.Delete("path"); } }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(FixtureSource(AcquireWork + " Outer.Run(lease);", AdmissionTypes + helpers), OrdinaryRoot());

        Assert.Equal(2, result.Items.Count(static site => site.Callee == "System.IO.File.Delete"));

        Assert.Single(result.Diagnostics, static d => d.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && d.Detail == "System.IO.File.Delete");
    }

    [Fact]
    public void DiamondAndRecursiveHelpersCollapseEquivalentAuthorityPaths()
    {
        string helpers = "static class Left { public static void Run() { Leaf.Run(); } } static class Right { public static void Run() { Leaf.Run(); } } static class Leaf { public static void Run() { Left.Run(); System.IO.File.Exists(\"path\"); } }";

        HostedProducerSite[] sites = Discover(FixtureSource("Left.Run(); Right.Run();", helpers)).Items.Where(static site => site.Callee == "System.IO.File.Exists").ToArray();

        Assert.Single(sites);
    }

    [Fact]
    public void SourceExpandedAggregateBoundaryTableContainsExactlyTheTwelveProvenContracts()
    {
        Assert.Equal(new[] {
            "RetroDownfall.Arcanum.Api.Intelligence.IBatchRecoveryService.ReconcileStrandedAsync",
            "RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.InitializeAsync",
            "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.ApplyOrResumeHostedPruneAsync",
            "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverFactoryResetAsync",
            "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverMutationAsync",
            "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverPruneAsync",
            "RetroDownfall.Arcanum.Infrastructure.InstallationReset.IInstallationResetStartupRecovery.RecoverBeforeBootstrapAsync",
            "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.IGrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync",
            "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseBootstrapper.EnsureInitializedAsync",
            "RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync",
            "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler.ReconcileAsync",
            "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexProcessor.ProcessAsync"
        }.Order(StringComparer.Ordinal), HostedGrimoireProducerInventory.AggregateBoundaries.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void BatchRecoveryExpandsItsBoundSourceWithoutCatalogingTheFacade()
    {
        const string boundary = "RetroDownfall.Arcanum.Api.Intelligence.IBatchRecoveryService";

        const string helpers = "namespace RetroDownfall.Arcanum.Api.Intelligence { public interface IBatchRecoveryService { void ReconcileStrandedAsync(); } public class Recovery : IBatchRecoveryService { public void ReconcileStrandedAsync() { System.IO.File.Delete(\"internal\"); } } }";

        string source = FixtureSource(boundary + " recovery = null!; recovery.ReconcileStrandedAsync();", helpers).Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); services.AddSingleton<" + boundary + ", RetroDownfall.Arcanum.Api.Intelligence.Recovery>();", StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(source, OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: awaited recovery" });

        Assert.DoesNotContain(result.Items, static site => site.Callee == "RetroDownfall.Arcanum.Api.Intelligence.IBatchRecoveryService.ReconcileStrandedAsync");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void SuccessfulElseRetainsBothWorkAndEffectOwnership()
    {
        string body = "bool deferred = false; RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var work)) { deferred = true; } else { using var lease = work; if (!lease.TryBeginExternalEffectGroup(out var group)) { deferred = true; } else { using var held = group; System.IO.File.Delete(\"path\"); } }";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(FixtureSource(body, AdmissionTypes), OrdinaryRoot());

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(result.Diagnostics, static d => d.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    private static HostedProducerOperationEntry OrdinaryRoot(string identity = "Worker.StartAsync") => new(identity, "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.WorkspaceIndexing, null, []);

    private static string CallRoot(string source, string callee, int occurrence = 0)
    {
        CSharpCompilation compilation = Compile(source);

        SyntaxTree tree = compilation.SyntaxTrees.Single();

        Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax call = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>().Where(call => compilation.GetSemanticModel(tree).GetSymbolInfo(call).Symbol is IMethodSymbol symbol && symbol.ContainingType.ToDisplayString() + "." + symbol.Name == callee).ElementAt(occurrence);

        return "Worker.StartAsync::call:"
            + callee
            + "#"
            + occurrence
            + "~"
            + HostedGrimoireProducerInventory.Fingerprint(call);
    }

    [Fact]
    public void ExactSourceAnchorRejectsInsertedSiblingRatherThanRetargeting()
    {
        string source = FixtureSource("System.IO.File.Exists(\"one\"); System.IO.File.Exists(\"two\");");

        HostedProducerOperationEntry root = OrdinaryRoot(CallRoot(source, "System.IO.File.Exists", 1));

        Assert.DoesNotContain(DiscoverWithRoots(source, root).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ROOT_UNRESOLVED");

        Assert.Contains(DiscoverWithRoots(source.Replace("System.IO.File.Exists(\"one\");", "System.IO.File.Exists(\"inserted\"); System.IO.File.Exists(\"one\");", StringComparison.Ordinal), root).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ROOT_UNRESOLVED");
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverWithRoots(string source, params HostedProducerOperationEntry[] roots)
    {
        CSharpCompilation compilation = Compile(source);

        return HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", roots)], []);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExactCallSelectorsDoNotCrossScanMixedAuthorityBranches(bool protectedRuntime)
    {
        string admission = protectedRuntime ? AcquireWork + " if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group;" : "";

        string source = FixtureSource("if (DateTime.Now.Ticks > 0) { System.IO.File.Exists(\"startup\"); } else { " + admission + " System.IO.File.Delete(\"runtime\"); }", AdmissionTypes);

        HostedProducerOperationEntry startup = new(CallRoot(source, "System.IO.File.Exists"), "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, "Worker.StartAsync: awaited startup branch", []);

        HostedProducerOperationEntry runtime = OrdinaryRoot(CallRoot(source, "System.IO.File.Delete"));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(source, startup, runtime);

        Assert.Single(result.Items, site => site.Callee == "System.IO.File.Exists" && site.OperationId.StartsWith(startup.OperationId, StringComparison.Ordinal));

        Assert.Single(result.Items, site => site.Callee == "System.IO.File.Delete" && site.OperationId.StartsWith(runtime.OperationId, StringComparison.Ordinal));

        Assert.DoesNotContain(result.Items, site => site.Callee == "System.IO.File.Delete" && site.OperationId.StartsWith(startup.OperationId, StringComparison.Ordinal));

        Assert.Equal(protectedRuntime, !result.Diagnostics.Any(static d => d.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Fact]
    public void SelectedOperationRoundTripsItsExactRetainedFrontiers()
    {
        string source = FixtureSource(AcquireWork + " if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group; System.IO.File.Delete(\"path\");", AdmissionTypes);

        HostedProducerOperationEntry root = OrdinaryRoot(CallRoot(source, "System.IO.File.Delete"));

        HostedProducerDiscovery<HostedProducerSite> discovery = DiscoverWithRoots(source, root);

        HostedProducerSite[] selected = discovery.Items.Where(site => site.OperationId.StartsWith(root.OperationId, StringComparison.Ordinal)).ToArray();

        Assert.Contains(selected, static site => site.Callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal));

        Assert.Contains(selected, static site => site.Callee.EndsWith(".TryBeginExternalEffectGroup", StringComparison.Ordinal));

        Assert.Empty(HostedGrimoireProducerInventory.Validate([new("Worker", [root with { Sites = selected }])], [], new(["Worker"], []), new(selected, discovery.Diagnostics)).Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingAuthorityClaimsAreRejected(bool wholeMember)
    {
        string source = FixtureSource("System.IO.File.Delete(\"path\");");

        HostedProducerOperationEntry ordinary = OrdinaryRoot(CallRoot(source, "System.IO.File.Delete"));

        HostedProducerOperationEntry startup = ordinary with { OperationId = wholeMember ? "Worker.StartAsync" : ordinary.OperationId, Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: startup" };

        Assert.Contains(DiscoverWithRoots(source, ordinary, startup).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ROOT_OVERLAP");
    }

    [Fact]
    public void SensitiveReadPropertySetterCannotBeClassifiedAsRead()
    {
        Assert.Contains(Discover(FixtureSource("new System.IO.FileInfo(\"path\").LastWriteTimeUtc = DateTime.UtcNow;")).Diagnostics, static d => d.Code == "HOSTED_SITE_UNCLASSIFIED" && d.Detail.Contains("LastWriteTimeUtc", StringComparison.Ordinal));
    }

    [Fact]
    public void AuthoredInitOnlyPropertyOnSensitiveTypeIsNotAnExternalEffect()
    {
        const string helper = "namespace RetroDownfall.Arcanum.Infrastructure.Backup { sealed class OwnedTemporaryDirectory { public string Path { get; init; } = string.Empty; public void TryDelete() { } } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource("_ = new RetroDownfall.Arcanum.Infrastructure.Backup.OwnedTemporaryDirectory { Path = \"path\" };", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.EndsWith(".Path setter", StringComparison.Ordinal));
    }

    [Fact]
    public void AuthoredGetOnlyAutoPropertyOnSensitiveTypeIsNotAnExternalEffect()
    {
        const string helper = "namespace RetroDownfall.Arcanum.Infrastructure.Backup { sealed class OwnedTemporaryDirectory { private OwnedTemporaryDirectory(string path) { Path = path; } public string Path { get; } public void TryDelete() { } public static OwnedTemporaryDirectory Create() => new(\"path\"); } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource("_ = RetroDownfall.Arcanum.Infrastructure.Backup.OwnedTemporaryDirectory.Create();", helper));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.EndsWith(".Path setter", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("RtlNtStatusToDosError", false)]
    [InlineData("UnexpectedNativeHelper", true)]
    public void OnlyExactPureSensitiveNativeHelpersAreExempt(string member, bool unclassified)
    {
        string helper = "namespace RetroDownfall.Arcanum.Infrastructure.Security { static class FileHandleIdentityInterop { [System.Runtime.InteropServices.DllImport(\"fixture\")] internal static extern uint " + member + "(int status); internal static uint Run() => " + member + "(0); } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource("_ = RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.Run();", helper));

        Assert.Equal(unclassified, result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.EndsWith("." + member, StringComparison.Ordinal)));
    }

    [Fact]
    public void EffectiveUserIdentityQueryIsAClassifiedSecurityRead()
    {
        const string helper = "namespace RetroDownfall.Arcanum.Infrastructure.Security { static class SecureFilePermissions { [System.Runtime.InteropServices.DllImport(\"fixture\")] internal static extern uint GetEffectiveUserIdNative(); internal static uint Run() => GetEffectiveUserIdNative(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource("_ = RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.Run();", helper));

        Assert.Contains(result.Items, static site => site.Callee == "RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.GetEffectiveUserIdNative" && site.Kind == HostedProducerSiteKind.FileSystemRead);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");
    }

    [Fact]
    public void WrongNamespaceWrapperIsRejected()
    {
        string source = RegistrationSource("RetroDownfall.Arcanum.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInstallationResetRecoveryAwareHostedService<Worker>(services);") + """
            namespace RetroDownfall.Arcanum.Infrastructure.DependencyInjection
            {
                public static class ServiceCollectionExtensions
                {
                    public static void AddInstallationResetRecoveryAwareHostedService<TService>(IServiceCollection services) where TService : class, IHostedService
                    {
                        services.AddHostedService(sp => new InstallationResetRecoveryAwareHostedService<TService>());
                    }
                }

                public class InstallationResetRecoveryAwareHostedService<T> : IHostedService
                {
                    public Task StartAsync(CancellationToken token) => Task.CompletedTask;

                    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
                }
            }
            """;

        Assert.Contains(HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([Compile(source)]).Diagnostics, static d => d.Code == "HOSTED_REGISTRATION_HELPER_SHAPE_CHANGED");
    }

    [Theory]
    [InlineData("System.IO.File.Delete(\"path\"); if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group;", false)]
    [InlineData("if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using (group) { } System.IO.File.Delete(\"path\");", false)]
    [InlineData("if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group; held.Dispose(); System.IO.File.Delete(\"path\");", false)]
    [InlineData("if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group; System.IO.File.Delete(\"path\");", true)]
    public void OrdinaryEffectsMustBeInsideTheRetainedGroup(string body, bool protectedEffect)
    {
        string source = FixtureSource("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease = null!; " + body, "namespace RetroDownfall.Arcanum.Infrastructure.Data { public interface IGrimoireWorkLease { bool TryBeginExternalEffectGroup(out System.IDisposable group); } }");

        CSharpCompilation compilation = Compile(source);

        HostedProducerOperationEntry operation = new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.WorkspaceIndexing, null, []);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [operation])], []);

        Assert.Equal(protectedEffect, !result.Diagnostics.Any(static d => d.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Fact]
    public void NameofIsNotAnUnresolvedCallTarget()
    {
        Assert.Empty(Discover(FixtureSource("_ = nameof(Worker);")).Diagnostics);
    }

    [Fact]
    public void EndpointThroughSingletonInterfaceHasAnExternalRoot()
    {
        string source = FixtureSource("")
            .Replace("public class Worker : IHostedService", "public class Worker : IHostedService, IQueue", StringComparison.Ordinal)
            .Replace("public Task StartAsync", "public void QueueIndexNow() { System.IO.File.Exists(\"path\"); } public Task StartAsync", StringComparison.Ordinal)
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); services.AddSingleton<IQueue>(sp => sp.GetRequiredService<Worker>());", StringComparison.Ordinal)
            + "interface IQueue { void QueueIndexNow(); } class Endpoint { void Invoke(IQueue worker) { worker.QueueIndexNow(); } }";

        Assert.Contains(Discover(source).Diagnostics, static item => item.Code == "HOSTED_EXTERNAL_OPERATION_UNCATALOGUED");
    }

    [Fact]
    public void DeclaredExternalRootIsResolvedAndNotReportedAsUncatalogued()
    {
        string source = FixtureSource("")
            .Replace("public Task StartAsync", "public void QueueIndexNow() { System.IO.File.Exists(\"path\"); } public Task StartAsync", StringComparison.Ordinal)
            + "class Endpoint { void Invoke(Worker worker) { worker.QueueIndexNow(); } }";

        CSharpCompilation compilation = Compile(source);

        HostedProducerOperationEntry root = new("Worker.QueueIndexNow", "src/Fixture.cs", "Worker", "QueueIndexNow", HostedProducerAuthorityKind.FiniteRequest, null, "Worker.QueueIndexNow: admitted finite request", []);

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [root])], []);

        Assert.DoesNotContain(discovery.Diagnostics, static d => d.Code == "HOSTED_EXTERNAL_OPERATION_UNCATALOGUED" || d.Code == "HOSTED_ROOT_UNRESOLVED");

        Assert.Contains(discovery.Items, static site => site.Member == "QueueIndexNow");
    }

    [Fact]
    public void PrivateCallsInsideOneHostedImplementationAreNotAdditionalExternalRoots()
    {
        string source = FixtureSource("")
            .Replace("public Task StartAsync", "public void QueueIndexNow() { Produce(); } private void Produce() { System.IO.File.Exists(\"path\"); } public Task StartAsync", StringComparison.Ordinal)
            + "class Endpoint { void Invoke(Worker worker) { worker.QueueIndexNow(); } }";

        CSharpCompilation compilation = Compile(source);

        HostedProducerOperationEntry root = new("Worker.QueueIndexNow", "src/Fixture.cs", "Worker", "QueueIndexNow", HostedProducerAuthorityKind.FiniteRequest, null, "Worker.QueueIndexNow: admitted finite request", []);

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [root])], []);

        Assert.DoesNotContain(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_EXTERNAL_OPERATION_UNCATALOGUED");

        Assert.Single(discovery.Items, static site => site.Callee == "System.IO.File.Exists");
    }

    [Fact]
    public void ApprenticeCatalogSeparatesRequestAndSelfAdmittedTaskAuthorities()
    {
        HostedProducerServiceEntry apprentice = Assert.Single(HostedGrimoireProducerInventory.Catalog, static service => service.ServiceType == "ApprenticeService");

        Assert.Contains(apprentice.Operations, static operation => operation.Member == "RunApprenticeAsync" && operation.Authority == HostedProducerAuthorityKind.OrdinaryHostedWork && operation.WorkKind == GrimoireWorkKind.ApprenticeExecution);

        Assert.Equal(["CancelAsync", "InterveneAsync", "PauseAsync", "ResumeAsync", "ReweaveAsync", "StartAsync"], apprentice.Operations.Where(static operation => operation.Authority == HostedProducerAuthorityKind.FiniteRequest).Select(static operation => operation.Member).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("exact", false)]
    [InlineData("missing-publication-barrier", true)]
    [InlineData("wrong-cleanup-task", true)]
    [InlineData("wrong-published-task", true)]
    [InlineData("missing-fault-observation", true)]
    public void ApprenticeSelfAdmittedTaskHandoffRequiresExactPublicationAndCleanup(
        string mutation,
        bool expectedUnowned)
    {
        const string type =
            "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService";

        HostedProducerOperationEntry startup = new(
            type + ".StartAsync",
            "src/Fixture.cs",
            type,
            "StartAsync",
            HostedProducerAuthorityKind.PreReadinessStartup,
            null,
            "host startup owns the exact self-admitted handoff",
            []);

        HostedProducerOperationEntry execution = new(
            type + ".RunApprenticeAsync",
            "src/Fixture.cs",
            type,
            "RunApprenticeAsync",
            HostedProducerAuthorityKind.OrdinaryHostedWork,
            GrimoireWorkKind.ApprenticeExecution,
            "the published task owns one independently admitted execution",
            []);

        HostedProducerDiscovery<HostedProducerSite> result =
            HostedGrimoireProducerInventory.DiscoverProducerSites(
                [Compile(ApprenticeSelfHandoffSource(mutation))],
                new(["ApprenticeService"], []),
                [new("ApprenticeService", [startup, execution])],
                []);

        Assert.Equal(
            expectedUnowned,
            result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Detail.StartsWith(
                    "System.Threading.Tasks.Task.Run;",
                    StringComparison.Ordinal)));

        if (!expectedUnowned)
        {
            Assert.DoesNotContain(
                result.Diagnostics,
                static diagnostic => diagnostic.Code is
                    "HOSTED_SITE_WORK_FRONTIER_MISSING"
                        or "HOSTED_ROOT_UNRESOLVED");
        }
    }

    [Fact]
    public void WorkspaceCatalogSeparatesRequestHandoffsFromSelfAdmittedTasks()
    {
        HostedProducerServiceEntry workspace = Assert.Single(HostedGrimoireProducerInventory.Catalog, static service => service.ServiceType == "WorkspaceIndexingService");

        Assert.Equal(["QueueIndexNow", "RegisterWorkspace"], workspace.Operations.Where(static operation => operation.Authority == HostedProducerAuthorityKind.FiniteRequest).Select(static operation => operation.Member).Order(StringComparer.Ordinal));

        Assert.All(workspace.Operations.Where(static operation => operation.Authority == HostedProducerAuthorityKind.FiniteRequest), static operation => Assert.Contains("tracked, self-admitted workspace handle", operation.Proof, StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionWholeGraphDiscoveryIsOwnedOnlyByMemoizedSupportFactories()
    {
        string testRoot = Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "tests",
            "RetroDownfall.Arcanum.Tests");

        string[] offenders = Directory
            .EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Support{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .SelectMany(path => CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), path: path)
                .GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(static method => method.DescendantNodes()
                    .OfType<MemberAccessExpressionSyntax>()
                    .Any(static access => access.Name.Identifier.ValueText
                        == "ProductionCompilations"))
                .Where(static method => method.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(static invocation => invocation.Expression
                        is MemberAccessExpressionSyntax access
                        && access.Name.Identifier.ValueText
                            == "DiscoverProducerSites"))
                .Select(method =>
                    $"{Path.GetRelativePath(testRoot, path)}:"
                    + $"{method.GetLocation().GetLineSpan().StartLinePosition.Line + 1}:"
                    + method.Identifier.ValueText))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryApplicationHostedServiceHasExactlyOneEntry()
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        HostedProducerInventoryValidation validation =
            HostedGrimoireProducerInventory.ValidateProductionTree();

        output.WriteLine($"Cold/in-process-first inventory: {stopwatch.Elapsed.TotalSeconds:F3}s");

        stopwatch.Restart();

        Assert.Same(validation, HostedGrimoireProducerInventory.ValidateProductionTree());

        output.WriteLine($"Warm inventory: {stopwatch.Elapsed.TotalMilliseconds:F3}ms");

        Assert.DoesNotContain(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code.StartsWith("HOSTED_SERVICE_", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionBatchCarrierHasOneExceptionSafePublicationLifetime()
    {
        _ = HostedGrimoireProducerInventory.Catalog.Single(
            static service => service.ServiceType == "BatchProcessingService");

        HostedProducerDiscovery<HostedProducerSite> discovery =
            HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        Assert.DoesNotContain(
            discovery.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE");
    }

    [Fact]
    public void ProductionDataRetentionSweepGraphRetainsItsExactWorkLease()
    {
        _ =
            HostedGrimoireProducerInventory.Catalog.Single(
                static service => service.ServiceType
                    == "DataRetentionSweepHostedService");

        HostedProducerDiscovery<HostedProducerSite> discovery =
            HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        Assert.DoesNotContain(
            discovery.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_ROOT_UNRESOLVED"
                    or "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Fact]
    public void ProductionDataRetentionSweepGraphRetainsItsCandidateEffectFrontier()
    {
        _ =
            HostedGrimoireProducerInventory.Catalog.Single(
                static service => service.ServiceType
                    == "DataRetentionSweepHostedService");

        HostedProducerDiscovery<HostedProducerSite> discovery =
            HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        Assert.DoesNotContain(
            discovery.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_ROOT_UNRESOLVED"
                    or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData("ApprenticeService")]
    [InlineData("BatchProcessingService")]
    [InlineData("GrimoireDatabaseHostedService")]
    [InlineData("McpServerBootstrapHostedService")]
    [InlineData("SagaExtractionService")]
    [InlineData("SessionAttachmentIndexingService")]
    [InlineData("UnseenServantService")]
    [InlineData("WorkspaceIndexingService")]
    public void ProductionOrdinaryHostedGraphsRetainTheirExactLifetimes(
        string serviceType)
    {
        _ =
            HostedGrimoireProducerInventory.Catalog.Single(
                candidate => candidate.ServiceType == serviceType);

        HostedProducerDiscovery<HostedProducerSite> discovery =
            HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerInventoryDiagnostic[] failures = discovery.Diagnostics
            .Where(static diagnostic => diagnostic.Code is
                "HOSTED_ROOT_UNRESOLVED"
                    or "HOSTED_SITE_UNCLASSIFIED"
                    or "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                    or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN")
            .ToArray();

        Assert.True(
            failures.Length == 0,
            string.Join(
                global::System.Environment.NewLine,
                failures.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Identity}: {diagnostic.Detail}")));
    }

    [Theory]
    [InlineData("BackupCommands", "Create")]
    [InlineData("BackupRestoreService", "RestoreAsync")]
    [InlineData("InstallationResetService", "ApplyFullUnderMaintenanceLockAsync")]
    [InlineData("GrimoireOfflineTransitionStartupRecovery", "RecoverBeforeBootstrapAsync")]
    public void ProductionNonHostedGraphsRetainTheirExactLifetimes(
        string enclosingType,
        string member)
    {
        _ =
            HostedGrimoireProducerInventory.NonHostedCatalog.Single(
                candidate => candidate.EnclosingType.EndsWith(
                        "." + enclosingType,
                        StringComparison.Ordinal)
                    && candidate.Member == member);

        HostedProducerDiscovery<HostedProducerSite> discovery =
            HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerInventoryDiagnostic[] failures = discovery.Diagnostics
            .Where(static diagnostic => diagnostic.Code is
                "HOSTED_ROOT_UNRESOLVED"
                    or "HOSTED_SITE_UNCLASSIFIED"
                    or "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                    or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN")
            .ToArray();

        Assert.True(
            failures.Length == 0,
            string.Join(
                global::System.Environment.NewLine,
                failures.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Identity}: {diagnostic.Detail}")));
    }

    [Fact]
    public void ProductionRecoveryGraphsRetainTheirClosedExactAuthority()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery =
            HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerInventoryDiagnostic[] failures = discovery.Diagnostics
            .Where(static diagnostic =>
                diagnostic.Code.StartsWith("HOSTED_RECOVERY_", StringComparison.Ordinal)
                || diagnostic.Code == "HOSTED_AGGREGATE_PROOF_MISSING")
            .ToArray();

        Assert.True(
            failures.Length == 0,
            string.Join(
                global::System.Environment.NewLine,
                failures.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Identity}: {diagnostic.Detail}")));
    }

    [Fact]
    public void AwaitedAggregateImplementationRetainsItsCallersWorkLease()
    {
        const string helper = "namespace RetroDownfall.Arcanum.Infrastructure.Data { internal interface IDataRetentionHostedSweep { Task ApplyOrResumeHostedPruneAsync(IGrimoireWorkLease workLease); } internal sealed class DataRetentionService : IDataRetentionHostedSweep { Task IDataRetentionHostedSweep.ApplyOrResumeHostedPruneAsync(IGrimoireWorkLease workLease) => ApplyOrResumeHostedPruneAsync(workLease); internal async Task ApplyOrResumeHostedPruneAsync(IGrimoireWorkLease workLease) { await PlanAsync(); } private static Task PlanAsync() { System.IO.File.Exists(\"path\"); return Task.CompletedTask; } } }";

        string body = AcquireWork.Replace(
                "return Task.CompletedTask;",
                "return;",
                StringComparison.Ordinal)
            + " RetroDownfall.Arcanum.Infrastructure.Data.IDataRetentionHostedSweep service = new RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService(); await service.ApplyOrResumeHostedPruneAsync(lease);";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(body, helper));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Fact]
    public void AwaitUsingStatementRetainsWorkAndEffectFrontiersAcrossAwaitedHelper()
    {
        const string body = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var admitted)) return; await using (RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease = admitted!) { if (!lease.TryBeginExternalEffectGroup(out var admittedGroup)) return; await using RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireExternalEffectGroup effectGroup = admittedGroup!; await Helper.RunAsync(); }";

        const string types = "namespace RetroDownfall.Arcanum.Infrastructure.Data { public enum GrimoireWorkKind { WorkspaceIndexing = 4 } public interface IGrimoireExternalEffectGroup : System.IAsyncDisposable { } public interface IGrimoireWorkLease : System.IAsyncDisposable { bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup group); } public interface IGrimoireConnectionAdmissionGate { bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease); } } internal static class Helper { internal static Task RunAsync() { System.IO.File.Delete(\"path\"); return Task.CompletedTask; } }";

        string source = RegistrationSource("services.AddHostedService<Worker>();")
            .Replace(
                "public Task StartAsync(CancellationToken token) => Task.CompletedTask;",
                "public async Task StartAsync(CancellationToken token) { " + body + " }",
                StringComparison.Ordinal)
            + types;

        HostedProducerDiscovery<HostedProducerSite> result =
            HostedGrimoireProducerInventory.DiscoverProducerSites(
                [Compile(source)],
                new(["Worker"], []),
                [new("Worker", [OrdinaryRoot()])],
                []);

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData("condition ? FirstAsync() : SecondAsync()")]
    [InlineData("condition switch { true => FirstAsync(), false => SecondAsync() }")]
    public void AwaitedReturnedTaskBranchRetainsItsCallersWorkLease(string route)
    {
        string helper = "internal static class Helper { internal static Task RouteAsync(bool condition) => "
            + route
            + "; private static Task FirstAsync() { System.IO.File.Exists(\"first\"); return Task.CompletedTask; } private static Task SecondAsync() { System.IO.Directory.Exists(\"second\"); return Task.CompletedTask; } }";

        string body = AcquireWork.Replace(
                "return Task.CompletedTask;",
                "return;",
                StringComparison.Ordinal)
            + " await Helper.RouteAsync(true);";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(body, helper));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ExactConstantControlArgumentSelectsTheExecutableProducerBranch(
        bool pendingJournalKnownUnstarted,
        bool effectExecutes)
    {
        const string helper = "namespace RetroDownfall.Arcanum.Infrastructure.Data { internal static class DataRetentionService { internal static Task RecoverPruneCoreAsync(bool pendingJournalKnownUnstarted) { if (!pendingJournalKnownUnstarted) System.IO.File.Delete(\"path\"); return Task.CompletedTask; } } }";

        string body = AcquireWork.Replace(
                "return Task.CompletedTask;",
                "return;",
                StringComparison.Ordinal)
            + " await RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverPruneCoreAsync("
            + pendingJournalKnownUnstarted.ToString().ToLowerInvariant()
            + ");";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(body, helper));

        Assert.Equal(
            effectExecutes,
            result.Items.Any(static site => site.Callee == "System.IO.File.Delete"));
    }

    [Fact]
    public void ExactEnumControlArgumentSurvivesAnExplicitInterfaceForwarder()
    {
        const string helper = "namespace RetroDownfall.Arcanum.Infrastructure.Mcp { internal enum McpGlobalInitializationAuthority { PreReadinessStartup, OrdinaryHostedWork } internal interface IMcpGlobalInitializationCoordinator { Task InitializeGlobalAsync(McpGlobalInitializationAuthority authority); } internal sealed class McpConnectionManager : IMcpGlobalInitializationCoordinator { Task IMcpGlobalInitializationCoordinator.InitializeGlobalAsync(McpGlobalInitializationAuthority authority) => EnsureGlobalLoadedAsync(authority); private static Task EnsureGlobalLoadedAsync(McpGlobalInitializationAuthority authority) => RunGlobalInitOperationAsync(authority); private static Task RunGlobalInitOperationAsync(McpGlobalInitializationAuthority authority) { if (authority == McpGlobalInitializationAuthority.PreReadinessStartup) System.IO.File.Exists(\"startup\"); else System.IO.File.Delete(\"ordinary\"); return Task.CompletedTask; } } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + " RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator coordinator = new RetroDownfall.Arcanum.Infrastructure.Mcp.McpConnectionManager(); await coordinator.InitializeGlobalAsync(RetroDownfall.Arcanum.Infrastructure.Mcp.McpGlobalInitializationAuthority.OrdinaryHostedWork);",
                helper));

        Assert.DoesNotContain(
            result.Items,
            static site => site.Callee == "System.IO.File.Exists");

        Assert.Single(
            result.Items,
            static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void EveryDiscoveredProducerSiteIsCataloguedExactlyOnce()
    {
        HostedProducerInventoryValidation validation =
            HostedGrimoireProducerInventory.ValidateProductionTree();

        HashSet<string> uncataloguedIdentities = validation.Diagnostics.Where(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCATALOGUED").Select(static diagnostic => diagnostic.Identity).ToHashSet(StringComparer.Ordinal);

        HostedProducerSite[] contextualSites = HostedGrimoireProducerInventory.ProductionSiteDiscovery.Items.Where(site => uncataloguedIdentities.Contains(HostedGrimoireProducerInventory.StableSiteIdentity(site))).ToArray();

        output.WriteLine($"Uncatalogued contextual sites: {contextualSites.Length}; unique physical capsules: {contextualSites.Select(static site => site.CapsuleId).Distinct(StringComparer.Ordinal).Count()}; capsules per exact operation root: {contextualSites.Select(static site => site.RootType + "|" + site.AuthorityOperationId + "|" + site.CapsuleId).Distinct(StringComparer.Ordinal).Count()}");

        foreach (IGrouping<HostedProducerSiteKind, HostedProducerSite> kind in contextualSites.GroupBy(static site => site.Kind).OrderBy(static group => group.Key))
        {
            output.WriteLine($"SITE_KIND {kind.Key}: {kind.Count()}");
        }

        foreach (IGrouping<string, HostedProducerSite> owner in contextualSites.GroupBy(static site => site.RootType + " | " + site.EnclosingType + "." + site.Member, StringComparer.Ordinal).OrderByDescending(static group => group.Count()).ThenBy(static group => group.Key, StringComparer.Ordinal).Take(10))
        {
            output.WriteLine($"SITE_OWNER {owner.Count()} | {owner.Key}");
        }

        foreach (IGrouping<string, HostedProducerInventoryDiagnostic> group in validation.Diagnostics.GroupBy(static diagnostic => diagnostic.Code))
        {
            output.WriteLine($"{group.Key}: {group.Count()}");

            foreach (IGrouping<string, HostedProducerInventoryDiagnostic> root in group
                .GroupBy(static diagnostic => diagnostic.Identity.Split('/', 2)[0], StringComparer.Ordinal)
                .OrderByDescending(static root => root.Count())
                .ThenBy(static root => root.Key, StringComparer.Ordinal)
                .Take(25))
            {
                output.WriteLine($"ROOT {group.Key} | {root.Count()} | {root.Key}");
            }

            if (group.Key is "HOSTED_CALL_TARGET_UNRESOLVED" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_SITE_UNCLASSIFIED")
            {
                int detailLimit = group.Key == "HOSTED_SITE_UNCLASSIFIED"
                    ? 1000
                    : 50;

                foreach (IGrouping<string, HostedProducerInventoryDiagnostic> detail in group.GroupBy(static diagnostic => diagnostic.Detail.Split(';')[0], StringComparer.Ordinal).OrderByDescending(static details => details.Count()).ThenBy(static details => details.Key, StringComparer.Ordinal).Take(detailLimit))
                {
                    output.WriteLine($"DETAIL {group.Key} | {detail.Count()} | {detail.Key}");
                }
            }

            if (group.Key is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                    or "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    or "HOSTED_SITE_EFFECT_FRONTIER_MISSING")
            {
                IGrouping<string, HostedProducerInventoryDiagnostic>[] physical = group.GroupBy(static diagnostic => diagnostic.Identity[(diagnostic.Identity.LastIndexOf('@') + 1)..] + " | " + diagnostic.Detail.Split(';')[0], StringComparer.Ordinal).ToArray();

                output.WriteLine($"{group.Key} unique physical anchors: {physical.Length}");

                foreach (IGrouping<string, HostedProducerInventoryDiagnostic> anchor in physical.Take(100))
                {
                    output.WriteLine($"PHYSICAL {group.Key} | {anchor.Count()} contexts | {anchor.Key}");
                }
            }

            int diagnosticLimit = group.Key is
                "HOSTED_SITE_UNCATALOGUED"
                    or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                    or "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                ? 25
                : 500;

            foreach (HostedProducerInventoryDiagnostic diagnostic in group.Take(diagnosticLimit))
            {
                string identity = diagnostic.Identity.Length <= 600
                    ? diagnostic.Identity
                    : "..." + diagnostic.Identity[^597..];

                output.WriteLine($"{identity}: {diagnostic.Detail}");
            }
        }

        Assert.True(validation.IsValid, "Every discovery, ownership, root, and site diagnostic must be closed by executable evidence before the production inventory is GREEN.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryScopeRequiresItsDeclaredWorkFrontier(bool frontier)
    {
        HostedProducerSite scope = new("Worker", "Worker.StartAsync/work@lease/site@src/Fixture.cs:20", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.ScopeCreation, "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope");

        HostedProducerSite lease = scope with { Kind = HostedProducerSiteKind.EffectFrontier, Callee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease", OperationId = "Worker.StartAsync/work@lease/workKind=WorkspaceIndexing/site@src/Fixture.cs:10" };

        HostedProducerSite[] sites = frontier ? [scope, lease] : [scope];

        HostedProducerOperationEntry operation = new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.WorkspaceIndexing, null, sites);

        HostedProducerInventoryValidation result = HostedGrimoireProducerInventory.Validate([new("Worker", [operation])], [], new(["Worker"], []), new(sites, []));

        Assert.Equal(frontier, result.IsValid);

        if (!frontier)
        {
            Assert.Contains(result.Diagnostics, static d => d.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
        }
    }

    [Theory]
    [InlineData("lease.TryBeginExternalEffectGroup(out var group);", false)]
    [InlineData("if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask;", true)]
    public void OnlyGuardedBoundEffectAdmissionIsFrontier(string admission, bool expected)
    {
        string source = FixtureSource("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease = null!; " + admission, "namespace RetroDownfall.Arcanum.Infrastructure.Data { public interface IGrimoireWorkLease { bool TryBeginExternalEffectGroup(out object group); } }");

        Assert.Equal(expected, Discover(source).Items.Any(static site => site.Kind == HostedProducerSiteKind.EffectFrontier));
    }

    [Fact]
    public void OverloadedStartIsExternalOperationRatherThanHostLifecycle()
    {
        string source = FixtureSource("").Replace("public Task StartAsync", "public void StartAsync(int job) { System.IO.File.Exists(\"path\"); } public Task StartAsync", StringComparison.Ordinal)
            + "class Endpoint { void Run(Worker worker) { worker.StartAsync(3); } }";

        Assert.Contains(Discover(source).Diagnostics, static d => d.Code == "HOSTED_EXTERNAL_OPERATION_UNCATALOGUED");
    }

    [Fact]
    public void TwoBranchesCallingSameHelperCollapseEquivalentAuthorityPaths()
    {
        string source = FixtureSource("if (DateTime.Now.Ticks > 0) Helper.Read(); else Helper.Read();", "static class Helper { public static void Read() { System.IO.File.Exists(\"path\"); } }");

        Assert.Single(Discover(source).Items, static site => site.Callee == "System.IO.File.Exists");
    }

    [Fact]
    public void SameHelperUnderAdmittedAndUnadmittedStatesRemainsDistinct()
    {
        const string helper = "static class Helper { public static void Read() { System.IO.File.Exists(\"path\"); } }";

        string body = "Helper.Read(); " + AcquireWork + " Helper.Read();";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(FixtureSource(body, AdmissionTypes + helper), OrdinaryRoot());

        Assert.Equal(2, result.Items.Count(static site => site.Callee == "System.IO.File.Exists"));

        Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Exists");
    }

    private static readonly string[] ExpectedHostedServices =
    [
        "GrimoireDatabaseHostedService",
        "CovenantFeatureConfigurationPublisher",
        "PidFileService",
        "FileEncryptionKeyBootstrapHostedService",
        "LongRunningOperationStartupHostedService",
        "SessionAttachmentPendingGcHostedService",
        "CovenantMaintenanceHostedService",
        "GrimoireSchemaTransitionHostedService",
        "EntryWeavingService",
        "SessionAttachmentIndexingService",
        "WorkspaceIndexingService",
        "SagaExtractionService",
        "TapestryWeavingService",
        "ArcanumSettingsClampStartupLogger",
        "ArcanumSecurityStartupChecks",
        "DataRetentionSweepHostedService",
        "A2ASendingLeaseRenewer",
        "Loremaster",
        "ApprenticeService",
        "McpServerBootstrapHostedService",
        "ProviderHealthProbeService",
        "UnseenServantService",
        "BatchProcessingService",
    ];

    [Fact]
    public void ProductionRegistrationsMatchRuntimeDescriptorsAndClosedVocabulary()
    {
        HostedProducerDiscovery<string> result = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices(HostedGrimoireProducerInventory.ProductionCompilations);

        Assert.Empty(result.Diagnostics);

        Assert.Equal(ExpectedHostedServices.Order(StringComparer.Ordinal), result.Items.Order(StringComparer.Ordinal));

        ServiceCollection services = [];

        services.AddArcanumApiServices(new ConfigurationBuilder().Build());

        ServiceDescriptor[] hosted = services.Where(static descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray();

        // AddDataProtection contributes this one framework-owned descriptor. No application descriptor,
        // factory descriptor, or unknown framework descriptor is excluded from the count.
        Assert.Single(hosted, static descriptor => descriptor.ImplementationType?.FullName == "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService" && descriptor.ImplementationType.Assembly.GetName().Name == "Microsoft.AspNetCore.DataProtection");

        Assert.Equal(result.Items.Count + 1, hosted.Length);
    }

    [Fact]
    public void ProductionIncludesEveryFirstPartyDependencyAndApiCliRoots()
    {
        Assert.Equal(["RetroDownfall.Arcanum.Core", "RetroDownfall.Arcanum.Secrets", "RetroDownfall.Arcanum.Infrastructure", "RetroDownfall.Arcanum.Api", "RetroDownfall.Arcanum.Cli"], HostedGrimoireProducerInventory.ProductionCompilations.Select(static compilation => compilation.AssemblyName));

        Assert.Contains(HostedGrimoireProducerInventory.NonHostedCatalog, static chain => chain.EnclosingType == "RetroDownfall.Arcanum.Cli.Commands.BackupCommands" && chain.Member == "Create");
    }

    [Fact]
    public void BackupLiveSourceHasExactCliCallerAuthority()
    {
        NonHostedProducerChainEntry chain = Assert.Single(HostedGrimoireProducerInventory.NonHostedCatalog, static entry => entry.EnclosingType == "RetroDownfall.Arcanum.Cli.Commands.BackupCommands" && entry.Member == "Create");

        Assert.Equal(HostedProducerAuthorityKind.OwnerBoundMaintenance, chain.Authority);

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerSite[] cliSites = discovery.Items.Where(site => site.RootType == chain.EnclosingType && site.OperationId.StartsWith(chain.ChainId, StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(cliSites);

        Assert.Contains(cliSites, static site => site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Backup.BackupDatabaseSnapshotter" && site.Callee == "System.IO.File.Move");
    }

    [Fact]
    public void RestoreSafetyBackupHasExactStoppedHostOrOwner()
    {
        NonHostedProducerChainEntry chain = Assert.Single(HostedGrimoireProducerInventory.NonHostedCatalog, static entry => entry.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService" && entry.Member == "RestoreAsync");

        Assert.Equal(HostedProducerAuthorityKind.StoppedHost, chain.Authority);

        Assert.Contains(HostedGrimoireProducerInventory.ProductionSiteDiscovery.Items, site => site.RootType == chain.EnclosingType && site.OperationId.StartsWith(chain.ChainId, StringComparison.Ordinal) && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Backup.BackupDatabaseSnapshotter" && site.Callee == "System.IO.File.Move");
    }

    [Fact]
    public void NonHostedBackupChainsDoNotEnterHostedRegistrationBijection()
    {
        HostedProducerDiscovery<string> registrations = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices(HostedGrimoireProducerInventory.ProductionCompilations);

        Assert.DoesNotContain(HostedGrimoireProducerInventory.NonHostedCatalog, chain => registrations.Items.Contains(chain.EnclosingType, StringComparer.Ordinal) || registrations.Items.Contains(chain.EnclosingType.Split('.').Last(), StringComparer.Ordinal));

        Assert.DoesNotContain(HostedGrimoireProducerInventory.Validate(HostedGrimoireProducerInventory.Catalog, HostedGrimoireProducerInventory.NonHostedCatalog, registrations, HostedGrimoireProducerInventory.ProductionSiteDiscovery).Diagnostics, static diagnostic => diagnostic.Code.StartsWith("HOSTED_SERVICE_", StringComparison.Ordinal) && diagnostic.Identity.Contains("Backup", StringComparison.Ordinal));
    }

    [Fact]
    public void LiveSourceCannotBecomeReachableFromHostedOrUnadmittedCaller()
    {
        HostedProducerSite[] liveSourceSites = HostedGrimoireProducerInventory.ProductionSiteDiscovery.Items.Where(static site => site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Backup.BackupDatabaseSnapshotter").ToArray();

        Assert.NotEmpty(liveSourceSites);

        Assert.All(liveSourceSites, static site => Assert.Contains(site.RootType, new[] { "RetroDownfall.Arcanum.Cli.Commands.BackupCommands", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService" }));
    }

    [Fact]
    public void SchemaBackfillStrategyTableMatchesEveryConfiguredConcreteImplementation()
    {
        CSharpCompilation infrastructure = Assert.Single(HostedGrimoireProducerInventory.ProductionCompilations, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        VariableDeclaratorSyntax declaration = Assert.Single(infrastructure.SyntaxTrees.SelectMany(static tree => tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()), static variable => variable.Identifier.ValueText == "Backfills" && variable.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == "GrimoireSchemaVersionChains");

        SemanticModel model = infrastructure.GetSemanticModel(declaration.SyntaxTree);

        string[] configured = declaration.Initializer!.Value.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>()
            .Select(creation => model.GetTypeInfo(creation).Type)
            .OfType<INamedTypeSymbol>()
            .Where(static type => type.AllInterfaces.Any(static contract => contract.ToDisplayString() == "RetroDownfall.Arcanum.Infrastructure.Data.Schema.IGrimoireSchemaBackfill"))
            .Select(static type => type.ToDisplayString())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(HostedGrimoireProducerInventory.SchemaBackfillStrategies.Order(StringComparer.Ordinal), configured);
    }

    [Fact]
    public void TwoCallsInOneMemberHaveDistinctSiteIdentities()
    {
        HostedProducerSite[] sites = Discover(FixtureSource("System.IO.File.Exists(\"a\"); System.IO.File.Exists(\"b\");")).Items.Where(static site => site.Callee == "System.IO.File.Exists").ToArray();

        Assert.Equal(2, sites.Length);
    }

    [Fact]
    public void CapsuleIdentityIgnoresTriviaButRejectsOneTokenMutation()
    {
        HostedProducerSite original = Assert.Single(Discover(FixtureSource("System.IO.File.Exists(\"a\");")).Items, static site => site.Callee == "System.IO.File.Exists");
        HostedProducerSite triviaOnly = Assert.Single(Discover(FixtureSource("/* review note */ System.IO.File . Exists ( \"a\" );")).Items, static site => site.Callee == "System.IO.File.Exists");
        HostedProducerSite mutation = Assert.Single(Discover(FixtureSource("System.IO.File.Exists(\"b\");")).Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Equal(original.CapsuleId, triviaOnly.CapsuleId);
        Assert.NotEqual(original.CapsuleId, mutation.CapsuleId);
    }

    [Fact]
    public void IdenticalBoundarySyntaxInDifferentLambdasHasDistinctCapsules()
    {
        string body = "Action first = () => { System.IO.File.Exists(\"a\"); }; Action second = () => { System.IO.File.Exists(\"a\"); }; first(); second();";

        HostedProducerSite[] sites = Discover(FixtureSource(body)).Items.Where(static site => site.Callee == "System.IO.File.Exists").ToArray();

        Assert.Equal(2, sites.Length);
        Assert.Equal(2, sites.Select(static site => site.CapsuleId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void StableCatalogComparisonRejectsNewAndChangedCapsules()
    {
        HostedProducerSite original = Assert.Single(Discover(FixtureSource("System.IO.File.Exists(\"a\");")).Items, static site => site.Callee == "System.IO.File.Exists");
        HostedProducerSite changed = Assert.Single(Discover(FixtureSource("System.IO.File.Exists(\"b\");")).Items, static site => site.Callee == "System.IO.File.Exists");
        HostedProducerOperationEntry operation = OrdinaryRoot() with { Sites = [original] };

        HostedProducerInventoryValidation validation = HostedGrimoireProducerInventory.Validate([new("Worker", [operation])], [], new(["Worker"], []), new([changed], []));

        Assert.Contains(validation.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCATALOGUED");
        Assert.Contains(validation.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_STALE");
    }

    [Fact]
    public void CapsuleManifestRejectsMalformedDuplicateAndUnresolvedFrontierRows()
    {
        HostedProducerSite site = Assert.Single(Discover(FixtureSource("System.IO.File.Exists(\"a\");")).Items, static candidate => candidate.Callee == "System.IO.File.Exists");
        string valid = HostedGrimoireProducerInventory.RenderCapsuleManifest([site]);
        string[] lines = valid.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Empty(HostedGrimoireProducerInventory.ParseCapsuleManifest(lines).Diagnostics);

        string anonymousMemberManifest = HostedGrimoireProducerInventory.RenderCapsuleManifest([site with { Member = string.Empty }]);
        HostedProducerCapsuleManifest anonymousMember = HostedGrimoireProducerInventory.ParseCapsuleManifest(anonymousMemberManifest.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Empty(anonymousMember.Diagnostics);
        Assert.Equal(string.Empty, Assert.Single(anonymousMember.Sites).Member);

        HostedProducerSite anonymousSite = Assert.Single(anonymousMember.Sites);
        HostedProducerOperationEntry anonymousOperation = OrdinaryRoot() with
        {
            Authority = HostedProducerAuthorityKind.PreReadinessStartup,
            WorkKind = null,
            Proof = "Worker.StartAsync: readiness",
            Sites = [anonymousSite],
        };

        HostedProducerInventoryValidation anonymousValidation = HostedGrimoireProducerInventory.Validate(
            [new("Worker", [anonymousOperation])],
            [],
            new(["Worker"], []),
            new([anonymousSite], []));

        Assert.DoesNotContain(anonymousValidation.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_IDENTITY_INVALID");

        HostedProducerCapsuleManifest duplicate = HostedGrimoireProducerInventory.ParseCapsuleManifest([.. lines, lines[^1]]);
        Assert.Contains(duplicate.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_ROW_DUPLICATE");

        string[] fields = lines[^1].Split('\t');
        fields[10] = new string('A', 64);
        HostedProducerCapsuleManifest unresolved = HostedGrimoireProducerInventory.ParseCapsuleManifest([.. lines[..^1], string.Join('\t', fields)]);
        Assert.Contains(unresolved.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_FRONTIER_UNRESOLVED");

        HostedProducerCapsuleManifest malformed = HostedGrimoireProducerInventory.ParseCapsuleManifest(["# wrong-version", "# wrong-columns", "broken"]);
        Assert.Contains(malformed.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_VERSION_INVALID");
        Assert.Contains(malformed.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_COLUMNS_INVALID");
        Assert.Contains(malformed.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_ROW_INVALID");
    }

    [Fact]
    public void ReviewedCapsuleManifestExactlyMatchesProductionDiscovery()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        Assert.Empty(discovery.Diagnostics);

        string expected = HostedGrimoireProducerInventory.RenderCapsuleManifest(discovery.Items);

        if (global::System.Environment.GetEnvironmentVariable("ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES") == "1")
        {
            File.WriteAllText(HostedGrimoireProducerInventory.CapsuleManifestPath, expected);
        }

        Assert.Equal(expected, File.ReadAllText(HostedGrimoireProducerInventory.CapsuleManifestPath));
    }

    [Fact]
    public void PublicationIncludesWriterCallsAndImplicitDisposal()
    {
        string source = FixtureSource("using var writer = new System.IO.StreamWriter(System.IO.Stream.Null); writer.Write(\"body\"); writer.Flush();");

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.StreamWriter.Write" && site.Kind == HostedProducerSiteKind.FileSystemEffect);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.StreamWriter.Flush" && site.Kind == HostedProducerSiteKind.FileSystemEffect);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.StreamWriter.Dispose" && site.Kind == HostedProducerSiteKind.FileSystemEffect);
    }

    [Fact]
    public void ConcreteProviderCallNormalizesToItsInterfaceSlot()
    {
        string source = FixtureSource("new RetroDownfall.Arcanum.Core.Weave.Weave().EmbedAsync();", "namespace RetroDownfall.Arcanum.Core.Weave { interface IWeaveService { void EmbedAsync(); } class Weave : IWeaveService { public void EmbedAsync() {} } }");

        Assert.Contains(Discover(source).Items, static site => site.Callee == "RetroDownfall.Arcanum.Core.Weave.IWeaveService.EmbedAsync");
    }

    [Fact]
    public void MarkedConnectionRouteIsJoinedThroughItsInterfaceBinding()
    {
        string source = FixtureSource("IRoute route = null!; route.Open();", "class GrimoireConnectionAcquisitionRouteAttribute : System.Attribute {} interface IRoute { void Open(); } class Route : IRoute { [GrimoireConnectionAcquisitionRoute] public void Open() {} }")
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); services.AddSingleton<IRoute, Route>();", StringComparison.Ordinal);

        Assert.Contains(Discover(source).Items, static site => site.Kind == HostedProducerSiteKind.OrdinaryConnectionRoute);
    }

    [Fact]
    public void IncompleteBlobPublicationIsRejected()
    {
        HostedProducerSite site = new("Worker", "Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.FileSystemEffect, "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync");

        HostedProducerOperationEntry operation = new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.BatchProcessing, null, [site]);

        HostedProducerInventoryValidation result = HostedGrimoireProducerInventory.Validate([new("Worker", [operation])], [], new(["Worker"], []), new([site], []));

        Assert.Contains(result.Diagnostics, static item => item.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE");
    }

    [Theory]
    [InlineData("Microsoft.Extensions.AI.IChatClient.GetResponseAsync", 3)]
    [InlineData("Microsoft.Extensions.AI.IChatClient.GetStreamingResponseAsync", 3)]
    [InlineData("Microsoft.Extensions.AI.IEmbeddingGenerator`2.GenerateAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Intelligence.IModelCallExecutor.ExecuteBufferedAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Intelligence.IModelCallExecutor.ExecuteStreamingAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Intelligence.IArcanumIntelligenceProvider.ExecutePromptAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Intelligence.IArcanumIntelligenceProvider.StreamPromptAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Weave.IWeaveService.EmbedAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Weave.IWeaveService.EmbedBatchAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Weave.Tapestry.ITapestrySummarizer.SummarizeAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Resilience.IProviderHealthProbe.ProbeAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonRunner.RunScheduledAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonJob.RunAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Weave.TapestryWeaver.WeaveAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StartAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StopAllAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Api.OpenAiV1Endpoints.ExecuteChatRequestForBatchAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.A2A.IA2AClientService.CancelRemoteTaskAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.OpenReadAsync", 4)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.InspectAsync", 4)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.HasEnvelope", 4)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.EncryptedBlobStoreCompatibilityExtensions.OpenCompatibleReadAsync", 4)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore.OpenReadAsync", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryCapturePath", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryCaptureOpenFile", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.TryGetPathIdentity", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.TryGetHandleIdentity", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.RunStartupPermissionSelfCheck", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Hosting.IWorkspaceFileWatcherFactory.Create", 4)]
    [InlineData("System.IO.Directory.Exists", 4)]
    [InlineData("System.IO.Directory.EnumerateDirectories", 4)]
    [InlineData("System.IO.Directory.EnumerateFileSystemEntries", 4)]
    [InlineData("System.IO.Directory.ResolveLinkTarget", 4)]
    [InlineData("System.IO.Directory.GetFileSystemEntries", 4)]
    [InlineData("System.IO.File.Exists", 4)]
    [InlineData("System.IO.File.GetAttributes", 4)]
    [InlineData("System.IO.File.GetLastWriteTimeUtc", 4)]
    [InlineData("System.IO.File.GetUnixFileMode", 4)]
    [InlineData("System.IO.File.OpenRead", 4)]
    [InlineData("System.IO.File.ReadAllBytes", 4)]
    [InlineData("System.IO.File.ReadAllBytesAsync", 4)]
    [InlineData("System.IO.File.ReadAllText", 4)]
    [InlineData("System.IO.File.ReadAllTextAsync", 4)]
    [InlineData("System.IO.File.ResolveLinkTarget", 4)]
    [InlineData("System.IO.FileStream.Length", 4)]
    [InlineData("System.IO.FileStream.Read", 4)]
    [InlineData("System.IO.FileStream.ReadAsync", 4)]
    [InlineData("System.IO.FileInfo.Length", 4)]
    [InlineData("System.IO.FileInfo.LastWriteTimeUtc", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.GetFileInformationByHandle", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.NtOpenFile", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.OpenAtUnix", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.OpenUnix", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.GetEffectiveUserIdNative", 4)]
    [InlineData("RetroDownfall.Arcanum.Secrets.Security.IOsCredentialPresenceProbe.ProbePresence", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.fstat", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.lstat", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.stat", 4)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.WriteAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.CompleteAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore.ReconcileAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyProvider.GetForWriteAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyRing.RotateAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyRing.RetireAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryDelete", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryQuarantine", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryDeleteQuarantined", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryRestoreQuarantined", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionFileProcessor.MigrateAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionFileProcessor.ReencryptAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Backup.OwnedTemporaryDirectory.TryDelete", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.DataLifecycle.IDataRetentionService.ApplyAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IUploadedFileRepository.CreateForOwnedFileAsync", 5)]
    [InlineData("System.IO.Directory.CreateDirectory", 5)]
    [InlineData("System.IO.Directory.Delete", 5)]
    [InlineData("System.IO.Directory.Move", 5)]
    [InlineData("System.IO.File.Copy", 5)]
    [InlineData("System.IO.File.WriteAllText", 5)]
    [InlineData("System.IO.File.Delete", 5)]
    [InlineData("System.IO.File.Move", 5)]
    [InlineData("System.IO.File.SetUnixFileMode", 5)]
    [InlineData("System.IO.FileStream.DisposeAsync", 5)]
    [InlineData("System.IO.FileStream.Flush", 5)]
    [InlineData("System.IO.FileStream.FlushAsync", 5)]
    [InlineData("System.IO.FileStream.SetLength", 5)]
    [InlineData("System.IO.FileStream.Write", 5)]
    [InlineData("System.IO.FileStream.WriteAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.EnsureOwnerOnlyDirectoryExists", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.ApplyOwnerOnlyFile", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.ApplyOwnerOnlyDirectory", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.TryEnsureOwnerOnlyDirectoryExistsStrict", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.TryApplyOwnerOnlyFileStrict", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.CreateOwnerOnlyTempFile", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.ApplyOwnerOnlyToSensitivePaths", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.TryApplyUnixFileMode", 5)]
    public void EveryClosedVocabularyMemberIsDiscovered(string symbol, int kind)
    {
        int memberSeparator = symbol.LastIndexOf('.');

        string type = symbol[..memberSeparator];

        string member = symbol[(memberSeparator + 1)..];

        int typeSeparator = type.LastIndexOf('.');

        string space = type[..typeSeparator];

        string name = type[(typeSeparator + 1)..];

        string declaration = name.Replace("`2", "<T, U>", StringComparison.Ordinal);

        string constructed = type.Replace("`2", "<object, object>", StringComparison.Ordinal);

        string source = FixtureSource($"new {constructed}().{member}();", $"namespace {space} {{ public class {declaration} {{ public void {member}() {{ }} }} }}");

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Contains(result.Items, site => site.Callee == symbol && (int)site.Kind == kind);
    }

    [Theory]
    [InlineData("System.IO.File.Exists(\"path\");", HostedProducerSiteKind.FileSystemRead, "System.IO.File.Exists")]
    [InlineData("System.IO.File.Delete(\"path\");", HostedProducerSiteKind.FileSystemEffect, "System.IO.File.Delete")]
    [InlineData("_ = new System.IO.FileInfo(\"path\").Length;", HostedProducerSiteKind.FileSystemRead, "System.IO.FileInfo.Length")]
    [InlineData("_ = new System.IO.FileStream(\"path\", System.IO.FileMode.Open, System.IO.FileAccess.Read);", HostedProducerSiteKind.FileSystemRead, "System.IO.FileStream..ctor")]
    [InlineData("_ = new System.IO.FileStream(\"path\", System.IO.FileMode.OpenOrCreate);", HostedProducerSiteKind.FileSystemEffect, "System.IO.FileStream..ctor")]
    [InlineData("_ = System.IO.File.Open(\"path\", System.IO.FileMode.Open, System.IO.FileAccess.Read);", HostedProducerSiteKind.FileSystemRead, "System.IO.File.Open")]
    [InlineData("_ = System.IO.File.Open(\"path\", System.IO.FileMode.Create);", HostedProducerSiteKind.FileSystemEffect, "System.IO.File.Open")]
    [InlineData("using var scope = new ServiceCollection().BuildServiceProvider().CreateScope();", HostedProducerSiteKind.ScopeCreation, "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope")]
    public void TraversalClassifiesBoundSites(string body, object kind, string callee)
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource(body));

        Assert.Contains(result.Items, site => site.Kind == (HostedProducerSiteKind)kind && site.Callee == callee && site.RootType == "Worker" && site.Member == "StartAsync");
    }

    [Theory]
    [InlineData("_ = System.IO.Directory.GetFileSystemEntries(\"path\");", HostedProducerSiteKind.FileSystemRead, "System.IO.Directory.GetFileSystemEntries")]
    [InlineData("System.IO.File.Copy(\"from\", \"to\");", HostedProducerSiteKind.FileSystemEffect, "System.IO.File.Copy")]
    [InlineData("_ = System.IO.File.GetLastWriteTimeUtc(\"path\");", HostedProducerSiteKind.FileSystemRead, "System.IO.File.GetLastWriteTimeUtc")]
    [InlineData("_ = System.IO.File.ReadAllTextAsync(\"path\").GetAwaiter().GetResult();", HostedProducerSiteKind.FileSystemRead, "System.IO.File.ReadAllTextAsync")]
    [InlineData("_ = System.IO.File.ResolveLinkTarget(\"path\", false);", HostedProducerSiteKind.FileSystemRead, "System.IO.File.ResolveLinkTarget")]
    [InlineData("System.IO.File.SetUnixFileMode(\"path\", (System.IO.UnixFileMode)0);", HostedProducerSiteKind.FileSystemEffect, "System.IO.File.SetUnixFileMode")]
    [InlineData("using var stream = new System.IO.FileStream(\"path\", System.IO.FileMode.OpenOrCreate); _ = stream.Read(new byte[1], 0, 1);", HostedProducerSiteKind.FileSystemRead, "System.IO.FileStream.Read")]
    [InlineData("var stream = new System.IO.FileStream(\"path\", System.IO.FileMode.OpenOrCreate); stream.DisposeAsync().AsTask().GetAwaiter().GetResult();", HostedProducerSiteKind.FileSystemEffect, "System.IO.FileStream.DisposeAsync")]
    [InlineData("using var stream = new System.IO.FileStream(\"path\", System.IO.FileMode.OpenOrCreate); stream.SetLength(0);", HostedProducerSiteKind.FileSystemEffect, "System.IO.FileStream.SetLength")]
    public void ProductionFileSystemVocabularyIsClosedOverObservedMembers(string body, object kind, string callee)
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource(body));

        Assert.Contains(result.Items, site => site.Kind == (HostedProducerSiteKind)kind && site.Callee == callee);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == callee);
    }

    [Fact]
    public void ExternalCapabilityInterfacesAreClassifiedAtTheirExactBoundary()
    {
        const string capabilities = """
            namespace RetroDownfall.Arcanum.Core.Security
            {
                public interface IDnsResolver
                {
                    Task<System.Net.IPAddress[]> GetHostAddressesAsync(
                        string host,
                        CancellationToken cancellationToken = default);
                }
            }

            namespace RetroDownfall.Arcanum.Infrastructure.Mcp
            {
                public interface IMcpClient
                {
                    Task InitializeAsync(CancellationToken cancellationToken = default);

                    Task<object> GetToolsAsync(CancellationToken cancellationToken = default);
                }
            }

            namespace RetroDownfall.Arcanum.Secrets.Security
            {
                public interface IHostProcessToolsMarkerCredentialCapabilitySource
                {
                    object OpenFixedSlot();

                    object ProveFixedSlotDurablyAbsent();
                }

                public interface IHostProcessToolsMarkerNativeRecordCapability
                {
                    object CompareDeleteExact(ReadOnlySpan<byte> expected);
                }
            }
            """;

        string body = R2Admission
            + "RetroDownfall.Arcanum.Core.Security.IDnsResolver dns = null!; "
            + "_ = await dns.GetHostAddressesAsync(\"host\", token); "
            + "RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpClient mcp = null!; "
            + "await mcp.InitializeAsync(token); _ = await mcp.GetToolsAsync(token); "
            + "RetroDownfall.Arcanum.Secrets.Security.IHostProcessToolsMarkerCredentialCapabilitySource source = null!; "
            + "_ = source.OpenFixedSlot(); _ = source.ProveFixedSlotDurablyAbsent(); "
            + "RetroDownfall.Arcanum.Secrets.Security.IHostProcessToolsMarkerNativeRecordCapability record = null!; "
            + "_ = record.CompareDeleteExact(new byte[1]);";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(body, capabilities));

        Assert.Contains(
            result.Items,
            static site => site.Kind == HostedProducerSiteKind.ProviderCall
                && site.Callee == "RetroDownfall.Arcanum.Core.Security.IDnsResolver.GetHostAddressesAsync");

        Assert.Equal(
            2,
            result.Items.Count(static site =>
                site.Kind == HostedProducerSiteKind.ProviderCall
                && site.Callee.StartsWith(
                    "RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpClient.",
                    StringComparison.Ordinal)));

        Assert.Equal(
            2,
            result.Items.Count(static site =>
                site.Kind == HostedProducerSiteKind.FileSystemRead
                && site.Callee.StartsWith(
                    "RetroDownfall.Arcanum.Secrets.Security.IHostProcessToolsMarkerCredentialCapabilitySource.",
                    StringComparison.Ordinal)));

        Assert.Contains(
            result.Items,
            static site => site.Kind == HostedProducerSiteKind.FileSystemEffect
                && site.Callee == "RetroDownfall.Arcanum.Secrets.Security.IHostProcessToolsMarkerNativeRecordCapability.CompareDeleteExact");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    [Fact]
    public void FileStreamHandleMetadataIsPureButPositionMutationIsAnEffect()
    {
        const string body = "using var stream = new System.IO.FileStream(\"path\", System.IO.FileMode.OpenOrCreate); _ = stream.Name; _ = stream.Position; _ = stream.SafeFileHandle; stream.Position = 0;";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource(body));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.FileStream.Position setter" && site.Kind == HostedProducerSiteKind.FileSystemEffect);

        Assert.DoesNotContain(result.Items, static site => site.Callee is "System.IO.FileStream.Name" or "System.IO.FileStream.Position" or "System.IO.FileStream.SafeFileHandle");
    }

    [Fact]
    public void SharedHelperKeepsTwoAuthorityRootIdentities()
    {
        string source = FixtureSource("Helper.Read();", "static class Helper { public static void Read() { System.IO.File.Exists(\"path\"); } }")
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "public Task StopAsync(CancellationToken token) { Helper.Read(); return Task.CompletedTask; }", StringComparison.Ordinal);

        HostedProducerSite[] sites = Discover(source).Items.Where(static site => site.Callee == "System.IO.File.Exists").ToArray();

        Assert.Equal(2, sites.Length);

        Assert.Equal(2, sites.Select(static site => site.OperationId).Distinct().Count());
    }

    [Theory]
    [InlineData("using var stream = new System.IO.FileStream(\"path\", System.IO.FileMode.OpenOrCreate); stream.Lock(0, 1);", "", "HOSTED_SITE_UNCLASSIFIED")]
    [InlineData("dynamic x = null!; x.Run();", "", "HOSTED_CALL_TARGET_UNRESOLVED")]
    [InlineData("IHelper x = null!; x.Run();", "interface IHelper { void Run(); }", "HOSTED_CALL_TARGET_UNRESOLVED")]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator x = null!; x.InitializeGlobalAsync();", "namespace RetroDownfall.Arcanum.Infrastructure.Mcp { public interface IMcpGlobalInitializationCoordinator { void InitializeGlobalAsync(); } }", "HOSTED_AGGREGATE_PROOF_MISSING")]
    public void TraversalFailsClosed(string body, string extra, string diagnostic)
    {
        Assert.Contains(Discover(FixtureSource(body, extra)).Diagnostics, item => item.Code == diagnostic);
    }

    [Theory]
    [InlineData("using var client = new System.Net.Http.HttpClient(); _ = client.SendAsync(new System.Net.Http.HttpRequestMessage()).GetAwaiter().GetResult();", "System.Net.Http.HttpClient.SendAsync", HostedProducerSiteKind.ProviderCall)]
    [InlineData("using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp); socket.ConnectAsync(\"localhost\", 80).GetAwaiter().GetResult();", "System.Net.Sockets.Socket.ConnectAsync", HostedProducerSiteKind.ProviderCall)]
    [InlineData("_ = System.Diagnostics.Process.Start(\"echo\");", "System.Diagnostics.Process.Start", HostedProducerSiteKind.ProviderCall)]
    [InlineData("Microsoft.Win32.SafeHandles.SafeFileHandle handle = null!; System.IO.RandomAccess.Write(handle, new byte[1], 0);", "System.IO.RandomAccess.Write", HostedProducerSiteKind.FileSystemEffect)]
    [InlineData("using var watcher = new System.IO.FileSystemWatcher(\".\"); watcher.EnableRaisingEvents = true;", "System.IO.FileSystemWatcher.EnableRaisingEvents setter", HostedProducerSiteKind.FileSystemRead)]
    public void KnownExternalCapabilitiesHaveConservativeProducerClassifications(
        string body,
        string boundary,
        object kind)
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource(body));

        Assert.Contains(
            result.Items,
            site => site.Callee == boundary
                && site.Kind == (HostedProducerSiteKind)kind);

        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == boundary);
    }

    [Fact]
    public void UnreviewedExternalCapabilityStillFailsClosed()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("using var ping = new System.Net.NetworkInformation.Ping(); _ = ping.Send(\"localhost\");"));

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Net.NetworkInformation.Ping.Send");
    }

    [Fact]
    public void EnumeratorOperationsRequireExactSourceProvenance()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("System.Collections.Generic.IAsyncEnumerable<int> source = null!; await using var enumerator = source.GetAsyncEnumerator(token); _ = await enumerator.MoveNextAsync(); _ = enumerator.Current;"));

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Collections.Generic.IAsyncEnumerable`1.GetAsyncEnumerator");

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Collections.Generic.IAsyncEnumerator`1.MoveNextAsync");
    }

    [Fact]
    public void EnumeratorOperationsAcceptAnExactAuthoredSource()
    {
        const string helper = "static async System.Collections.Generic.IAsyncEnumerable<int> Values() { await Task.CompletedTask; yield return 1; }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("await using var enumerator = Values().GetAsyncEnumerator(token); _ = await enumerator.MoveNextAsync(); _ = enumerator.Current;", helper));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.Contains(
                    "Enumerator",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void EnumeratorOperationsAcceptOneExactAssignmentFromACataloguedSource()
    {
        const string body = "System.Collections.Generic.IEnumerator<string>? enumerator = null; try { enumerator = System.IO.Directory.EnumerateFileSystemEntries(\".\").GetEnumerator(); } catch (System.IO.IOException) { return; } using (enumerator) { if (enumerator.MoveNext()) _ = enumerator.Current; }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(body));

        Assert.Contains(
            result.Items,
            static site => site.Callee == "System.IO.Directory.EnumerateFileSystemEntries"
                && site.Kind == HostedProducerSiteKind.FileSystemRead);

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.Contains(
                    "Enumerator",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void StreamReaderClassificationUsesExactStreamProvenance()
    {
        const string embedded = "using System.IO.Stream stream = typeof(Worker).Assembly.GetManifestResourceStream(\"fixture\")!; using System.IO.StreamReader reader = new(stream); _ = reader.ReadToEnd();";

        HostedProducerDiscovery<HostedProducerSite> embeddedResult = Discover(
            FixtureSource(embedded));

        Assert.DoesNotContain(
            embeddedResult.Items,
            static site => site.Callee is
                "System.IO.StreamReader.ReadToEnd"
                    or "System.IO.Stream.Dispose");

        Assert.DoesNotContain(
            embeddedResult.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.IO.StreamReader.ReadToEnd");

        HostedProducerDiscovery<HostedProducerSite> fileResult = Discover(
            FixtureSource("using System.IO.StreamReader reader = new(System.IO.File.OpenRead(\"path\")); _ = reader.ReadToEnd();"));

        Assert.Contains(
            fileResult.Items,
            static site => site.Callee == "System.IO.StreamReader.ReadToEnd"
                && site.Kind == HostedProducerSiteKind.FileSystemRead);
    }

    [Fact]
    public void StreamReaderClassificationRetainsExactProvenanceAcrossAuthoredCalls()
    {
        const string helper = "static string Read(System.IO.Stream stream) { using System.IO.StreamReader reader = new(stream); return reader.ReadToEnd(); }";

        HostedProducerDiscovery<HostedProducerSite> embedded = Discover(
            FixtureSource(
                "using System.IO.Stream stream = typeof(Worker).Assembly.GetManifestResourceStream(\"fixture\")!; _ = Read(stream);",
                helper));

        Assert.DoesNotContain(
            embedded.Items,
            static site => site.Callee == "System.IO.StreamReader.ReadToEnd");

        Assert.DoesNotContain(
            embedded.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.IO.StreamReader.ReadToEnd");

        HostedProducerDiscovery<HostedProducerSite> file = Discover(
            FixtureSource(
                "using System.IO.Stream stream = System.IO.File.OpenRead(\"path\"); _ = Read(stream);",
                helper));

        Assert.Contains(
            file.Items,
            static site => site.Callee == "System.IO.StreamReader.ReadToEnd"
                && site.Kind == HostedProducerSiteKind.FileSystemRead);
    }

    [Fact]
    public void EmbeddedSchemaLazyFactoryIsBoundedInProcessWork()
    {
        const string schema = "static class SchemaCatalog { private static readonly Lazy<string> Loaded = new(Load, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); public static string Value => Loaded.Value; private static string Load() { using System.IO.Stream stream = typeof(SchemaCatalog).Assembly.GetManifestResourceStream(\"fixture\")!; using System.IO.StreamReader reader = new(stream); return reader.ReadToEnd(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("_ = SchemaCatalog.Value;", schema));

        Assert.DoesNotContain(
            result.Items,
            static site => site.Kind is
                HostedProducerSiteKind.FileSystemRead
                    or HostedProducerSiteKind.FileSystemEffect);

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_UNCLASSIFIED");
    }

    [Fact]
    public void LazyFactoryResolutionDoesNotFallBackToAnotherSameTypedField()
    {
        const string values = "static class LazyValues { private static readonly Lazy<bool> Known = new(() => true, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); private static readonly Lazy<bool> Unknown = Create(); public static bool Value => Unknown.Value; private static Lazy<bool> Create() => throw new InvalidOperationException(); }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("_ = LazyValues.Value;", values));

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Lazy`1.Value");
    }

    [Fact]
    public void LazyFactoryResolutionKeepsSameTypedFieldsIndependent()
    {
        const string values = "static class LazyValues { private static readonly Lazy<bool> Safe = new(() => true, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); private static readonly Lazy<bool> Dangerous = new(() => { System.IO.File.Delete(\"path\"); return true; }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); public static bool Value => Safe.Value; }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("_ = LazyValues.Value;", values));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_UNCLASSIFIED");

        Assert.DoesNotContain(
            result.Items,
            static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void LazyBoundedDataProofRequiresExecutionAndPublication()
    {
        const string values = "static class LazyValues { private static readonly Lazy<bool> Loaded = new(() => true, System.Threading.LazyThreadSafetyMode.PublicationOnly); public static bool Value => Loaded.Value; }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("_ = LazyValues.Value;", values));

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Lazy`1.Value");
    }

    [Theory]
    [InlineData("private static int count; private static readonly Lazy<int> Loaded = new(() => ++count, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); public static int Value => Loaded.Value;")]
    [InlineData("private static readonly Lazy<Task> Loaded = new(() => Task.Run(static () => { }), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); public static Task Value => Loaded.Value;")]
    [InlineData("private static readonly Lazy<IEnumerable<int>> Loaded = new(() => System.Linq.Enumerable.Where(new[] { 1 }, static value => value > 0), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); public static IEnumerable<int> Value => Loaded.Value;")]
    public void LazyProducerOrDeferredStateIsNotTreatedAsBoundedData(string members)
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(
                "_ = LazyValues.Value;",
                "static class LazyValues { " + members + " }"));

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Detail.StartsWith(
                    "System.Lazy`1..ctor;",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void NestedLazyBoundedDataFactoriesAreProvenIndependently()
    {
        const string values = "static class LazyValues { private static readonly Lazy<string> Inner = new(() => \"inner\", System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); private static readonly Lazy<string> Outer = new(() => Inner.Value + \" outer\", System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); public static string Value => Outer.Value; }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("_ = LazyValues.Value;", values));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_UNCLASSIFIED");
    }

    [Fact]
    public void ExactStableLocalLazyBoundedDataIsProven()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(
                "var value = new Lazy<bool>(() => true, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); _ = value.Value;"));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_UNCLASSIFIED");
    }

    [Fact]
    public void LazyBoundedDataMayMutateOnlyExactlyProvenLocalArguments()
    {
        const string values = "static class LazyValues { private static readonly Lazy<int> Loaded = new(Build, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); public static int Value => Loaded.Value; private static int Build() { var values = new System.Collections.Generic.Dictionary<string, int>(); Add(values); return values.Count; } private static void Add(System.Collections.Generic.Dictionary<string, int> values) { values[\"answer\"] = 42; } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("_ = LazyValues.Value;", values));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_UNCLASSIFIED");
    }

    [Fact]
    public void LazyBoundedDataRejectsSharedCollectionHiddenBehindAnArgument()
    {
        const string values = "static class LazyValues { private static readonly System.Collections.Generic.Dictionary<string, int> Shared = new(); private static readonly Lazy<int> Loaded = new(Build, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); public static int Value => Loaded.Value; private static int Build() { Add(Shared); return Shared.Count; } private static void Add(System.Collections.Generic.Dictionary<string, int> values) { values[\"answer\"] = 42; } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("_ = LazyValues.Value;", values));

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Detail.StartsWith(
                    "System.Lazy`1..ctor;",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void LazyValueAfterExactCreatedGuardDoesNotExecuteItsFactory()
    {
        const string values = "static class LazyValues { private static readonly Lazy<bool> Loaded = new(() => { System.IO.File.Delete(\"path\"); return true; }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); public static void ReadIfCreated() { if (!Loaded.IsValueCreated) { return; } _ = Loaded.Value; } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("LazyValues.ReadIfCreated();", values));

        Assert.DoesNotContain(
            result.Items,
            static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_UNCLASSIFIED");
    }

    [Theory]
    [InlineData("static () => 42", "System.Threading.LazyThreadSafetyMode.ExecutionAndPublication", false)]
    [InlineData("static () => { System.IO.File.Delete(\"path\"); return 42; }", "System.Threading.LazyThreadSafetyMode.ExecutionAndPublication", true)]
    [InlineData("static () => 42", "System.Threading.LazyThreadSafetyMode.PublicationOnly", true)]
    public void ConcurrentDictionaryLazyPublicationRetainsExactFactoryProof(
        string factory,
        string mode,
        bool expectedUnproven)
    {
        string values = "static class LazyValues { private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<int>> Values = new(); public static int Value => Values.GetOrAdd(\"key\", static _ => new Lazy<int>(FACTORY, MODE)).Value; }"
            .Replace("FACTORY", factory, StringComparison.Ordinal)
            .Replace("MODE", mode, StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("_ = LazyValues.Value;", values));

        Assert.Equal(
            expectedUnproven,
            result.Diagnostics.Any(static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_UNCLASSIFIED"));
    }

    [Fact]
    public void ScalarArgumentFanOutDoesNotMultiplySemanticTraversalStates()
    {
        const int depth = 14;

        string helpers = string.Join(
            " ",
            Enumerable.Range(0, depth)
                .Select(index => "static void Layer"
                    + index
                    + "(int value) { Layer"
                    + (index + 1)
                    + "(value); Layer"
                    + (index + 1)
                    + "(value + 1); }"))
            + " static void Layer"
            + depth
            + "(int value) { _ = value; }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("Layer0(0);", helpers));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");
    }

    [Fact]
    public void CyclicLazyFactoriesRemainUnknown()
    {
        const string values = "static class LazyValues { private static readonly Lazy<string> Left = new(() => Right.Value, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); private static readonly Lazy<string> Right = new(() => Left.Value, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication); public static string Value => Left.Value; }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("_ = LazyValues.Value;", values));

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Lazy`1.Value");
    }

    [Fact]
    public void ProductionSchemaLazyFactoriesAreProvenAsBoundedData()
    {
        const string catalogType =
            "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog";

        const string manifestsType =
            "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests";

        string[] members = HostedGrimoireProducerInventory.AdditionalProductionRoots
            .Where(chain => chain.EnclosingType is catalogType or manifestsType)
            .Select(static chain => chain.Member)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "get_AllObjects",
                "get_CanonicalSchemaFingerprint",
                "get_Core",
                "get_CoreObjects",
                "get_CoreSchemaFingerprint",
                "get_CovenantAccelerator",
                "get_CovenantAcceleratorObjects",
                "get_CovenantAcceleratorSchemaFingerprint",
                "get_CovenantCanonical",
                "get_CovenantCanonicalObjects",
                "get_CovenantCanonicalSchemaFingerprint",
                "get_TransitionStatements",
            ],
            members);

        HostedProducerDiscovery<HostedProducerSite> result =
            HostedGrimoireProducerInventory.AdditionalProductionSiteDiscovery;

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_ROOT_UNRESOLVED");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.StartsWith(
                    "System.Lazy`1",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void IntrinsicTypeAllowanceDoesNotHideRefOrOutOperations()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("_ = System.Runtime.InteropServices.MemoryMarshal.TryRead<int>(new byte[4], out int value);"));

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Runtime.InteropServices.MemoryMarshal.TryRead");
    }

    [Fact]
    public void ExactInProcessSynchronizationMembersAreLifecycleSafe()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("int value = 0; _ = System.Threading.Interlocked.Increment(ref value); System.Threading.Volatile.Write(ref value, 2); _ = System.Threading.Volatile.Read(ref value);"));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");

        Assert.DoesNotContain(
            result.Items,
            static site => site.Callee.StartsWith(
                "System.Threading.",
                StringComparison.Ordinal));
    }

    [Fact]
    public void SynchronousIntrinsicDelegateIsRecursivelyClassified()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + "_ = string.Create(1, 0, (span, _) => { System.IO.File.Delete(\"path\"); span[0] = 'x'; });"));

        Assert.Single(
            result.Items,
            static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_UNCLASSIFIED");
    }

    [Fact]
    public void InterpolatedStringCreateIsABoundedIntrinsic()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("_ = string.Create(System.Globalization.CultureInfo.InvariantCulture, $\"value:{1}\");"));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.String.Create");
    }

    [Theory]
    [InlineData("string[] values = [\"one\"]; _ = values.Contains(\"one\", StringComparer.Ordinal);")]
    [InlineData("string[] values = [\"one\"]; _ = values.ToHashSet(StringComparer.Ordinal);")]
    [InlineData("string[] values = [\"one\"]; _ = values.SequenceEqual([\"one\"], StringComparer.Ordinal);")]
    [InlineData("var values = System.Collections.Immutable.ImmutableArray.Create(\"one\"); _ = values.SequenceEqual(System.Collections.Immutable.ImmutableArray.Create(\"one\"), StringComparer.Ordinal);")]
    [InlineData("var values = System.Collections.Immutable.ImmutableArray.Create(1); _ = values.SequenceEqual(System.Collections.Immutable.ImmutableArray.Create(1));")]
    [InlineData("IReadOnlyList<string>? values = [\"one\"]; IReadOnlyList<string>? expected = [\"one\"]; _ = values!.SequenceEqual(expected!, StringComparer.Ordinal);")]
    [InlineData("List<string> values = [\"one\"]; _ = values.Contains(\"one\", StringComparer.Ordinal);")]
    [InlineData("string[] values = [\"one\"]; _ = values.Skip(0).Contains(\"one\", StringComparer.Ordinal);")]
    [InlineData("IEnumerable<string> values = [\"one\"]; _ = values.Select(static value => value).Where(static value => value.Length > 0).Cast<string>().ToHashSet(StringComparer.Ordinal);")]
    public void ExactSequenceOperationsWithReviewedComparersAreBounded(
        string body)
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(body));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");
    }

    [Fact]
    public void ImmutableArrayCovariantSequenceEqualityUsesItsImplicitDefaultComparer()
    {
        const string values = "namespace Values { public record Base(int Value); public sealed record Derived(int Value) : Base(Value); }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(
                "var expected = System.Collections.Immutable.ImmutableArray.Create<Values.Base>(new Values.Base(1)); var actual = System.Collections.Immutable.ImmutableArray.Create(new Values.Derived(1)); _ = expected.SequenceEqual(actual);",
                values));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail
                    == "System.Linq.ImmutableArrayExtensions.SequenceEqual");
    }

    [Theory]
    [InlineData("await channel.Writer.WriteAsync(1, token);", false)]
    [InlineData("_ = channel.Writer.WriteAsync(1, token);", true)]
    public void ChannelWriterAsyncBackpressureRequiresExactCompletionOwnership(
        string write,
        bool expectedUnowned)
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(
                "var channel = System.Threading.Channels.Channel.CreateBounded<int>(1); "
                    + write));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail
                    == "System.Threading.Channels.ChannelWriter`1.WriteAsync");

        Assert.Equal(
            expectedUnowned,
            result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Detail.StartsWith(
                    "System.Threading.Channels.ChannelWriter`1.WriteAsync;",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public void ProductionSequenceOperationUsesTheSameReviewedComparerProof()
    {
        _ = Assert.Single(
            HostedGrimoireProducerInventory.AdditionalProductionRoots,
            static chain => chain.EnclosingType
                    == "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex"
                && chain.Member == "Add");

        HostedProducerDiscovery<HostedProducerSite> result =
            HostedGrimoireProducerInventory.AdditionalProductionSiteDiscovery;

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_ROOT_UNRESOLVED");

        Assert.Contains(
            result.Items,
            static site => site.EnclosingType
                    == "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex"
                && site.Kind == HostedProducerSiteKind.FileSystemEffect);

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Linq.Enumerable.Contains");
    }

    [Fact]
    public void ProductionImmutableArraySequenceEqualityUsesDefaultValueEquality()
    {
        const string type =
            "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore";

        string[] members = HostedGrimoireProducerInventory.AdditionalProductionRoots
            .Where(chain => chain.EnclosingType == type)
            .Select(static chain => chain.Member)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["SameBinding", "SamePayload"], members);

        HostedProducerDiscovery<HostedProducerSite> result =
            HostedGrimoireProducerInventory.AdditionalProductionSiteDiscovery;

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_ROOT_UNRESOLVED");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail
                    == "System.Linq.ImmutableArrayExtensions.SequenceEqual");
    }

    [Fact]
    public void ReviewedExternalDtoPropertiesRemainPlainData()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("ModelContextProtocol.Protocol.ElicitResult result = null!; _ = result.Action; _ = result.Content;"));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.StartsWith(
                    "ModelContextProtocol.Protocol.ElicitResult.",
                    StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("_ = Task.Delay(1);", true)]
    [InlineData("Task.Delay(1).GetAwaiter().GetResult();", false)]
    public void TimerBackedDelayRequiresCompletionOwnership(
        string body,
        bool expectedUnowned)
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(body));

        Assert.Equal(
            expectedUnowned,
            result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Detail.StartsWith(
                    "System.Threading.Tasks.Task.Delay;",
                    StringComparison.Ordinal)));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Threading.Tasks.Task.Delay");
    }

    [Theory]
    [InlineData("var values = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);", false)]
    [InlineData("StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal; var values = new System.Collections.Generic.HashSet<string>(comparer);", false)]
    [InlineData("System.Collections.Generic.IEqualityComparer<string> comparer = null!; var values = new System.Collections.Generic.Dictionary<string, int>(comparer);", true)]
    public void CollectionComparerConstructionRequiresAnExactPureComparer(
        string body,
        bool expectedUnclassified)
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(body));

        Assert.Equal(
            expectedUnclassified,
            result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Collections.Generic.Dictionary`2..ctor"));
    }

    [Fact]
    public void LazyFactoryRemainsRecursivelyClassified()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource("var value = new Lazy<bool>(() => { System.IO.File.Delete(\"path\"); return true; }); _ = value.Value;"));

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Detail.StartsWith(
                    "System.Lazy`1..ctor;",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void DatabaseCallsRequireOnlyTheWorkFrontier()
    {
        string body = AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal)
            + " using var connection = new Microsoft.Data.Sqlite.SqliteConnection(); using var command = connection.CreateCommand(); command.CommandText = \"SELECT 1;\"; _ = await command.ExecuteScalarAsync(token);";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body));

        Assert.Contains(
            result.Items,
            static site => site.Kind == HostedProducerSiteKind.DatabaseAccess
                && site.Callee == "System.Data.Common.DbCommand.ExecuteScalarAsync");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED"
                or "HOSTED_SITE_WORK_FRONTIER_MISSING"
                or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Fact]
    public void SqliteCommandConnectionPropertyIsReviewedDatabasePlumbing()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(
                "using var connection = new Microsoft.Data.Sqlite.SqliteConnection(); using var command = connection.CreateCommand(); command.Connection = connection; _ = command.Connection;"));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.StartsWith(
                    "Microsoft.Data.Sqlite.SqliteCommand.Connection",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void TaskCompletionSourceCancellationIsReviewedInProcessSignaling()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(
                "var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); _ = completion.TrySetCanceled(token);"));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail
                    == "System.Threading.Tasks.TaskCompletionSource`1.TrySetCanceled");
    }

    [Theory]
    [InlineData("_ = Task.CompletedTask.ContinueWith(_ => System.IO.File.Delete(\"path\"));")]
    [InlineData("_ = CancellationToken.None.Register(() => System.IO.File.Delete(\"path\"));")]
    [InlineData("using var timer = new System.Threading.Timer(_ => System.IO.File.Delete(\"path\"), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));")]
    public void UnreviewedExternalCallbackCarriersRemainUnowned(string body)
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource(body));

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void CancellationRegistrationWithBoundedCallbackNeedsNoProducerAdmission()
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(
                "TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously); using (token.Register(() => { _ = completion.TrySetResult(); })) { await completion.Task; }"));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_UNCLASSIFIED");
    }

    [Theory]
    [InlineData("System.Threading.Tasks.Parallel.ForEach(new[] { 1 }, _ => System.IO.File.Delete(\"path\"));")]
    [InlineData("new System.Collections.Generic.List<int> { 1 }.ForEach(_ => System.IO.File.Delete(\"path\"));")]
    public void ReviewedEagerExternalCallbackCarriersExecuteWithinTheirCaller(string body)
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(R2Admission + body));

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void EagerlyConsumedExternalMethodGroupIsClassifiedAtItsCallbackSite()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(
                R2Admission
                    + "_ = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(new[] { \"path\" }, System.IO.File.Exists));"));

        Assert.Single(
            result.Items,
            static site => site.Callee == "System.IO.File.Exists"
                && site.Kind == HostedProducerSiteKind.FileSystemRead);

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    or "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData("+=", "add")]
    [InlineData("-=", "remove")]
    public void ExternalEventMutationFailsClosed(string mutation, string operation)
    {
        string body = "using var watcher = new System.IO.FileSystemWatcher(\".\"); watcher.Changed "
            + mutation
            + " (_, _) => System.IO.File.Delete(\"path\");";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource(body));

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.IO.FileSystemWatcher.Changed " + operation);

        if (operation == "add")
        {
            Assert.Contains(
                result.Diagnostics,
                static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
        }
    }

    [Fact]
    public void WorkspaceWatcherActivationUsesWorkAdmissionAndDurableLifecycleOwnership()
    {
        string source = $$"""
            using System;
            using System.Collections.Generic;
            using System.IO;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Hosting;

            public sealed class Worker : IHostedService
            {
                private readonly Queue<string> pending = new();
                private RetroDownfall.Arcanum.Infrastructure.Hosting.IWorkspaceFileWatcher? watcher;

                public Task StartAsync(CancellationToken token)
                {
                    {{AcquireWork}}
                    RetroDownfall.Arcanum.Infrastructure.Hosting.IWorkspaceFileWatcherFactory factory = new RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceFileWatcherFactory();
                    watcher = factory.Create(".", change => Queue(change), _ => Queue("error"));
                    return Task.CompletedTask;
                }

                public Task StopAsync(CancellationToken token)
                {
                    watcher?.Dispose();
                    watcher = null;
                    return Task.CompletedTask;
                }

                private void Queue(string value)
                {
                    lock (pending)
                    {
                        pending.Enqueue(value);
                    }
                }
            }

            namespace RetroDownfall.Arcanum.Infrastructure.Hosting
            {
                internal interface IWorkspaceFileWatcher : IDisposable { }

                internal interface IWorkspaceFileWatcherFactory
                {
                    IWorkspaceFileWatcher Create(string path, Action<string> onChange, Action<Exception> onError);
                }

                internal sealed class WorkspaceFileWatcherFactory : IWorkspaceFileWatcherFactory
                {
                    public IWorkspaceFileWatcher Create(string path, Action<string> onChange, Action<Exception> onError) =>
                        new FileSystemWorkspaceFileWatcher(path, onChange, onError);
                }

                internal sealed class FileSystemWorkspaceFileWatcher : IWorkspaceFileWatcher
                {
                    private readonly FileSystemWatcher watcher;

                    internal FileSystemWorkspaceFileWatcher(string path, Action<string> onChange, Action<Exception> onError)
                    {
                        watcher = new FileSystemWatcher(path);
                        watcher.Changed += (_, args) => onChange(args.FullPath);
                        watcher.Error += (_, args) => onError(args.GetException());
                        watcher.EnableRaisingEvents = true;
                    }

                    public void Dispose() => watcher.Dispose();
                }
            }
            """ + AdmissionTypes;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Contains(
            result.Items,
            static site => site.Callee == "System.IO.FileSystemWatcher.EnableRaisingEvents setter"
                && site.Kind == HostedProducerSiteKind.FileSystemRead
                && site.WorkFrontierCapsuleId is not null);

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED"
                or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                or "HOSTED_SITE_WORK_FRONTIER_MISSING"
                or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Fact]
    public void InterfaceCallFollowsExactSingletonBinding()
    {
        string source = FixtureSource("IHelper helper = null!; helper.Run();", "interface IHelper { void Run(); } class Helper : IHelper { public void Run() { System.IO.File.Exists(\"path\"); } }")
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); services.AddSingleton<IHelper, Helper>();", StringComparison.Ordinal);

        Assert.Contains(Discover(source).Items, static site => site.EnclosingType == "Helper" && site.Callee == "System.IO.File.Exists");
    }

    [Fact]
    public void InterfaceCallConservativelyFollowsEveryRegisteredImplementation()
    {
        const string helpers = "public interface IHelper { void Run(); } internal sealed class First : IHelper { public void Run() { System.IO.File.Exists(\"first\"); } } internal sealed class Second : IHelper { public void Run() { System.IO.File.Delete(\"second\"); } } internal sealed class Unregistered : IHelper { public void Run() { System.IO.Directory.Exists(\"unregistered\"); } }";

        string source = FixtureSource("IHelper helper = null!; helper.Run();", helpers)
            .Replace(
                "services.AddHostedService<Worker>();",
                "services.AddHostedService<Worker>(); services.AddSingleton<IHelper, First>(); services.AddSingleton<IHelper, Second>();",
                StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Contains(result.Items, static site => site.EnclosingType == "First" && site.Callee == "System.IO.File.Exists");

        Assert.Contains(result.Items, static site => site.EnclosingType == "Second" && site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(result.Items, static site => site.EnclosingType == "Unregistered");

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
                && diagnostic.Detail == "IHelper.Run");
    }

    [Fact]
    public void ClosedInternalInterfaceFollowsItsOnlyAuthoredImplementation()
    {
        const string helpers = "internal interface IHelper { void Run(); } internal sealed class Helper : IHelper { public void Run() { System.IO.File.Exists(\"path\"); } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource("IHelper helper = null!; helper.Run();", helpers));

        Assert.True(
            result.Items.Any(static site => site.EnclosingType == "Helper" && site.Callee == "System.IO.File.Exists"),
            string.Join(global::System.Environment.NewLine, result.Diagnostics));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Detail == "IHelper.Run");
    }

    [Theory]
    [InlineData("internal", "internal sealed class Other : IHelper { public void Run() { } }")]
    [InlineData("public", "")]
    public void OpenOrMultiplyImplementedInterfaceRemainsUnresolved(string accessibility, string additionalImplementation)
    {
        string helpers = accessibility + " interface IHelper { void Run(); } " + accessibility + " sealed class Helper : IHelper { public void Run() { System.IO.File.Exists(\"path\"); } } " + additionalImplementation;

        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource("IHelper helper = null!; helper.Run();", helpers));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Detail == "IHelper.Run");
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("unused-implementation", true)]
    [InlineData("opaque-element", false)]
    [InlineData("extra-create-call", false)]
    [InlineData("unsealed", false)]
    [InlineData("public-contract", false)]
    public void OfflineTransitionHandlersRequireOneClosedProductionRegistry(
        string shape,
        bool resolved)
    {
        string accessibility = shape == "public-contract" ? "public" : "internal";

        string firstAccessibility = shape == "unsealed"
            ? "internal"
            : "internal sealed";

        string secondElement = shape == "opaque-element"
            ? "CreateUnknown()"
            : "new Second()";

        string unused = shape == "unused-implementation"
            ? "internal sealed class Unused : IGrimoireOfflineTransitionHandler { public void Decode() { System.IO.Directory.Exists(\"unused\"); } }"
            : "";

        string extraCreate = shape == "extra-create-call"
            ? "internal static void BuildOther() => _ = Create([new First()]);"
            : "";

        string helpers = "namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions { "
            + accessibility
            + " interface IGrimoireOfflineTransitionHandler { void Decode(); } "
            + firstAccessibility
            + " class First : IGrimoireOfflineTransitionHandler { public void Decode() { System.IO.File.Exists(\"first\"); } } "
            + "internal sealed class Second : IGrimoireOfflineTransitionHandler { public void Decode() { System.IO.File.Delete(\"second\"); } } "
            + unused
            + " internal sealed class Registry { "
            + "private readonly System.Collections.Generic.IReadOnlyList<IGrimoireOfflineTransitionHandler> handlers; "
            + "private Registry(System.Collections.Generic.IReadOnlyList<IGrimoireOfflineTransitionHandler> handlers) { this.handlers = handlers; } "
            + "internal static Registry Production { get; } = Value(Create([new First(), "
            + secondElement
            + "])); "
            + "private static Registry Value(Registry registry) => registry; "
            + "private static Registry Create(System.Collections.Generic.IEnumerable<IGrimoireOfflineTransitionHandler> values) => new(System.Linq.Enumerable.ToArray(values)); "
            + "private static IGrimoireOfflineTransitionHandler CreateUnknown() => null!; "
            + "internal IGrimoireOfflineTransitionHandler Resolve() => handlers[0]; "
            + extraCreate
            + " } }";

        const string body = "var registry = RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.Registry.Production; var handler = registry.Resolve(); handler.Decode();";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(
            FixtureSource(body, helpers));

        Assert.Equal(
            resolved,
            !result.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
                && diagnostic.Detail == "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.IGrimoireOfflineTransitionHandler.Decode"));

        Assert.Equal(
            resolved,
            result.Items.Any(static site => site.EnclosingType.EndsWith(".First", StringComparison.Ordinal)
                && site.Callee == "System.IO.File.Exists"));

        Assert.Equal(
            resolved,
            result.Items.Any(static site => site.EnclosingType.EndsWith(".Second", StringComparison.Ordinal)
                && site.Callee == "System.IO.File.Delete"));

        Assert.DoesNotContain(
            result.Items,
            static site => site.EnclosingType.EndsWith(".Unused", StringComparison.Ordinal));
    }

    [Fact]
    public void EndpointInvokedSensitiveOperationNeedsItsOwnRoot()
    {
        string source = FixtureSource("").Replace("public class Worker : IHostedService", "public class Worker : IHostedService", StringComparison.Ordinal)
            .Replace("public Task StartAsync", "public void QueueIndexNow() { System.IO.File.Exists(\"path\"); } public Task StartAsync", StringComparison.Ordinal)
            + "class Endpoint { void Invoke(Worker worker) { worker.QueueIndexNow(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Contains(result.Diagnostics, static item => item.Code == "HOSTED_EXTERNAL_OPERATION_UNCATALOGUED");

        Assert.Contains(result.Items, static site => site.Member == "QueueIndexNow");
    }

    [Fact]
    public void DeclaredMissingRootFailsClosed()
    {
        CSharpCompilation compilation = Compile(FixtureSource(""));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [], [new("Missing.Run", "src/Fixture.cs", "Missing", "Run", HostedProducerAuthorityKind.StoppedHost, "Missing.Run: exact stopped host owner", [])]);

        Assert.Contains(result.Diagnostics, static item => item.Code == "HOSTED_ROOT_UNRESOLVED");
    }

    private static HostedProducerDiscovery<HostedProducerSite> Discover(string source)
    {
        CSharpCompilation compilation = Compile(source);

        return HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]), [], []);
    }

    private static string FixtureSource(string body, string extra = "") => RegistrationSource("services.AddHostedService<Worker>();")
        .Replace("public Task StartAsync(CancellationToken token) => Task.CompletedTask;", "public Task StartAsync(CancellationToken token) { " + body + " return Task.CompletedTask; }", StringComparison.Ordinal) + extra;

    [Theory]
    [InlineData("new-service", "HOSTED_SERVICE_UNCATALOGUED")]
    [InlineData("stale-service", "HOSTED_SERVICE_STALE")]
    [InlineData("duplicate-service", "HOSTED_SERVICE_DUPLICATE")]
    [InlineData("new-site", "HOSTED_SITE_UNCATALOGUED")]
    [InlineData("stale-site", "HOSTED_SITE_STALE")]
    [InlineData("duplicate-site", "HOSTED_SITE_DUPLICATE")]
    [InlineData("broad", "HOSTED_IDENTITY_INVALID")]
    [InlineData("missing-proof", "HOSTED_PROOF_MISSING")]
    [InlineData("shared-proof", "HOSTED_PROOF_SHARED")]
    [InlineData("wrong-kind", "HOSTED_WORK_KIND_INVALID")]
    [InlineData("missing-kind", "HOSTED_WORK_KIND_MISSING")]
    [InlineData("external-effect", "HOSTED_SITE_EFFECT_FRONTIER_MISSING")]
    public void ValidatorRejectsIndependentMutation(string mutation, string code)
    {
        HostedProducerSite site = new("Worker", "Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.FileSystemRead, "System.IO.File.Exists");

        HostedProducerOperationEntry operation = new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, "Worker.StartAsync: Generic Host awaits completion", [site]);

        List<HostedProducerServiceEntry> catalog = [new("Worker", [operation])];

        List<string> registrations = ["Worker"];

        List<HostedProducerSite> sites = [site];

        switch (mutation)
        {
            case "new-service": registrations.Add("NewWorker"); break;
            case "stale-service": registrations.Clear(); break;
            case "duplicate-service": catalog.Add(catalog[0]); break;
            case "new-site": sites.Add(site with { Callee = "System.IO.File.ReadAllText" }); break;
            case "stale-site": sites.Clear(); break;
            case "duplicate-site": operation = operation with { Sites = [site, site] }; break;
            case "broad": operation = operation with { SourcePath = "src/**" }; break;
            case "missing-proof": operation = operation with { Proof = null }; break;
            case "shared-proof": catalog.Add(new("AnotherWorker", [operation with { OperationId = "AnotherWorker.StartAsync" }])); registrations.Add("AnotherWorker"); break;
            case "wrong-kind": operation = operation with { WorkKind = GrimoireWorkKind.WorkspaceIndexing }; break;
            case "missing-kind": operation = operation with { Authority = HostedProducerAuthorityKind.OrdinaryHostedWork, Proof = null }; break;
            case "external-effect":
                site = site with { Kind = HostedProducerSiteKind.FileSystemEffect, Callee = "System.IO.File.Delete" };

                operation = operation with { Authority = HostedProducerAuthorityKind.OrdinaryHostedWork, WorkKind = GrimoireWorkKind.WorkspaceIndexing, Proof = null, Sites = [site] };

                sites = [site];

                break;
        }

        catalog[0] = catalog[0] with { Operations = [operation] };

        HostedProducerInventoryValidation result = HostedGrimoireProducerInventory.Validate(catalog, [], new(registrations, []), new(sites, []));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetAwareHelperIsUnwrappedOnceAndItsBodyIsValidated(bool mutate)
    {
        string source = RegistrationSource("RetroDownfall.Arcanum.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInstallationResetRecoveryAwareHostedService<Worker>(services);") + $$"""
            namespace RetroDownfall.Arcanum.Infrastructure.DependencyInjection
            {
                public static class ServiceCollectionExtensions
                {
                    public static void AddInstallationResetRecoveryAwareHostedService<TService>(this IServiceCollection services) where TService : class, IHostedService
                    {
                        services.AddHostedService({{(mutate ? "sp => new Worker()" : "sp => new RetroDownfall.Arcanum.Infrastructure.Hosting.InstallationResetRecoveryAwareHostedService<TService>()")}});
                    }
                }
            }
            namespace RetroDownfall.Arcanum.Infrastructure.Hosting
            {
                public class InstallationResetRecoveryAwareHostedService<T> : IHostedService
                {
                    public Task StartAsync(CancellationToken token) => Task.CompletedTask;

                    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
                }
            }
            """;

        HostedProducerDiscovery<string> result = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([Compile(source)]);

        Assert.Equal(["Worker"], result.Items);

        if (mutate)
        {
            Assert.Contains(result.Diagnostics, static d => d.Code == "HOSTED_REGISTRATION_HELPER_SHAPE_CHANGED");
        }
        else
        {
            Assert.Empty(result.Diagnostics);
        }
    }

    [Theory]
    [InlineData("services.AddHostedService<Missing>();")]
    [InlineData("services.AddHostedService<IHostedService>();")]
    [InlineData("services.AddHostedService<AbstractWorker>();")]
    [InlineData("services.AddHostedService<T>();")]
    [InlineData("Lookalike.AddHostedService<Worker>(services);")]
    public void UnsupportedRegistrationFailsClosed(string registration)
    {
        string source = RegistrationSource(registration) + """
            public abstract class AbstractWorker : Microsoft.Extensions.Hosting.IHostedService
            {
                public abstract System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken t);
                public abstract System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken t);
            }

            public static class Lookalike { public static void AddHostedService<T>(object services) {} }
            """;

        HostedProducerDiscovery<string> result = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([Compile(source)]);

        Assert.Contains(result.Diagnostics, static d => d.Code == "HOSTED_REGISTRATION_UNSUPPORTED_SHAPE");
    }

    [Theory]
    [InlineData("services.AddHostedService<Worker>();")]
    [InlineData("services.AddHostedService<Worker>(sp => sp.GetRequiredService<Worker>());")]
    [InlineData("services.AddHostedService(sp => sp.GetRequiredService<Worker>());")]
    [InlineData("services.AddHostedService(sp => new Worker());")]
    [InlineData("services.AddHostedService(Factory);")]
    [InlineData("Func<IServiceProvider, Worker> factory = Factory; services.AddHostedService(factory);")]
    [InlineData("ServiceCollectionHostedServiceExtensions.AddHostedService<Worker>(services);")]
    public void RegistrationUsesBoundImplementationType(string registration)
    {
        HostedProducerDiscovery<string> result = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([Compile(RegistrationSource(registration))]);

        Assert.Empty(result.Diagnostics);

        Assert.Equal(["Worker"], result.Items);
    }

    private static string RegistrationSource(string registration) => $$"""
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        public class Worker : IHostedService
        {
            public Task StartAsync(CancellationToken token) => Task.CompletedTask;

            public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        }

        public static class Composition
        {
            static Worker Factory(IServiceProvider provider) => new Worker();
            public static void Configure(IServiceCollection services) { {{registration}} }
        }
        """;

    private static string ApprenticeSelfHandoffSource(string mutation)
    {
        string barrier = mutation == "missing-publication-barrier"
            ? string.Empty
            : "lock (_executionLifecycleLock) { }";

        string cleanupTask = mutation == "wrong-cleanup-task"
            ? "Task.CompletedTask"
            : "task!";

        string publishedTask = mutation == "wrong-published-task"
            ? "Task.CompletedTask"
            : "task";

        string catchClause = mutation == "missing-fault-observation"
            ? string.Empty
            : "catch (Exception) { }";

        return $$"""
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Hosting;

            namespace RetroDownfall.Arcanum.Infrastructure.Hosting
            {
                public sealed class ApprenticeService : IHostedService
                {
                    private readonly object _executionLifecycleLock = new();
                    private readonly Dictionary<Guid, Task> _activeTasks = new();

                    public Task StartAsync(CancellationToken token)
                    {
                        BeginExecutionTask(Guid.Empty);

                        return Task.CompletedTask;
                    }

                    public Task StopAsync(CancellationToken token) => Task.CompletedTask;

                    private void BeginExecutionTask(Guid apprenticeId)
                    {
                        Task? task = null;

                        long generation = 1;

                        lock (_executionLifecycleLock)
                        {
                            task = Task.Run(async () =>
                            {
                                {{barrier}}

                                try
                                {
                                    await RunApprenticeAsync(apprenticeId, generation).ConfigureAwait(false);
                                }
                                {{catchClause}}
                                finally
                                {
                                    CleanupExecution(apprenticeId, generation, {{cleanupTask}});
                                }
                            });

                            _activeTasks[apprenticeId] = {{publishedTask}};
                        }
                    }

                    private static void CleanupExecution(
                        Guid apprenticeId,
                        long generation,
                        Task completedTask)
                    {
                    }

                    private static async Task RunApprenticeAsync(
                        Guid apprenticeId,
                        long generation)
                    {
                        RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!;

                        if (!gate.TryAcquireWorkLease(
                                RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.ApprenticeExecution,
                                out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease))
                        {
                            return;
                        }

                        using (lease)
                        {
                            _ = System.IO.File.Exists("path");
                        }

                        await Task.CompletedTask;
                    }
                }
            }

            namespace RetroDownfall.Arcanum.Infrastructure.Data
            {
                public enum GrimoireWorkKind { ApprenticeExecution = 9 }

                public interface IGrimoireWorkLease : IDisposable
                {
                    bool TryBeginExternalEffectGroup(out IDisposable group);
                }

                public interface IGrimoireConnectionAdmissionGate
                {
                    bool TryAcquireWorkLease(
                        GrimoireWorkKind kind,
                        out IGrimoireWorkLease lease);
                }
            }
            """;
    }

    private static CSharpCompilation Compile(string source)
    {
        string[] assemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);

        return CSharpCompilation.Create("InventoryFixture", [CSharpSyntaxTree.ParseText(source, path: "src/Fixture.cs")], assemblies.Select(static path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
