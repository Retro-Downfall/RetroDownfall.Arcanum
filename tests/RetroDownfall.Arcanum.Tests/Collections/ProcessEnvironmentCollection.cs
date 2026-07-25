// Tests that mutate process-global environment variables (e.g. HOME / USERPROFILE,
// which ArcanumPaths reads) must not run concurrently with other tests that read
// those paths, or they race and flake. Assigning them to this DisableParallelization
// collection serializes them against every other collection.
[CollectionDefinition("ProcessEnvironment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection :
    ICollectionFixture<object>,
    ICollectionFixture<RetroDownfall.Arcanum.Tests.Fixtures.GrimoireFixture>
{
}
