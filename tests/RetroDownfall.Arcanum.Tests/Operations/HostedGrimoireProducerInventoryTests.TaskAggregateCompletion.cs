using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("direct", true)]
    [InlineData("configured", true)]
    [InlineData("none-wait", true)]
    [InlineData("owned-return", true)]
    [InlineData("dropped", false)]
    [InlineData("unrelated-return", false)]
    [InlineData("when-any", false)]
    [InlineData("filtered", false)]
    [InlineData("discarded-child", false)]
    [InlineData("cancelable-wait", false)]
    [InlineData("throwing-enumerable", false)]
    [InlineData("throwing-selector", false)]
    [InlineData("null-task", false)]
    [InlineData("synchronous-forwarder", false)]
    [InlineData("async-discarded-child", false)]
    [InlineData("custom-builder", false)]
    public void TaskAggregateCompletionRequiresEveryProducedTaskAndItsActualJoin(string shape, bool owned)
    {
        string selector = shape switch
        {
            "discarded-child" => "entry => { _ = entry.CancelAsync(); return Task.CompletedTask; }",
            "throwing-selector" => "(entry, index) => index == 1 ? throw new System.InvalidOperationException() : entry.CancelAsync()",
            "null-task" => "(entry, index) => index == 1 ? null! : entry.CancelAsync()",
            "synchronous-forwarder" => "static entry => entry.CancelAsync()",
            "async-discarded-child" => "static async entry => { _ = entry.CancelAsync(); await Task.CompletedTask; }",
            "custom-builder" => "Runner.Run",
            _ => "static async entry => await entry.CancelAsync().ConfigureAwait(false)",
        };

        string sequence = "System.Linq.Enumerable.Select(entries, " + selector + ")";

        string aggregate = "Task.WhenAll(" + sequence + ")";

        string statement = shape switch
        {
            "direct" => "await " + aggregate + ";",
            "configured" => "await " + aggregate + ".ConfigureAwait(false);",
            "none-wait" => "await " + aggregate + ".WaitAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);",
            "owned-return" => "await Drain(entries);",
            "dropped" => "_ = " + aggregate + ";",
            "unrelated-return" => "await Unrelated(entries);",
            "when-any" => "await Task.WhenAny(" + sequence + ");",
            "filtered" => "await Task.WhenAll(System.Linq.Enumerable.Where(" + sequence + ", static task => task.IsCompleted));",
            "cancelable-wait" => "await " + aggregate + ".WaitAsync(token);",
            _ => "await " + aggregate + ";",
        };

        string array = shape == "throwing-enumerable"
            ? "var entries = Entries(); "
            : "var entries = new Entry[] { new Entry(), new Entry() }; ";

        string helpers = "sealed class Entry { internal async Task CancelAsync() { await Task.CompletedTask; System.IO.File.Delete(\"aggregate-child\"); } }";

        if (shape == "custom-builder")
        {
            helpers += AsyncBuilderHelpers("none")
                .Replace("System.Threading.Tasks.ValueTask", "System.Threading.Tasks.Task", StringComparison.Ordinal)
                .Replace("Run()", "Run(Entry entry)", StringComparison.Ordinal)
                .Replace("await System.Threading.Tasks.Task.CompletedTask;", "await entry.CancelAsync();", StringComparison.Ordinal);
        }

        string members = "private static Task Drain(Entry[] entries) => " + aggregate + "; "
            + "private static Task Unrelated(Entry[] entries) { _ = " + aggregate + "; return Task.CompletedTask; } "
            + "private static System.Collections.Generic.IEnumerable<Entry> Entries() { yield return new Entry(); throw new System.InvalidOperationException(); } ";

        string source = R2Source(R2Admission + array + statement + " await Task.CompletedTask;", helpers)
            .Replace("public async Task StartAsync", members + " public async Task StartAsync", StringComparison.Ordinal);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.NotEmpty(EnumerationDeleteSites(result));

        Assert.All(EnumerationDeleteSites(result), site =>
        {
            Assert.Equal(owned, site.WorkFrontierCapsuleId is not null);

            Assert.Equal(owned, site.EffectFrontierCapsuleId is not null);
        });

        Assert.Equal(!owned, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));
    }

    [Fact]
    public void Foreign_selector_compilation_cannot_borrow_the_callers_stock_async_builder()
    {
        string builder = AsyncBuilderHelpers("none").Split("sealed class MyBuilder", StringSplitOptions.None)[1]
            .Replace("MyBuilder", "AsyncTaskMethodBuilder", StringComparison.Ordinal)
            .Replace("System.Threading.Tasks.ValueTask", "System.Threading.Tasks.Task", StringComparison.Ordinal);

        CSharpCompilation foreign = Compile("public static class Runner { public static async System.Threading.Tasks.Task Run(int entry) { await System.Threading.Tasks.Task.CompletedTask; System.IO.File.Delete(\"foreign-selector\"); } } "
            + "namespace System.Runtime.CompilerServices { public sealed class AsyncTaskMethodBuilder" + builder + " }")
            .WithAssemblyName("Foreign.Selectors");

        Assert.Empty(foreign.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        CSharpCompilation compilation = Compile(R2Source(R2Admission
            + "await Task.WhenAll(System.Linq.Enumerable.Select(new int[] { 0, 1 }, Runner.Run));"))
            .AddReferences(foreign.ToMetadataReference());

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation, foreign], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.NotEmpty(EnumerationDeleteSites(result));

        Assert.All(EnumerationDeleteSites(result), static site =>
        {
            Assert.Null(site.WorkFrontierCapsuleId);

            Assert.Null(site.EffectFrontierCapsuleId);
        });

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrayTaskAggregateConstructionCanAbortWithAnEarlierChildStillRunning(bool nullTask)
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task? child = null;

        Task? aggregate = null;

        async Task RunChildAsync() => await release.Task.ConfigureAwait(false);

        try
        {
            Exception? failure = Record.Exception(() =>
            {
                aggregate = Task.WhenAll(new[] { 0, 1 }.Select(index =>
                {
                    if (index == 0)
                    {
                        return child = RunChildAsync();
                    }

                    return nullTask ? null! : throw new InvalidOperationException("selector failed");
                }));
            });

            Assert.NotNull(failure);

            Assert.Null(aggregate);

            Assert.NotNull(child);

            Assert.False(child.IsCompleted);
        }
        finally
        {
            release.TrySetResult();

            if (child is not null)
            {
                await child;
            }
        }
    }
}
