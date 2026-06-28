// Tests that swap the process-global Spectre AnsiConsole.Console (and assert on the
// captured TestConsole output) must not run concurrently with any other test that
// writes to the global console (e.g. a background daemon alert), or they race and
// flake. Assigning them to this DisableParallelization collection serializes them.
[CollectionDefinition("GlobalConsole", DisableParallelization = true)]
public sealed class GlobalConsoleCollection : ICollectionFixture<object>
{
}
