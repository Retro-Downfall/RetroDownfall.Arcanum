namespace RetroDownfall.Arcanum.Tests.Collections;

/// <summary>
/// Isolates the whole-program Roslyn producer inventory from the parallel test suite.
/// </summary>
/// <remarks>
/// The production inventory retains several complete compilations and a large recursive analysis
/// graph. Running it beside thousands of coverage-instrumented tests can force the process into
/// sustained memory pressure and turn a bounded scan into hours of CPU work.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostedProducerAnalysisCollection
{

    public const string Name = "HostedProducerAnalysis";

}
