using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("actual", true)]
    [InlineData("actual-operation", true)]
    [InlineData("actual-host-none", true)]
    [InlineData("actual-host-unknown", false)]
    [InlineData("null-population", false)]
    [InlineData("disposed-source", false)]
    [InlineData("null-completion", false)]
    [InlineData("discarded-cancellation", false)]
    [InlineData("source-alias", false)]
    [InlineData("completion-alias", false)]
    public void Closed_refresh_tracker_task_publication_requires_the_actual_population_and_bindings(string shape, bool safe)
    {
        const string path = "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildSpendRefreshTracker.cs";

        string tracker = File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), path));

        string extra = string.Empty;

        switch (shape)
        {
            case "null-population":
                tracker = tracker.ReplaceSdkFixtureText("entries.Add(entry);", "entries.Add(null!);", StringComparison.Ordinal);
                break;
            case "disposed-source":
                tracker = tracker.ReplaceSdkFixtureText("internal Task CancelAsync() => _stop.CancelAsync();",
                    "internal Task CancelAsync() { _stop.Dispose(); return _stop.CancelAsync(); }", StringComparison.Ordinal);
                break;
            case "null-completion":
                tracker = tracker.ReplaceSdkFixtureText("internal Task Completion => _completion.Task;",
                    "internal Task Completion => null!;", StringComparison.Ordinal);
                break;
            case "discarded-cancellation":
                tracker = tracker.ReplaceSdkFixtureText("static entry => entry.CancelAsync()",
                    "static entry => { _ = entry.CancelAsync(); return Task.CompletedTask; }", StringComparison.Ordinal);
                break;
            case "source-alias":
                extra = "global using CancellationTokenSource = ForeignTrackerStop; public sealed class ForeignTrackerStop { public System.Threading.CancellationToken Token => default; public System.Threading.Tasks.Task CancelAsync() => null!; public void Dispose() { } }";
                break;
            case "completion-alias":
                extra = "global using TaskCompletionSource = ForeignTrackerCompletion; public sealed class ForeignTrackerCompletion { public ForeignTrackerCompletion(System.Threading.Tasks.TaskCreationOptions options) { } public System.Threading.Tasks.Task Task => null!; public bool TrySetResult() => true; }";
                break;
        }

        string call = shape switch
        {
            "actual-operation" => "await tracker.CancelOperationAsync(System.Guid.NewGuid());",
            "actual-host-none" => "await tracker.CloseAllAsync(CancellationToken.None);",
            "actual-host-unknown" => "await tracker.CloseAllAsync(token);",
            _ => "using var closed = await tracker.CloseRootsScopedAsync(System.Array.Empty<System.Guid>());",
        };

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission
            + "var tracker = new RetroDownfall.Arcanum.Infrastructure.Guild.GuildSpendRefreshTracker(); " + call),
            "RetroDownfall.Arcanum.Infrastructure")
            .WithAssemblyName("RetroDownfall.Arcanum.Infrastructure")
            .AddSyntaxTrees(
                CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;", path: "src/TrackerGlobals.cs"),
                CSharpSyntaxTree.ParseText(tracker, path: path),
                CSharpSyntaxTree.ParseText(extra, path: "src/TrackerShadow.cs"));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.Equal(!safe, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Detail.Contains("GuildSpendRefreshTracker.Entry.CancelAsync", StringComparison.Ordinal)));
    }
}
