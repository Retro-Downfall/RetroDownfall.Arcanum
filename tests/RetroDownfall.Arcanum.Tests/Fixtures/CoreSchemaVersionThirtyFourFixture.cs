using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The published Core34 shape, unchanged by Core35's retirement of optional mirrors.</summary>
internal static class CoreSchemaVersionThirtyFourFixture
{
    internal const string PublishedFingerprint = "1243F9B7B5256B4C07A71E030C145D20804297950101E93A5E411510D872E148";

    internal static GrimoireSchemaVersionChainSet ChainSet()
    {
        GrimoireSchemaVersionChainSet current = GrimoireSchemaVersionChains.Default;

        GrimoireSchemaVersionChain core = current.ForTier(GrimoireSchemaTransactionTier.Core);

        return new GrimoireSchemaVersionChainSet(
        [
            new(core.HeadManifest with { Version = 34, SourceDefinitionFingerprint = PublishedFingerprint }, core.HeadObjects, core.Steps.Where(static step => step.ToVersion <= 34).ToArray()),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);
    }
}
