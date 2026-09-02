using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// The Covenant canonical head tree as it stood at schema version 1, reconstructed so an upgrade can be
/// driven from a real version-1 installation rather than from a hand-written metadata row.
/// </summary>
/// <remarks>
/// Version 2 only <i>adds</i> objects and edits none, so the reconstruction is the shipped canonical tree
/// with every curation object removed. That is also what keeps it honest: the fingerprint this list
/// produces is compared against the pin the shipped chain carries for version 2, so a reconstruction that
/// drifted fails here rather than quietly certifying the wrong pin — and a wrong pin refuses every
/// operator's version-1 installation with a source-definition mismatch.
///
/// <para>A later version step that <i>edits</i> a canonical object has to freeze that object's version-1
/// text here, or the reconstruction stops describing version 1 and the pin assertion says so.</para>
/// </remarks>
internal static class CovenantCanonicalSchemaVersionOneFixture
{

    /// <summary>The prefix every object version 2 introduced shares.</summary>
    private const string CurationObjectPrefix = "covenant_curation";

    /// <summary>Every Covenant canonical object as version 1 declared it.</summary>
    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CovenantCanonicalObjects
            .Where(static definition => !definition.Name.StartsWith(CurationObjectPrefix, StringComparison.Ordinal))
            .Select(static definition => definition.Name == "covenant_versions"
                ? CovenantCanonicalSchemaVersionTwoFixture.CovenantVersionsObject
                : definition),
    ];

    /// <summary>The fingerprint the version-1 tree published, computed from the reconstruction above.</summary>
    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeRawSourceFingerprint(Objects);

    /// <summary>
    /// An installable version-1 chain set: the shipped Core chain, the reconstructed canonical tree at
    /// version 1 with no step, and the shipped accelerator chain.
    /// </summary>
    /// <remarks>
    /// Installing this and then handing the same installer <see cref="GrimoireSchemaVersionChains.Default"/>
    /// is the whole of an upgrade as a caller reaches it. Nothing writes a metadata row or a journal row to
    /// describe the older installation, so no assertion rests on a state a test invented.
    /// </remarks>
    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core),
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Covenant,
                    GrimoireSchemaTransactionTier.CovenantCanonical,
                    version: 1,
                    Fingerprint,
                    Objects),
                Objects,
                []),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

}
