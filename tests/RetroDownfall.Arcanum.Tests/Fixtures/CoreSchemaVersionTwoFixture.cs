using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// The Core head tree as it stood at schema version 2, reconstructed so an upgrade can be driven from a
/// real version-2 installation rather than from a hand-written metadata row.
/// </summary>
/// <remarks>
/// Version 3 only <i>adds</i> objects and edits none, so the reconstruction is the version-3 tree with
/// every Annals object removed. That is also what keeps it honest: the fingerprint this list produces
/// is compared against the pin the shipped chain carries for version 2, so a reconstruction that
/// drifted fails rather than quietly certifying the wrong pin.
///
/// <para>Each fixture peels back exactly one version, which is why this one starts from
/// <see cref="CoreSchemaVersionThreeFixture"/> rather than from the shipped catalog. Version 4 edits
/// <c>saga_memories</c>, and versions 2 and 3 declared that table identically, so
/// <see cref="CoreSchemaVersionThreeFixture"/> has already frozen the text this fixture needs and no
/// freeze of its own is required here — only the objects version 3 added have to come back off.</para>
///
/// <para>A later version step that <i>edits</i> a Core object also present at version 2 has to freeze
/// that object's version-2 text here, exactly as <see cref="CoreSchemaVersionOneFixture"/> freezes two
/// objects, or the reconstruction stops describing version 2 and the pin assertion says so.</para>
/// </remarks>
internal static class CoreSchemaVersionTwoFixture
{

    /// <summary>The prefix every object version 3 introduced shares.</summary>
    private const string AnnalsObjectPrefix = "annal_";

    /// <summary>Every Core object as version 2 declared it.</summary>
    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. CoreSchemaVersionThreeFixture.Objects.Where(
            static definition => !definition.Name.StartsWith(AnnalsObjectPrefix, StringComparison.Ordinal)),
    ];

    /// <summary>The fingerprint the version-2 tree published, computed from the reconstruction above.</summary>
    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeRawSourceFingerprint(Objects);

    /// <summary>
    /// An installable version-2 chain set: the reconstructed Core tree at version 2 with no step, and
    /// the shipped chains for both Covenant tiers.
    /// </summary>
    /// <remarks>
    /// Installing this and then handing the same installer <see cref="GrimoireSchemaVersionChains.Default"/>
    /// is the whole of an upgrade as a caller reaches it. Nothing writes a metadata row or a journal row
    /// to describe the older installation, so no assertion rests on a state a test invented.
    ///
    /// <para>The chain carries the shipped step to version 2, because a chain needs exactly one step per
    /// version above 1 and that step is the one version 2 actually shipped with — it pins the version-1
    /// fingerprint and names the sweep version 2 depended on. A fresh install runs no step at all, so the
    /// step is here to make this a faithful statement of what version 2 was rather than to be executed.</para>
    /// </remarks>
    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 2,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps
                        .Where(static step => step.ToVersion == 2),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

}
