// SpellScannerTests drive SpellScanner, which holds STATIC caches (MetadataScanCache,
// FullSpellCache, MetadataScanInFlight, FullSpellInFlight) and scans the shared global
// spells directory. Parallel test instances pollute each other's cache lookups and
// collide on the global scan, so the class must run serialized.
[CollectionDefinition("SpellScanner", DisableParallelization = true)]
public sealed class SpellScannerCollection : ICollectionFixture<object>
{
}
