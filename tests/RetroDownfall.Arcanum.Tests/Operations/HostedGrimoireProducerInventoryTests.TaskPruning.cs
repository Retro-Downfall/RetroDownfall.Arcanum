using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("owned", false)]
    [InlineData("remove-before-await", true)]
    [InlineData("await-different-task", true)]
    [InlineData("clear", true)]
    [InlineData("replace", true)]
    [InlineData("escape", true)]
    [InlineData("capture", true)]
    [InlineData("missing-final-drain", true)]
    [InlineData("cancelable-item-join", true)]
    [InlineData("cancelable-final-join", true)]
    public void Completed_task_pruning_requires_exact_observation_and_final_drain(string shape, bool refused)
    {
        string join = shape switch
        {
            "await-different-task" => "await Task.CompletedTask.ConfigureAwait(false);",
            "cancelable-item-join" => "await completed.WaitAsync(token).ConfigureAwait(false);",
            _ => "await completed.ConfigureAwait(false);",
        };

        string finalJoin = shape switch
        {
            "missing-final-drain" => "await Task.CompletedTask;",
            "cancelable-final-join" => "await Task.WhenAll(active).WaitAsync(token).ConfigureAwait(false);",
            _ => "await Task.WhenAll(active).ConfigureAwait(false);",
        };

        string remove = "active.RemoveAt(index);";

        string cleanup = shape == "remove-before-await" ? remove + join : join + remove;

        string extra = shape switch
        {
            "clear" => "active.Clear();",
            "replace" => "active[index] = Task.CompletedTask;",
            "escape" => "Child.Escape(active);",
            "capture" => "System.Action capture = () => active.Clear(); capture();",
            _ => "",
        };

        string source = R2Source(R2Admission + $$"""
            System.Collections.Generic.List<Task> active = new();
            try
            {
                foreach (int item in new[] { 1, 2 })
                {
                    for (int index = active.Count - 1; index >= 0; index--)
                    {
                        Task completed = active[index];
                        if (!completed.IsCompleted) continue;
                        {{extra}}
                        {{cleanup}}
                    }
                    Task execution = Child.SendAsync();
                    active.Add(execution);
                }
            }
            finally
            {
                {{finalJoin}}
            }
            """, """
            static class Child
            {
                internal static async Task SendAsync()
                {
                    await Task.Yield();
                    System.IO.File.Delete("owned-task-effect");
                }
                internal static void Escape(object value) { }
            }
            """);

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var discovered = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation],
            new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(discovered.Items, static site => site.Callee == "System.IO.File.Delete");

        bool unowned = discovered.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Detail.StartsWith("Child.SendAsync;", StringComparison.Ordinal));

        Assert.Equal(refused, unowned);

        if (!refused)
        {
            Assert.DoesNotContain(discovered.Diagnostics, static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

            var send = Assert.Single(discovered.Items, static site => site.Callee == "System.IO.File.Delete");

            Assert.NotNull(send.WorkFrontierCapsuleId);

            Assert.NotNull(send.EffectFrontierCapsuleId);
        }
    }
}
