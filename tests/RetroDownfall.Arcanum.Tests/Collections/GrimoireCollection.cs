/// <summary>
/// Serialized because several of these classes install process-global test seams.
///
/// The Grimoire tests share one SQLCipher fixture, so xUnit already runs the classes in this
/// collection one at a time — but without <c>DisableParallelization</c> the collection as a whole
/// still ran alongside every un-attributed class. Members here assign static seams that production
/// code reads (<c>FileHandleIdentityInterop</c>, <c>SessionWriteLock</c>) and attach a
/// <c>MeterListener</c> to the shared Arcanum meter, all of which are visible to every test in the
/// process for as long as they are installed. <c>EnvironmentIsolationContractTests</c> enforces
/// this.
/// </summary>
[CollectionDefinition("Grimoire", DisableParallelization = true)]
public sealed class GrimoireCollection : ICollectionFixture<RetroDownfall.Arcanum.Tests.Fixtures.GrimoireFixture>
{
}
