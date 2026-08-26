using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// The Core head tree as it stood at schema version 1, reconstructed so an upgrade can be driven from a
/// real version-1 installation rather than from a hand-written metadata row.
/// </summary>
/// <remarks>
/// Two objects moved when Core reached version 2, so only those two are frozen here; every other Core
/// object is taken from the shipped catalog because it is unchanged. That is also what keeps the
/// reconstruction honest: the fingerprint this list produces is compared against the pin the shipped
/// chain carries for version 1, so a reconstruction that drifted from the real version-1 tree fails
/// rather than quietly certifying the wrong pin.
///
/// <para>A later version step that edits a third Core object has to freeze that object's version-2 text
/// here as well, or the reconstruction stops describing version 1 and the pin assertion says so.</para>
/// </remarks>
internal static class CoreSchemaVersionOneFixture
{

    /// <summary><c>saga_memories</c> before it carried a scope classification.</summary>
    internal const string SagaMemoriesSql =
        """
        CREATE TABLE IF NOT EXISTS saga_memories (
            Id TEXT PRIMARY KEY,
            Content TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            SessionId TEXT,
            Tags TEXT,
            Source TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_saga_memories_session ON saga_memories(SessionId);

        CREATE INDEX IF NOT EXISTS idx_saga_memories_created ON saga_memories(CreatedAt);

        """;

    /// <summary><c>lexicon_entries</c> when one normalized name meant one entity installation-wide.</summary>
    internal const string LexiconEntriesSql =
        """
        CREATE TABLE IF NOT EXISTS lexicon_entries (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            NameNormalized TEXT NOT NULL,
            Type TEXT NOT NULL,
            FactsJson TEXT NOT NULL,
            FactsText TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS IX_lexicon_entries_NameNormalized
        ON lexicon_entries(NameNormalized);

        """;

    /// <summary>
    /// Every Core object as version 1 declared it.
    /// </summary>
    /// <remarks>
    /// Line endings are normalized because the frozen text above is a C# literal and the catalog's text
    /// is an embedded file. A checkout that handed one of them CRLF would move the fingerprint without
    /// changing a single character of SQL.
    /// </remarks>
    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects.Select(
            static definition => definition.Name switch
            {

                "saga_memories" => definition with { Sql = SagaMemoriesSql.ReplaceLineEndings("\n") },

                "lexicon_entries" => definition with { Sql = LexiconEntriesSql.ReplaceLineEndings("\n") },

                _ => definition,

            }),
    ];

    /// <summary>The fingerprint the version-1 tree published, computed from the reconstruction above.</summary>
    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    /// <summary>
    /// An installable version-1 chain set: the reconstructed Core tree at version 1 with no step, and
    /// the shipped chains for both Covenant tiers.
    /// </summary>
    /// <remarks>
    /// Installing this and then handing the same installer <see cref="GrimoireSchemaVersionChains.Default"/>
    /// is the whole of an upgrade as a caller reaches it. Nothing writes a metadata row or a journal row
    /// to describe the older installation, so no assertion here rests on a state a test invented.
    /// </remarks>
    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 1,
                    Fingerprint,
                    Objects),
                Objects,
                []),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

}
