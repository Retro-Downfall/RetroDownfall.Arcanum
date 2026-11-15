using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("implicit", true)]
    [InlineData("explicit", true)]
    [InlineData("opaque", false)]
    [InlineData("other", false)]
    [InlineData("virtual", false)]
    [InlineData("selected", true)]
    public void HostedFactoryCleanupRequiresTheActualHandoffReceiver(string receiver, bool sameInstance)
    {
        string dispatch = receiver switch
        {
            "implicit" => "ContinueAsync(token)",
            "explicit" => "this.ContinueAsync(token)",
            "opaque" => "UnknownWorker.Value.ContinueAsync(token)",
            "virtual" => "ContinueAsync(token)",
            "selected" => "ContinueAsync(token)",
            _ => "new Worker(new OtherCleanup()).ContinueAsync(token)",
        };

        string source = R2Source((receiver == "selected" ? "StartupProbe(); " : "") + "background = Task.Run(() => " + dispatch + ");")
            .Replace("public class Worker : IHostedService\n{", "public class Worker : IHostedService\n{ private readonly System.IAsyncDisposable cleanup; public Worker(System.IAsyncDisposable cleanup) { this.cleanup = cleanup; }", StringComparison.Ordinal)
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "private Task? background; private async Task ContinueAsync(CancellationToken token) { " + R2Admission + " await using (cleanup) { } } public async Task StopAsync(CancellationToken token) { if (background is not null) await background; }", StringComparison.Ordinal)
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(sp => new Worker(new KnownCleanup()));", StringComparison.Ordinal)
            .Replace("static Worker Factory(IServiceProvider provider) => new Worker();", "static Worker Factory(IServiceProvider provider) => new Worker(new KnownCleanup());", StringComparison.Ordinal)
            + "public sealed class KnownCleanup : System.IAsyncDisposable { public System.Threading.Tasks.ValueTask DisposeAsync() { System.IO.File.Delete(\"factory-cleanup\"); return default; } }"
            + " internal static class UnknownWorker { internal static Worker Value { get; set; } = null!; }"
            + " internal sealed class OtherCleanup : System.IAsyncDisposable { public System.Threading.Tasks.ValueTask DisposeAsync() => default; }";

        if (receiver == "virtual")
        {
            source = source.Replace("private async Task ContinueAsync", "protected virtual async Task ContinueAsync", StringComparison.Ordinal);
        }

        if (receiver == "selected")
        {
            source = source.Replace("private Task? background;", "private void StartupProbe() { } private Task? background;", StringComparison.Ordinal);
        }

        string startupOperation = receiver == "selected"
            ? "Worker.StartAsync::call:Worker.StartupProbe#0~" + HostedGrimoireProducerInventory.Fingerprint(SyntaxFactory.ParseExpression("StartupProbe()"))
            : "Worker.StartAsync";

        HostedProducerOperationEntry startup = OrdinaryRoot(startupOperation) with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: awaited startup" };

        HostedProducerOperationEntry runtime = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync" };

        List<HostedProducerAdmittedStateObservation> observed = [];

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [startup, runtime])], [], admittedStateObserver: observed.Add);

        Assert.Contains(observed, state => !state.Lifecycle && state.Method == "M:KnownCleanup.DisposeAsync");

        Assert.Contains(result.Items, site => site.Callee == "System.IO.File.Delete" && site.AuthorityOperationId == runtime.OperationId);

        Assert.Equal(sameInstance, observed.Any(state => state.Lifecycle && state.Method == "M:KnownCleanup.DisposeAsync"));

        Assert.Equal(!sameInstance, result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("joined-callback")]
    [InlineData("opaque-callback")]
    public void HostHandoffDoesNotInheritStartupWorkOrEffect(string shape)
    {
        string construction = shape == "opaque-callback" ? "new KnownCleanup(() => CallbackTarget.Delete())" : "new KnownCleanup()";

        string helper = shape switch
        {
            "direct" => "public sealed class KnownCleanup : System.IAsyncDisposable { public System.Threading.Tasks.ValueTask DisposeAsync() { System.IO.File.Delete(\"factory-cleanup\"); return default; } }",
            "joined-callback" => "public sealed class KnownCleanup : System.IAsyncDisposable { public async System.Threading.Tasks.ValueTask DisposeAsync() { var runner = new Runner(() => { CallbackTarget.Delete(); return System.Threading.Tasks.Task.CompletedTask; }); await runner.RunAsync(); } } public static class CallbackTarget { public static void Delete() => System.IO.File.Delete(\"factory-cleanup\"); } internal sealed class Runner { private readonly System.Func<System.Threading.Tasks.Task> callback; public Runner(System.Func<System.Threading.Tasks.Task> callback) { this.callback = callback; } public System.Threading.Tasks.Task RunAsync() => callback(); }",
            _ => "public sealed class KnownCleanup : System.IAsyncDisposable { private readonly System.Action action; public KnownCleanup(System.Action action) { this.action = action; } public System.Threading.Tasks.ValueTask DisposeAsync() { action(); return default; } } public static class CallbackTarget { public static void Delete() => System.IO.File.Delete(\"factory-cleanup\"); }",
        };

        string source = R2Source(R2Admission + "background = Task.Run(() => ContinueAsync(token));")
            .Replace("public class Worker : IHostedService\n{", "public class Worker : IHostedService\n{ private readonly System.IAsyncDisposable cleanup; public Worker(System.IAsyncDisposable cleanup) { this.cleanup = cleanup; }", StringComparison.Ordinal)
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "private Task? background; private async Task ContinueAsync(CancellationToken token) { await using (cleanup) { } } public async Task StopAsync(CancellationToken token) { if (background is not null) await background; }", StringComparison.Ordinal)
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(sp => new Worker(" + construction + "));", StringComparison.Ordinal)
            .Replace("static Worker Factory(IServiceProvider provider) => new Worker();", "static Worker Factory(IServiceProvider provider) => new Worker(" + construction + ");", StringComparison.Ordinal)
            + helper;

        HostedProducerOperationEntry startup = OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: awaited startup" };

        HostedProducerOperationEntry runtime = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync" };

        List<HostedProducerAdmittedStateObservation> observed = [];

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [startup, runtime])], [], admittedStateObserver: observed.Add);

        HostedProducerAdmittedStateObservation handoff = Assert.Single(observed, state => state.Lifecycle && state.RootOperation == runtime.OperationId && state.Method.StartsWith("M:Worker.ContinueAsync(", StringComparison.Ordinal));

        Assert.Null(handoff.WorkFrontier);

        Assert.Null(handoff.RegisteredRootWorkFrontier);

        Assert.Null(handoff.EffectFrontier);

        HostedProducerAdmittedStateObservation cleanup = Assert.Single(observed, state => state.Lifecycle && state.RootOperation == runtime.OperationId && state.Method == "M:KnownCleanup.DisposeAsync");

        Assert.Null(cleanup.WorkFrontier);

        Assert.Null(cleanup.RegisteredRootWorkFrontier);

        Assert.Null(cleanup.EffectFrontier);

        if (shape == "opaque-callback")
        {
            Assert.DoesNotContain(observed, state => state.Lifecycle && state.Method == "M:CallbackTarget.Delete");

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Identity.StartsWith(runtime.OperationId + "/dispose@", StringComparison.Ordinal)
                && diagnostic.Identity.EndsWith("/callback@src/Fixture.cs:" + source.IndexOf("action()", StringComparison.Ordinal), StringComparison.Ordinal)
                && diagnostic.Detail.StartsWith("System.Action.Invoke;", StringComparison.Ordinal));

            return;
        }

        if (shape == "joined-callback")
        {
            HostedProducerAdmittedStateObservation callback = Assert.Single(observed, state => state.Lifecycle && state.RootOperation == runtime.OperationId && state.Method == "M:CallbackTarget.Delete");

            Assert.Null(callback.WorkFrontier);

            Assert.Null(callback.RegisteredRootWorkFrontier);

            Assert.Null(callback.EffectFrontier);
        }

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Fact]
    public void NonplannedStartDoesNotBorrowHostedFactoryReceiver()
    {
        string source = R2Source("background = Task.Run(() => ContinueAsync(token));")
            .Replace("public class Worker : IHostedService\n{", "public class Worker : IHostedService\n{ private readonly System.IAsyncDisposable cleanup; public Worker(System.IAsyncDisposable cleanup) { this.cleanup = cleanup; }", StringComparison.Ordinal)
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "private Task? background; private async Task ContinueAsync(CancellationToken token) { " + R2Admission + " await using (cleanup) { } } public async Task StopAsync(CancellationToken token) { if (background is not null) await background; }", StringComparison.Ordinal)
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(sp => new Worker(new KnownCleanup()));", StringComparison.Ordinal)
            .Replace("static Worker Factory(IServiceProvider provider) => new Worker();", "static Worker Factory(IServiceProvider provider) => new Worker(new KnownCleanup());", StringComparison.Ordinal)
            + " public sealed class KnownCleanup : System.IAsyncDisposable { public System.Threading.Tasks.ValueTask DisposeAsync() { System.IO.File.Delete(\"factory-cleanup\"); return default; } }"
            + " public sealed class QuestCaller { public System.Threading.Tasks.Task BeginAsync(Worker worker, System.Threading.CancellationToken token) => worker.StartAsync(token); }";

        HostedProducerOperationEntry startup = OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: awaited startup" };

        HostedProducerOperationEntry runtime = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync" };

        HostedProducerOperationEntry caller = new("QuestCaller.BeginAsync", "src/Fixture.cs", "QuestCaller", "BeginAsync", HostedProducerAuthorityKind.FiniteRequest, null, "QuestCaller.BeginAsync: external invocation", []);

        List<HostedProducerAdmittedStateObservation> observed = [];

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("QuestCaller", [caller]), new("Worker", [startup, runtime])], [], admittedStateObserver: observed.Add);

        HostedProducerAdmittedStateObservation external = Assert.Single(observed, state => state.RootOperation == caller.OperationId && state.Method.StartsWith("M:QuestCaller.BeginAsync(", StringComparison.Ordinal));

        Assert.Contains(observed, state => state.Occurrence == external.Occurrence && state.RootOperation == runtime.OperationId && state.Method.StartsWith("M:Worker.ContinueAsync(", StringComparison.Ordinal));

        Assert.DoesNotContain(observed, state => state.Occurrence == external.Occurrence && state.Method == "M:KnownCleanup.DisposeAsync");
    }

    [Fact]
    public void ResetAwareHostedConstructionReachesTheSameInstanceHandoff()
    {
        string source = "using Microsoft.Extensions.DependencyInjection.Extensions; using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;\n" + R2Source("StartupProbe(); background = Task.Run(() => ContinueAsync(token));")
            .Replace("public class Worker : IHostedService\n{", "public class Worker : IHostedService\n{ private readonly System.IAsyncDisposable cleanup; public Worker() { cleanup = new KnownCleanup(); }", StringComparison.Ordinal)
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "private void StartupProbe() { } private Task? background; private async Task ContinueAsync(CancellationToken token) { " + R2Admission + " await using (cleanup) { } } public async Task StopAsync(CancellationToken token) { if (background is not null) await background; }", StringComparison.Ordinal)
            .Replace("services.AddHostedService<Worker>();", "services.AddInstallationResetRecoveryAwareHostedService<Worker>();", StringComparison.Ordinal)
            + "public sealed class KnownCleanup : System.IAsyncDisposable { public System.Threading.Tasks.ValueTask DisposeAsync() { System.IO.File.Delete(\"reset-aware-cleanup\"); return default; } }"
            + " namespace RetroDownfall.Arcanum.Infrastructure.DependencyInjection { public static class ServiceCollectionExtensions { public static IServiceCollection AddInstallationResetRecoveryAwareHostedService<TService>(this IServiceCollection services) where TService : class, IHostedService { services.TryAddSingleton<TService>(); services.AddHostedService(static sp => new RetroDownfall.Arcanum.Infrastructure.Hosting.InstallationResetRecoveryAwareHostedService<TService>(sp.GetRequiredService<TService>(), sp.GetService<InstallationResetApiAdmission>())); return services; } } public sealed class InstallationResetApiAdmission { } }"
            + " namespace RetroDownfall.Arcanum.Infrastructure.Hosting { public sealed class InstallationResetRecoveryAwareHostedService<T>(T inner, RetroDownfall.Arcanum.Infrastructure.DependencyInjection.InstallationResetApiAdmission? admission) : IHostedService where T : class, IHostedService { public Task StartAsync(CancellationToken token) => inner.StartAsync(token); public Task StopAsync(CancellationToken token) => inner.StopAsync(token); } }";

        string startupOperation = "Worker.StartAsync::call:Worker.StartupProbe#0~" + HostedGrimoireProducerInventory.Fingerprint(SyntaxFactory.ParseExpression("StartupProbe()"));

        HostedProducerOperationEntry startup = OrdinaryRoot(startupOperation) with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: awaited startup" };

        HostedProducerOperationEntry runtime = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync" };

        List<HostedProducerAdmittedStateObservation> observed = [];

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Infrastructure");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [startup, runtime])], [], admittedStateObserver: observed.Add);

        Assert.Contains(observed, state => !state.Lifecycle && state.Method == "M:KnownCleanup.DisposeAsync");

        Assert.Contains(result.Items, site => site.Callee == "System.IO.File.Delete" && site.AuthorityOperationId == runtime.OperationId);

        Assert.Contains(observed, state => state.Lifecycle && state.Method == "M:KnownCleanup.DisposeAsync");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("mutable", false)]
    [InlineData("authored-getter", false)]
    [InlineData("other-instance", false)]
    [InlineData("virtual", false)]
    [InlineData("startup-only-admission", true)]
    [InlineData("lease-formal", false)]
    [InlineData("depth", true)]
    [InlineData("warm", false)]
    [InlineData("probe-before", true)]
    [InlineData("probe-inside", true)]
    [InlineData("shadowed-scheduler", false)]
    [InlineData("cycle", true)]
    public void ColdHostOwnedTaskAndDeclaredRootShareOneIndependentTraversal(string shape, bool independent)
    {
        string token = shape == "authored-getter" ? "Suspicious" : "shutdown.Token";

        string target = shape == "other-instance" ? "new Worker().ContinueAsync" : "ContinueAsync";

        string startupAdmission = (shape == "startup-only-admission" ? R2Admission : "") + (shape == "probe-before" ? "_ = LazyValues.Value; " : "");

        string workerAdmission = (shape == "startup-only-admission" ? "" : R2Admission) + (shape == "probe-inside" ? "_ = LazyValues.Value; " : "") + (shape == "cycle" ? "await StartAsync(token); " : "");

        string source = R2Source(startupAdmission + "StartupProbe(); background = Task.Run(() => " + target + "(" + token + "), CancellationToken.None);")
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "private readonly CancellationTokenSource shutdown = new(); private void StartupProbe() { } private Task? background; private async Task ContinueAsync(CancellationToken token) { " + workerAdmission + " await Producer.RunAsync(); } public async Task StopAsync(CancellationToken token) { if (background is not null) await background; }", StringComparison.Ordinal)
            + " public static class Producer { public static async Task RunAsync() { await Task.Yield(); System.IO.File.Delete(\"owned-task\"); Leaf(); } private static void Leaf() { System.IO.File.Exists(\"after-depth\"); } }"
            + " internal static class LazyValues { private static readonly Lazy<bool> Cached = new(() => true, LazyThreadSafetyMode.ExecutionAndPublication); public static bool Value => Cached.Value; }";

        if (shape == "mutable")
        {
            source = source.Replace("private readonly CancellationTokenSource shutdown", "private CancellationTokenSource shutdown", StringComparison.Ordinal);
        }

        if (shape == "authored-getter")
        {
            source = source.Replace("private void StartupProbe()", "private CancellationToken Suspicious { get { System.IO.File.Exists(\"getter\"); return shutdown.Token; } } private void StartupProbe()", StringComparison.Ordinal);
        }

        if (shape == "virtual")
        {
            source = source.Replace("private async Task ContinueAsync", "protected virtual async Task ContinueAsync", StringComparison.Ordinal);
        }

        if (shape == "shadowed-scheduler")
        {
            source = "extern alias counterfeit;\n" + source.Replace("background = Task.Run(", "background = counterfeit::System.Threading.Tasks.Task.Run(", StringComparison.Ordinal);
        }

        if (shape == "probe-inside")
        {
            source = source.Replace("private static readonly Lazy<bool> Cached = new(() => true, LazyThreadSafetyMode.ExecutionAndPublication); public static bool Value => Cached.Value;", "private static readonly Lazy<bool> Cached = new(() => Build(true), LazyThreadSafetyMode.ExecutionAndPublication); public static bool Value => Cached.Value; private static bool Build(bool guard) { var operation = new RetroDownfall.Arcanum.Core.Operations.LongRunningOperation(\"owner\", 0); if (guard && operation.Kind == \"owner\") return true; return false; }", StringComparison.Ordinal)
                + " namespace RetroDownfall.Arcanum.Core.Operations { public sealed record LongRunningOperation(string Kind, int CheckpointVersion); }";
        }

        string startupOperation = "Worker.StartAsync::call:Worker.StartupProbe#0~" + HostedGrimoireProducerInventory.Fingerprint(SyntaxFactory.ParseExpression("StartupProbe()"));

        HostedProducerOperationEntry startup = OrdinaryRoot(startupOperation) with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: awaited startup" };

        HostedProducerOperationEntry runtime = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync", LeaseFormal = shape == "lease-formal" ? "token" : null };

        List<HostedProducerAdmittedStateObservation> observed = [];

        List<HostedProducerRootCompletionObservation> completions = [];

        var compilation = Compile(source);

        if (shape == "shadowed-scheduler")
        {
            var counterfeit = Compile("namespace System.Threading.Tasks { public static class Task { public static T Run<T>(System.Func<T> callback, System.Threading.CancellationToken token) => callback(); } }")
                .WithAssemblyName("CounterfeitScheduler");

            using MemoryStream image = new();

            Assert.True(counterfeit.Emit(image).Success);

            compilation = compilation.AddReferences(MetadataReference.CreateFromImage(image.ToArray(), MetadataReferenceProperties.Assembly.WithAliases(["counterfeit"])));
        }

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = shape == "warm"
            ? HostedGrimoireProducerInventory.ProbeIndependentHostDirectFirst(compilation, startup, runtime, observed.Add)
            : HostedGrimoireProducerInventory.DiscoverProducerSites(
                [compilation], new(["Worker"], []), [new("Worker", [startup, runtime])], [], traversalMaximumDepth: shape == "depth" ? 3 : 128, admittedStateObserver: observed.Add, rootCompletionObserver: completions.Add);

        HostedProducerAdmittedStateObservation[] entries = [.. observed.Where(state => state.RootOperation == runtime.OperationId && state.Method.StartsWith("M:Worker.ContinueAsync(", StringComparison.Ordinal))];

        Assert.True(shape == "cycle" ? entries.Length >= 1 && entries.All(static entry => entry.Lifecycle) : entries.Length == (independent ? 1 : 2), string.Join("\n", observed.Select(state => $"{state.Occurrence}|{state.RootOperation}|{state.Method}|{state.Lifecycle}")));

        Assert.All(entries, entry =>
        {
            Assert.Null(entry.WorkFrontier);

            Assert.Null(entry.RegisteredRootWorkFrontier);

            Assert.Null(entry.EffectFrontier);
        });

        if (independent)
        {
            Assert.True(entries[0].Lifecycle);
        }

        if (shape == "cycle")
        {
            Assert.InRange(entries.Length, 1, 8);
        }

        if (shape == "warm")
        {
            Assert.True(Assert.Single(entries, static entry => !entry.Lifecycle).Sequence < Assert.Single(entries, static entry => entry.Lifecycle).Sequence);
        }

        Assert.Single(result.Items, site => site.Callee == "System.IO.File.Delete" && site.AuthorityOperationId == runtime.OperationId);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Equal(shape == "startup-only-admission", result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"));

        Assert.Equal(shape == "startup-only-admission", result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));

        Assert.Equal(shape == "depth", result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED"));

        if (shape is "depth" or "probe-inside" or "cycle")
        {
            HostedProducerRootCompletionObservation direct = Assert.Single(completions, completion => completion.Planned && !completion.Lifecycle && completion.Operation == runtime.OperationId);

            Assert.True(direct.Returned);

            Assert.False(direct.Complete);

            Assert.False(direct.Fresh);
        }

        if (shape == "probe-inside")
        {
            Assert.Contains(completions, completion => completion.Operation.StartsWith("<bounded-data-proof>:", StringComparison.Ordinal));

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "System.Lazy`1.Value");

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_RECOVERY_MATRIX_UNPROVEN");
        }
    }
}
