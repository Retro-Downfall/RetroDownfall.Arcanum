using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// The Core head tree as it stood at schema version 3, reconstructed so an upgrade can be driven from a
/// real version-3 installation rather than from a hand-written metadata row.
/// </summary>
/// <remarks>
/// Version 4 both <i>adds</i> two objects and <i>edits</i> one, so the reconstruction has to do both:
/// the two suppression tables are removed, and <c>saga_memories.sql</c>'s frozen version-3 text is
/// substituted for the shipped one. That is also what keeps it honest: the fingerprint this list
/// produces is compared against the pin the shipped chain carries for version 4, so a reconstruction
/// that drifted fails rather than quietly certifying the wrong pin.
///
/// <para>It peels back from <see cref="CoreSchemaVersionFourFixture"/> rather than from the shipped
/// catalog, because each fixture removes exactly one version's worth of change and rebases on the one
/// above it. Reading the shipped catalog directly would silently absorb every object a later version
/// adds, and the first symptom would be the version-4 pin assertion failing for a reason that has
/// nothing to do with version 4.</para>
///
/// <para>A later version step that <i>edits</i> a third Core object has to freeze that object's
/// version-3 text here as well, or the reconstruction stops describing version 3 and the pin assertion
/// says so.</para>
/// </remarks>
internal static class CoreSchemaVersionThreeFixture
{

    /// <summary>The two objects version 4 introduced.</summary>
    private static readonly string[] VersionFourObjectNames =
    [
        "saga_retirement_suppressions",
        "saga_suppression_key",
    ];

    /// <summary><c>saga_memories</c> before it carried the two curation lifecycle columns.</summary>
    private const string SagaMemoriesSql =
        """
        -- ScopeKindCode and CampaignId are laid out the way SQLite lays out an added column, not the way the
        -- rest of this file is indented, and that is deliberate. Version 2 reaches an existing installation
        -- through ALTER TABLE ... ADD COLUMN, which rewrites the stored table declaration by splicing
        -- ", <column-def>" in front of the closing parenthesis and taking the definition verbatim. The
        -- installer then compares that stored text with this file, normalized. A version-2 installation built
        -- fresh from this file and one evolved from version 1 have to normalize to the same string, so this
        -- file has to be written in the shape ALTER produces. Reindenting these two columns reports
        -- DefinitionDrift on every evolved installation and on none of the fresh ones, which is the hardest
        -- shape of that failure to reproduce.
        --
        -- The two columns are separate on purpose. A single nullable CampaignId would make "explicitly
        -- installation-global" and "ownership never resolved" the same null, and those two answers are
        -- opposites: the first is retrievable inside every Campaign, the second inside none until an operator
        -- resolves the binding. Codes 1 to 3 are the codes session_campaign_bindings.BindingKindCode already
        -- uses, because a memory's scope is its owning Session's binding at the moment it was written; 0 means
        -- an upgrade has not classified the row yet and is likewise retrievable nowhere.
        --
        -- The invariant "CampaignId is present exactly when ScopeKindCode is 2" is not a table CHECK, because
        -- SQLite's ALTER cannot add one and an evolved installation could therefore never match a file that
        -- declared it. SagaMemoryScopeKind and its writers own it instead.
        CREATE TABLE IF NOT EXISTS saga_memories (
            Id TEXT PRIMARY KEY,
            Content TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            SessionId TEXT,
            Tags TEXT,
            Source TEXT
        , ScopeKindCode INTEGER NOT NULL DEFAULT 0, CampaignId TEXT);

        CREATE INDEX IF NOT EXISTS idx_saga_memories_session ON saga_memories(SessionId);

        CREATE INDEX IF NOT EXISTS idx_saga_memories_created ON saga_memories(CreatedAt);

        -- Campaign-scoped retrieval reads this on every turn, and the classification sweep pages the same
        -- index looking for code 0, so the kind leads and the Campaign follows it.
        CREATE INDEX IF NOT EXISTS idx_saga_memories_scope ON saga_memories(ScopeKindCode, CampaignId);

        """;

    /// <summary>
    /// Every Core object as version 3 declared it, peeled back one version from the version-4 tree.
    /// </summary>
    /// <remarks>
    /// Line endings are normalized because the frozen text above is a C# literal and the catalog's text
    /// is an embedded file. A checkout that handed one of them CRLF would move the fingerprint without
    /// changing a single character of SQL.
    /// </remarks>
    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. CoreSchemaVersionFourFixture.Objects
            .Where(static definition => !VersionFourObjectNames.Contains(definition.Name, StringComparer.Ordinal))
            .Select(static definition => definition.Name switch
            {

                "saga_memories" => definition with { Sql = SagaMemoriesSql.ReplaceLineEndings("\n") },

                _ => definition,

            }),
    ];

    /// <summary>The fingerprint the version-3 tree published, computed from the reconstruction above.</summary>
    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    /// <summary>
    /// An installable version-3 chain set: the reconstructed Core tree at version 3 with the two steps
    /// that reach it, and the shipped chains for both Covenant tiers.
    /// </summary>
    /// <remarks>
    /// Installing this and then handing the same installer <see cref="GrimoireSchemaVersionChains.Default"/>
    /// is the whole of an upgrade as a caller reaches it. Nothing writes a metadata row or a journal row
    /// to describe the older installation, so no assertion rests on a state a test invented.
    ///
    /// <para>The chain carries the two shipped steps to version 3, because a chain needs exactly one step
    /// per version above 1 and those are the steps version 3 actually shipped with — the first pins the
    /// version-1 fingerprint and names the Campaign-scope sweep, the second pins the version-2 fingerprint
    /// and names the Annals backfill. A fresh install runs neither, so they are here to make this a
    /// faithful statement of what version 3 was rather than to be executed.</para>
    /// </remarks>
    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 3,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps
                        .Where(static step => step.ToVersion <= 3),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

}
