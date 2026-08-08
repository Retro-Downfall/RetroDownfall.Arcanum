/// <summary>
/// Serializes tests that assign a process-global seam other than an environment variable.
///
/// Production code exposes a handful of deliberate static test hooks, and Serilog routes every log
/// record in the process through one static <c>Log.Logger</c>. A test that installs any of them
/// changes behaviour for every test running beside it, and a test that then asserts over what its
/// own sink or counter captured is measuring the whole suite rather than itself. Either way the
/// failure lands on whichever test happened to be running at the time, which reads as an unrelated
/// test being flaky.
///
/// <c>ProcessEnvironment</c> already provides this guarantee for environment variables; this is the
/// same guarantee for the other globals, kept separate so each collection's purpose stays legible.
/// <c>EnvironmentIsolationContractTests</c> enforces membership.
/// </summary>
[CollectionDefinition(ProcessGlobalSeamCollectionName.Value, DisableParallelization = true)]
public sealed class ProcessGlobalSeamCollection : ICollectionFixture<object>
{
}

internal static class ProcessGlobalSeamCollectionName
{

    internal const string Value = "ProcessGlobalSeam";

}
