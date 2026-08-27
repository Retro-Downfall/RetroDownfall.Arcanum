using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The step that settles every stored identity on one spelling, driven the way a host reaches it:
/// install version 4, then hand the same installer the shipped chain and let the shipped driver drain
/// the sweep.
/// </summary>
/// <remarks>
/// Version 5 is a <i>verifier</i> before it is a repair. Both minority-spelling writers were
/// unreachable for their entire existence, so an installation that predates this work already holds
/// the canonical form and the sweep counts zero, rewrites nothing, and records that it found nothing.
/// The repair arm exists because source can prove no code path wrote a bad row and cannot prove
/// nobody edited the database by hand.
///
/// <para>What the repair arm may touch is narrower than "every identity column", and the boundary is
/// the schema's own immutability contract rather than a preference. A Session identity cannot be moved
/// in place at all: eight of its fourteen foreign-key children refuse the write by trigger, four of
/// them unconditionally, and <c>session_turn_quota_state</c> holds one row for every Session ever
/// created. An identity that is only <i>referenced</i> by an unenforced column - the Campaign a Session
/// names, the Entry an embedding belongs to - can be moved, and those are the two the plan calls out as
/// the expensive silent failures. So the repair is scoped to a reference whose canonical target already
/// exists, which is provably a restoration of a broken pairing rather than a rewrite that could break a
/// working one.</para>
/// </remarks>
public sealed class IdentitySpellingEvolutionTests
{

    /// <summary>A Campaign spelled the way the object-relational writer spells one.</summary>
    private static readonly Guid CampaignIdentity = new("A0000000-0000-4000-8000-00000000000C");

    /// <summary>A Session spelled the way the object-relational writer spells one.</summary>
    private static readonly Guid SessionIdentity = new("B0000000-0000-4000-8000-00000000000E");

    /// <summary>An Entry spelled the way the object-relational writer spells one.</summary>
    private static readonly Guid EntryIdentity = new("C0000000-0000-4000-8000-000000000001");

    static IdentitySpellingEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    /// <summary>
    /// The pin is a literal captured before the version-4 tree was edited, and nothing can recompute it
    /// from a tree that no longer exists. Reconstructing that tree and hashing it is the only check that
    /// the pinned value is the one version 4 actually published - and a wrong pin means every version-4
    /// installation refuses the upgrade with <c>SourceDefinitionMismatch</c>.
    /// </summary>
    [Fact]
    public void Version_four_reconstruction_matches_the_pinned_fingerprint()
    {

        Assert.Equal(
            "35B3B5AD90B8BE3571516C88CB0FDF4F8E61712F86F8D1134D07D92B3F980AC1",
            CoreSchemaVersionFourFixture.Fingerprint);

        GrimoireSchemaVersionChain core =
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        Assert.Equal(CoreSchemaVersionFourFixture.Fingerprint, core.SourceDefinitionFingerprintFor(4));

    }

    /// <summary>
    /// An installation evolved to version 5 and one installed fresh at version 5 have to describe the
    /// same tree, object for object and character for character after normalization.
    /// </summary>
    [Fact]
    public async Task Evolving_a_version_four_installation_reaches_the_shipped_version_five_tree()
    {

        IReadOnlyDictionary<string, string> evolved = await GuardDefinitionsAsync(evolve: true);

        IReadOnlyDictionary<string, string> fresh = await GuardDefinitionsAsync(evolve: false);

        Assert.NotEmpty(fresh);

        Assert.Equal(
            fresh.Keys.OrderBy(static name => name, StringComparer.Ordinal),
            evolved.Keys.OrderBy(static name => name, StringComparer.Ordinal));

        foreach ((string name, string definition) in fresh)
        {

            Assert.Equal(
                GrimoireSqlNormalizer.Normalize(definition),
                GrimoireSqlNormalizer.Normalize(evolved[name]));

        }

    }

    /// <summary>
    /// The failure the whole plan names as the expensive one: the weaving service left-joins
    /// <c>entry_embeddings</c> to <c>Entries</c>, so an embedding whose <c>EntryId</c> is spelled
    /// differently from the Entry it belongs to reports that Entry as unembedded and the corpus is
    /// silently re-embedded at provider cost.
    /// </summary>
    /// <remarks>
    /// The Entry itself is written the way the object-relational writer writes one, because that is what
    /// every installation holds; the embedding row carries the minority form. The assertion is that the
    /// two <i>join</i> again, not merely that a column changed case.
    ///
    /// <para>Stated plainly, because a fixture that misrepresents its own provenance is how this family
    /// keeps producing green tests that prove nothing: <b>no writer produces this exact pair.</b> The
    /// weaving service copies whatever spelling <c>Entries."Id"</c> holds into <c>EntryId</c>, so on a
    /// healthy installation the two always agree. A canonical Entry beside a minority-spelled embedding
    /// is reachable only by an edit made outside Arcanum - which is the case the repair arm exists for,
    /// since source can prove no code path wrote a bad row and cannot prove nobody edited the database.
    /// What it is not is a state this suite invented to be convenient: it is the exact mismatch the
    /// design names as the expensive one, and the arm is here to fix it wherever it came from.</para>
    /// </remarks>
    [Fact]
    public async Task A_legacy_spelled_embedding_is_repaired_onto_the_entry_it_belongs_to()
    {

        await using IdentitySpellingUpgradeHarness harness = await IdentitySpellingUpgradeHarness.StartAsync();

        await harness.SeedSessionAsync(SessionIdentity, CampaignIdentity, campaignCanonical: true);

        await harness.SeedEntryAsync(EntryIdentity, SessionIdentity);

        await harness.SeedEmbeddingAsync(Legacy(EntryIdentity));

        Assert.Equal(0, await harness.EmbeddedEntryCountAsync());

        await harness.UpgradeAsync();

        Assert.Equal(Canonical(EntryIdentity), await harness.ScalarStringAsync("SELECT EntryId FROM entry_embeddings"));

        Assert.Equal(1, await harness.EmbeddedEntryCountAsync());

    }

    /// <summary>
    /// The unregistered member of the family: a Session's Campaign reference has no foreign key, and two
    /// live comparisons bind the canonical form against it - a Campaign deletion that clears the column,
    /// and a Campaign-filtered session listing. A Session spelling it the minority way keeps pointing at
    /// a deleted Campaign and is omitted from that listing.
    /// </summary>
    /// <remarks>
    /// Unlike the embedding case, this seed is the exact shape a shipped writer rendered: the protected
    /// artifact transfer store spelled an imported Session's destination Campaign with a bare
    /// <c>ToString()</c> until that writer was converted earlier in this work. The Campaign it names is
    /// canonical, because the object-relational writer is the only thing that has ever created one.
    /// </remarks>
    [Fact]
    public async Task A_legacy_spelled_campaign_reference_is_repaired_onto_the_campaign_it_names()
    {

        await using IdentitySpellingUpgradeHarness harness = await IdentitySpellingUpgradeHarness.StartAsync();

        await harness.SeedSessionAsync(SessionIdentity, CampaignIdentity, campaignCanonical: false);

        Assert.Equal(0, await harness.SessionsInCampaignAsync(Canonical(CampaignIdentity)));

        await harness.UpgradeAsync();

        Assert.Equal(
            Canonical(CampaignIdentity),
            await harness.ScalarStringAsync("SELECT \"CampaignId\" FROM \"Sessions\""));

        Assert.Equal(1, await harness.SessionsInCampaignAsync(Canonical(CampaignIdentity)));

    }

    /// <summary>
    /// A pairing that already agrees is left alone, whichever spelling it agrees on.
    /// </summary>
    /// <remarks>
    /// This is the case that decides whether the repair is safe rather than merely tidy. A Session
    /// identity cannot be moved in place - <c>session_turn_quota_state</c>, <c>session_turn_claims</c>,
    /// the two artifact tables, the two tombstone tables and the binding all refuse the write by trigger
    /// - so uppercasing an embedding whose Entry is <i>not</i> canonical would break a join that
    /// currently works, in the name of fixing one that does not. The repair therefore only moves a
    /// reference whose canonical target already exists, and this case is what proves it.
    ///
    /// <para>This pair is the one the pre-conversion writers would have left behind: the transfer store
    /// spelled an imported Entry's identity with a bare <c>ToString()</c>, and the weaving service then
    /// copied that spelling into the embedding, so the two agreed in the minority form. Leaving them
    /// agreed is the correct outcome, not a limitation.</para>
    /// </remarks>
    [Fact]
    public async Task An_embedding_whose_entry_is_not_canonical_is_left_paired_rather_than_half_moved()
    {

        await using IdentitySpellingUpgradeHarness harness = await IdentitySpellingUpgradeHarness.StartAsync();

        await harness.SeedSessionAsync(SessionIdentity, CampaignIdentity, campaignCanonical: true);

        await harness.SeedLegacyEntryAsync(EntryIdentity, SessionIdentity);

        await harness.SeedEmbeddingAsync(Legacy(EntryIdentity));

        Assert.Equal(1, await harness.EmbeddedEntryCountAsync());

        await harness.UpgradeAsync();

        Assert.Equal(Legacy(EntryIdentity), await harness.ScalarStringAsync("SELECT EntryId FROM entry_embeddings"));

        Assert.Equal(1, await harness.EmbeddedEntryCountAsync());

    }

    /// <summary>
    /// The case every real installation takes. Nothing is seeded in the minority form, so the sweep
    /// counts zero for every column it verifies, rewrites nothing, and the tier reaches head anyway.
    /// </summary>
    [Fact]
    public async Task A_canonical_installation_reaches_head_and_rewrites_nothing()
    {

        await using IdentitySpellingUpgradeHarness harness = await IdentitySpellingUpgradeHarness.StartAsync();

        await harness.SeedSessionAsync(SessionIdentity, CampaignIdentity, campaignCanonical: true);

        await harness.SeedEntryAsync(EntryIdentity, SessionIdentity);

        await harness.SeedEmbeddingAsync(Canonical(EntryIdentity));

        await harness.UpgradeAsync();

        Assert.Equal(0L, await harness.NonCanonicalRowCountAsync());

        Assert.Equal(Canonical(EntryIdentity), await harness.ScalarStringAsync("SELECT EntryId FROM entry_embeddings"));

        Assert.Equal(
            Canonical(CampaignIdentity),
            await harness.ScalarStringAsync("SELECT \"CampaignId\" FROM \"Sessions\""));

    }

    /// <summary>
    /// The closed statement of what the step answers for - which columns it counts and which references
    /// it may move - pinned by name and proved against the live schema.
    /// </summary>
    /// <remarks>
    /// Without this a sweep that quietly stopped covering a column would keep every other case green:
    /// each of them asserts that nothing non-canonical remains, and a column nobody looks at contributes
    /// nothing to that total. The repaired references need the same pin for the same reason - every other
    /// case asserts that one <i>particular</i> pairing was restored, so a third reference added without a
    /// case of its own would be covered by nothing. Running each declared name against a freshly
    /// installed schema is what turns a rename or a misspelling into a failure here rather than into a
    /// column that silently counts nothing forever.
    /// </remarks>
    [Fact]
    public async Task The_verifier_answers_for_exactly_the_declared_identity_columns()
    {

        Assert.Equal(
            [
                ("Sessions", "Id"),
                ("Sessions", "CampaignId"),
                ("Campaigns", "Id"),
                ("Entries", "Id"),
                ("Entries", "SessionId"),
                ("entry_embeddings", "EntryId"),
                ("assistant_entry_finalizations", "AssistantEntryId"),
                ("assistant_entry_finalizations", "SessionId"),
                ("session_sensitivity_state", "SessionId"),
            ],
            IdentitySpellingBackfill.VerifiedColumns);

        Assert.Equal(
            [
                ("Sessions", "CampaignId", "Campaigns", "Id"),
                ("entry_embeddings", "EntryId", "Entries", "Id"),
            ],
            IdentitySpellingBackfill.RepairedReferences.Select(
                static reference =>
                    (reference.Table, reference.Column, reference.TargetTable, reference.TargetColumn)));

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

        foreach ((string table, string column) in IdentitySpellingBackfill.VerifiedColumns)
        {

            Assert.Equal(0L, await CountAsync(connection, table, column));

        }

    }

    /// <summary>
    /// Canonical is uppercase <i>and</i> dashed <i>and</i> 36 characters, and each of those three is a
    /// separate way to be wrong.
    /// </summary>
    /// <remarks>
    /// This case exists because the rest of the suite cannot reach two of the three. Every identity it
    /// seeds is a 36-character dashed Guid in one case or the other, so only the case arm of the
    /// predicate is ever exercised and the length and dash arms could be deleted with the whole suite
    /// staying green - which is this defect family's own trap, reproduced inside the thing built to catch
    /// it.
    ///
    /// <para>The dash-free value is the one that matters. <c>Guid.ToString("N")</c> renders 32 uppercase
    /// hex characters, which is already its own <c>upper()</c> image, so a case-only check passes it in
    /// silence. That form is not hypothetical: two columns in this schema legitimately hold it, and the
    /// argument for excluding them from this step rests on the predicate being able to tell the
    /// difference.</para>
    /// </remarks>
    [Fact]
    public async Task A_dash_free_or_short_identity_is_counted_although_it_is_already_uppercase()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

        string dashFree = EntryIdentity.ToString("N").ToUpperInvariant();

        Assert.Equal(dashFree, dashFree.ToUpperInvariant());

        await InsertEmbeddingAsync(connection, dashFree);

        Assert.Equal(1L, await CountAsync(connection, "entry_embeddings", "EntryId"));

        await InsertEmbeddingAsync(connection, Canonical(EntryIdentity)[..8]);

        Assert.Equal(2L, await CountAsync(connection, "entry_embeddings", "EntryId"));

        await InsertEmbeddingAsync(connection, Canonical(SessionIdentity));

        Assert.Equal(2L, await CountAsync(connection, "entry_embeddings", "EntryId"));

    }

    /// <summary>Asks the sweep's own question of one column, so no test carries a second copy of it.</summary>
    private static Task<long> CountAsync(SqliteConnection connection, string table, string column) =>
        IdentitySpellingBackfill.CountNonCanonicalAsync(
            connection,
            transaction: null,
            table,
            column,
            CancellationToken.None);

    /// <summary>
    /// An embedding row carrying an arbitrary identity, written directly because no writer can produce
    /// the spellings this case needs.
    /// </summary>
    private static async Task InsertEmbeddingAsync(SqliteConnection connection, string entryId)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "INSERT INTO entry_embeddings (EntryId, Embedding, Dim) VALUES ($id, zeroblob(8), 2);";

        _ = command.Parameters.AddWithValue("$id", entryId);

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    /// <summary>The canonical spelling: uppercase, dashed, 36 characters, as the provider renders it.</summary>
    private static string Canonical(Guid identity) => identity.ToString("D").ToUpperInvariant();

    /// <summary>
    /// The minority spelling, rendered the way the writers that produced it rendered it: a bare
    /// <c>ToString()</c>, which is lowercase dashed.
    /// </summary>
    private static string Legacy(Guid identity) => identity.ToString("D").ToLowerInvariant();

    /// <summary>
    /// Every stored definition version 5 touches, evolved and fresh.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> GuardDefinitionsAsync(bool evolve)
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        if (evolve)
        {

            GrimoireSchemaInstallResult seed = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                CoreSchemaVersionFourFixture.ChainSet(),
                1536,
                CancellationToken.None);

            // If the fixture ever regressed to producing a version-5 tree, the "evolved" arm would
            // install nothing further and the comparison against "fresh" would pass vacuously.
            Assert.Equal(4, seed.Core.SchemaVersion);

        }

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            GrimoireSchemaVersionChains.Default,
            1536,
            CancellationToken.None);

        Dictionary<string, string> definitions = new(StringComparer.Ordinal);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT name, sql FROM sqlite_master WHERE name LIKE 'assistant_entry_finalizations%';";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {

            if (!reader.IsDBNull(1))
            {

                definitions[reader.GetString(0)] = reader.GetString(1);

            }

        }

        return definitions;

    }

    /// <summary>
    /// One open scratch installation that starts at version 4 and is upgraded through the shipped chain.
    /// </summary>
    /// <remarks>
    /// Every row this seeds is one no writer can produce any more, which is exactly what a migration
    /// test is for: the two writers that once rendered the minority form were converted before this step
    /// was authored. What the suite <i>asserts</i> is produced entirely by production code from that
    /// legacy state - the shipped installer, the shipped coordinator, and the shipped sweep.
    /// </remarks>
    private sealed class IdentitySpellingUpgradeHarness : IAsyncDisposable
    {

        private const string Timestamp = "2026-01-01T00:00:00.0000000+00:00";

        private readonly EvolutionScratchDatabase _file;

        private readonly SqliteConnection _connection;

        private readonly ServiceProvider _services;

        private readonly GrimoireSchemaTransitionCoordinator _coordinator;

        private IdentitySpellingUpgradeHarness(
            EvolutionScratchDatabase file,
            SqliteConnection connection,
            ServiceProvider services,
            GrimoireSchemaTransitionCoordinator coordinator)
        {

            _file = file;

            _connection = connection;

            _services = services;

            _coordinator = coordinator;

        }

        internal static async Task<IdentitySpellingUpgradeHarness> StartAsync()
        {

            EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                CoreSchemaVersionFourFixture.ChainSet(),
                1536,
                CancellationToken.None);

            Assert.Equal(GrimoireSchemaTierHealth.Healthy, installed.Core.Health);

            Assert.Equal(4, installed.Core.SchemaVersion);

            ServiceCollection collection = new();

            _ = collection.AddOptions();

            _ = collection.Configure<ArcanumSettings>(static _ => { });

            ServiceProvider services = collection.BuildServiceProvider();

            GrimoireSchemaInstaller installer =
                GrimoireSchemaTestInstaller.Create(GrimoireSchemaVersionChains.Default);

            GrimoireSchemaTransitionCoordinator coordinator = new(
                new FixedCoreConnectionSource(connection),
                GrimoireSchemaVersionChains.Default,
                installer,
                new GrimoireSchemaBackfillRunner(installer, TimeProvider.System),
                services,
                new CovenantAvailability(new CovenantRuntimeGenerationProvider()),
                TimeProvider.System);

            return new IdentitySpellingUpgradeHarness(file, connection, services, coordinator);

        }

        /// <summary>
        /// A Campaign and the Session that names it. The Campaign identity is always canonical, because
        /// the object-relational writer is the only thing that has ever written one; the Session's
        /// reference to it is the half that varies.
        /// </summary>
        internal async Task SeedSessionAsync(Guid session, Guid campaign, bool campaignCanonical)
        {

            await ExecuteAsync(
                """
                INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
                VALUES ($id, 'Alpha', 'alpha', '/campaigns/alpha', 0, '{}', $now, $now);
                """,
                ("$id", Canonical(campaign)),
                ("$now", Timestamp));

            await ExecuteAsync(
                """
                INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
                VALUES ($id, $campaign, 'active', $now, $now);
                """,
                ("$id", Canonical(session)),
                ("$campaign", campaignCanonical ? Canonical(campaign) : Legacy(campaign)),
                ("$now", Timestamp));

        }

        /// <summary>An Entry spelled the way every installation holds one.</summary>
        internal Task SeedEntryAsync(Guid entry, Guid session) =>
            InsertEntryAsync(Canonical(entry), Canonical(session));

        /// <summary>
        /// An Entry spelled the minority way, which only a hand edit can produce - the state the repair
        /// arm must decline to half-move.
        /// </summary>
        internal Task SeedLegacyEntryAsync(Guid entry, Guid session) =>
            InsertEntryAsync(Legacy(entry), Canonical(session));

        internal Task SeedEmbeddingAsync(string entryId) =>
            ExecuteAsync(
                "INSERT INTO entry_embeddings (EntryId, Embedding, Dim) VALUES ($id, zeroblob(8), 2);",
                ("$id", entryId));

        /// <summary>
        /// How many Entries the weaving service would see as embedded, expressed the way it asks: an
        /// exact join between the two columns.
        /// </summary>
        internal Task<int> EmbeddedEntryCountAsync() =>
            ScalarAsync(
                """
                SELECT COUNT(*) FROM "Entries" AS entry
                JOIN entry_embeddings AS embedding ON embedding.EntryId = entry."Id";
                """);

        internal Task<int> SessionsInCampaignAsync(string campaignId) =>
            ScalarAsync($"SELECT COUNT(*) FROM \"Sessions\" WHERE \"CampaignId\" = '{campaignId}';");

        /// <summary>
        /// Every identity column the step verifies, counted as one number by the sweep's own predicate.
        /// </summary>
        /// <remarks>
        /// The question is asked through <see cref="IdentitySpellingBackfill"/> rather than restated
        /// here, so a verifier that quietly stopped covering a column cannot be certified by a test
        /// carrying its own second copy of what canonical means.
        /// </remarks>
        internal async Task<long> NonCanonicalRowCountAsync()
        {

            long total = 0;

            foreach ((string table, string column) in IdentitySpellingBackfill.VerifiedColumns)
            {

                total += await IdentitySpellingBackfill.CountNonCanonicalAsync(
                    _connection,
                    transaction: null,
                    table,
                    column,
                    CancellationToken.None);

            }

            return total;

        }

        internal async Task UpgradeAsync()
        {

            _ = await GrimoireSchemaTestInstaller.InstallAsync(
                _connection,
                GrimoireSchemaVersionChains.Default,
                1536,
                CancellationToken.None);

            while ((await _coordinator.RunOnceAsync(CancellationToken.None)).Value.Advanced)
            {

            }

            Assert.Equal(
                GrimoireSchemaVersionChains.CoreSchemaVersion,
                await ScalarAsync(
                    "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE TransactionTierCode = 0;"));

        }

        internal async Task<string?> ScalarStringAsync(string sql)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = sql;

            object? value = await command.ExecuteScalarAsync(CancellationToken.None);

            return value is DBNull or null ? null : (string)value;

        }

        public async ValueTask DisposeAsync()
        {

            await _services.DisposeAsync();

            await _connection.DisposeAsync();

            _file.Dispose();

        }

        private Task InsertEntryAsync(string entryId, string sessionId) =>
            ExecuteAsync(
                """
                INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
                VALUES ($id, $session, 1, 'a turn', 'test', $now, 1);
                """,
                ("$id", entryId),
                ("$session", sessionId),
                ("$now", Timestamp));

        private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = sql;

            foreach ((string name, object? value) in parameters)
            {

                _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);

            }

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }

        private async Task<int> ScalarAsync(string sql)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = sql;

            return Convert.ToInt32(
                await command.ExecuteScalarAsync(CancellationToken.None),
                CultureInfo.InvariantCulture);

        }

    }

    /// <summary>The one open connection the harness has, which a request scope would otherwise supply.</summary>
    private sealed class FixedCoreConnectionSource(SqliteConnection connection) : ICovenantConnectionSource
    {

        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(connection);

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(connection);

    }

}
