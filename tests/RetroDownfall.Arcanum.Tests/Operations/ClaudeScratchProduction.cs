using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

// Scratch only (never committed).
public sealed class ClaudeScratchProduction
{
    [Fact]
    public void DumpScoped()
    {
        string lane = global::System.Environment.GetEnvironmentVariable("CLAUDE_LANE") ?? "additional";

        string[] members = (global::System.Environment.GetEnvironmentVariable("CLAUDE_MEMBERS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

        List<string> probe = [];

        HostedGrimoireProducerInventory.CLAUDE_SCRATCH_PROBE_SINK = probe.Add;

        List<HostedProducerAdmittedStateObservation> states = [];

        HostedGrimoireProducerInventory.CLAUDE_SCRATCH_STATE_SINK = states.Add;

        var result = HostedGrimoireProducerInventory.ScratchDiscover(lane, members);

        List<string> lines = [.. probe.Select(static line => "PROBE " + line), .. result.Diagnostics.Select(static d => d.Code + ": " + d.Identity + ": " + d.Detail)];

        foreach (var metric in result.AnalysisMetrics!.RootTraversals) lines.Add("ROOT " + metric.RootType + " " + metric.RootOperation + " states=" + metric.AnalyzedStates + " calls=" + metric.TraversalCalls + " ms=" + (int)metric.Elapsed.TotalMilliseconds);

        foreach (var group in states.GroupBy(static state => state.RootOperation + " | " + state.Method).OrderByDescending(static group => group.Count()).Take(80))
        {
            lines.Add("MEMBERSTATES " + group.Count() + " distinctEnv=" + group.Select(static s => (s.EvaluationEnvironmentToken, s.FallbackValueBindings, s.FallbackAdmissionBindings, s.FallbackConcreteBindings, s.FallbackEvaluationEnvironment)).Distinct().Count() + " distinctFrontier=" + group.Select(static s => (s.WorkFrontier, s.EffectFrontier, s.RecoveryEffectFrontier)).Distinct().Count() + " distinctSel=" + group.Select(static s => (s.SelectionStart, s.SelectionLength)).Distinct().Count() + " " + group.Key);
        }

        global::System.IO.File.WriteAllLines(global::System.Environment.GetEnvironmentVariable("CLAUDE_DUMP") + ".scoped.txt", lines);
    }
}
