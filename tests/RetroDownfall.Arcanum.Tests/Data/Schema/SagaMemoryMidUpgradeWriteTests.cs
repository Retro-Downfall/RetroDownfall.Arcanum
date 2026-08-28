using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// A Saga memory written on an installation that has taken version 5's DDL and has not yet drained its
/// sweep.
/// </summary>
/// <remarks>
/// <b>This window is real and it is not short.</b> <c>GrimoireSchemaInstaller.RunStepsAsync</c> applies a
/// step's statements, and when the step declares a backfill it commits that DDL with the journal row and
/// returns <c>Incomplete</c>; the sweep runs afterwards, in the transition coordinator's later passes.
/// So every version-5 guard is durably installed and enforcing while
/// <c>session_campaign_bindings.CampaignId</c> still holds whatever an upgrading installation wrote
/// before it, and the drain can take many batches on a store with attachments in it.
///
/// <para>Every other column version 5 guards has writers that <i>render</i> their value, and those were
/// converted. <c>saga_memories.CampaignId</c> is the exception: its writer <i>copies</i> a value out of
/// a column the same step has not repaired yet. Handed on verbatim, that copy aborted the insert on
/// version 5's own guard - so on an upgrading installation no Saga memory could be written for any
/// Session the sweep had not reached, on any turn, until the drain finished.
/// <see cref="SagaMemoryScopeClassifier"/> now canonicalizes the identity it hands on, which makes the
/// write independent of how far the sweep has got rather than merely shortening the window.</para>
///
/// <para>The state is built the way an upgrade produces it, not asserted into place: install version 4,
/// write a binding in the spelling the turn-begin repository used to render, then hand the installer the
/// shipped chain <i>once</i>. That single call is what leaves the DDL committed and the sweep pending,
/// and the case asserts that state before it writes anything - a test that ran after the drain would
/// prove nothing at all about this.</para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SagaMemoryMidUpgradeWriteTests
{

    /// <summary>The floor <c>ArcanumSettingClamps.EmbeddingsDimensions</c> raises anything smaller to.</summary>
    private const int TestDimensions = 64;

    private static readonly Guid CampaignIdentity = new("A0000000-0000-4000-8000-0000000000C5");

    /// <summary>The Session whose binding the sweep has not reached: the minority spelling.</summary>
    private static readonly Guid SessionIdentity = new("B0000000-0000-4000-8000-0000000000E5");

    /// <summary>A Session in the same Campaign whose binding the core data initializer canonicalized.</summary>
    private static readonly Guid CanonicalSessionIdentity = new("B0000000-0000-4000-8000-0000000000E6");

    private static readonly string Timestamp =
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture);

    static SagaMemoryMidUpgradeWriteTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task A_memory_written_before_the_sweep_drains_succeeds_and_records_the_canonical_campaign()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionFourFixture.ChainSet(),
            TestDimensions,
            CancellationToken.None);

        Assert.Equal(4, installed.Core.SchemaVersion);

        await SeedBoundSessionAsync(connection);

        // One call, which is what leaves the version-5 DDL committed and the sweep still pending.
        GrimoireSchemaInstallResult upgraded = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            GrimoireSchemaVersionChains.Default,
            TestDimensions,
            CancellationToken.None);

        Assert.Equal(4, upgraded.Core.SchemaVersion);

        // The state the case exists for, asserted rather than assumed: the guard is installed and the
        // binding it will judge a copy of still holds the minority spelling.
        Assert.Equal(
            "saga_memories_CampaignId_guard_identity_insert",
            await ScalarStringAsync(
                connection,
                """
                SELECT name FROM sqlite_master
                WHERE type = 'trigger' AND name = 'saga_memories_CampaignId_guard_identity_insert';
                """));

        Assert.Equal(
            CampaignIdentity.ToString("D").ToLowerInvariant(),
            await ScalarStringAsync(
                connection,
                $"SELECT CampaignId FROM session_campaign_bindings WHERE SessionId = '{Canonical(SessionIdentity)}';"));

        string memoryId = Guid.NewGuid().ToString();

        await using ArcanumDbContext db = CreateContext(file);

        SagaMemoryStore store = CreateStore(db);

        SagaMemoryWriteOutcome outcome = await store.InsertAsync(
            memoryId,
            "a conclusion drawn mid-upgrade",
            DateTimeOffset.UtcNow,
            SessionIdentity,
            tags: null,
            source: "test",
            new float[TestDimensions],
            CancellationToken.None);

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);

        Assert.Equal(
            CampaignIdentity.ToString("D").ToUpperInvariant(),
            await ScalarStringAsync(connection, """SELECT CampaignId FROM saga_memories;"""));

        Assert.Equal(
            0L,
            await IdentitySpellingBackfill.CountNonCanonicalAsync(
                connection,
                transaction: null,
                "saga_memories",
                "CampaignId",
                CancellationToken.None));

    }

    /// <summary>
    /// Reinstating a memory mid-drain releases the suppression its identical twin recorded on the other
    /// half of the installation, so the content really is writable again.
    /// </summary>
    /// <remarks>
    /// <b>Sharing a function is not sharing a value, and the first attempt at this shared only the
    /// function.</b> A suppression digest takes the Campaign identity into its preimage, and the two ends
    /// of the digest read that identity from different places: the write path from the classifier, which
    /// canonicalizes, and the release from the memory row, which the version-5 sweep may not have
    /// reached. Handed the row's minority spelling, the pair collapsed to one digest asked for twice - so
    /// a release removed only the record written on its own half, returned <c>Applied</c>, and the next
    /// write of that content was still refused with nothing reporting why.
    ///
    /// <para><b>One row is seeded and everything else is driven.</b> Two Sessions in one Campaign, bound
    /// the two ways an upgrading installation is bound, each carrying a memory with identical content,
    /// each retired through <c>RetireAsync</c> - which is what puts one suppression under each spelling.
    /// The memory on the un-swept half is the seeded one, because no build since the classifier began
    /// canonicalizing can write a row holding the minority spelling; that row is exactly what the sweep
    /// exists to repair, so a case about behaviour before the sweep has to start from one.</para>
    ///
    /// <para><b>What it counts is the release, not the row.</b> It writes the content back through the
    /// store afterwards and demands <see cref="SagaMemoryWriteOutcome.Written"/>, because asserting that
    /// a suppression row disappeared would pass a release that deleted the wrong one.</para>
    /// </remarks>
    [Fact]
    public async Task A_reinstate_before_the_sweep_drains_releases_the_suppression_its_twin_recorded()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionFourFixture.ChainSet(),
            TestDimensions,
            CancellationToken.None);

        await SeedBoundSessionAsync(connection);

        await using ArcanumDbContext db = CreateContext(file);

        SagaMemoryStore store = CreateStore(db);

        const string Content = "a conclusion both halves of the installation drew";

        byte[] contentDigest = AnnalContentDigest.ForSagaMemory(Content);

        string onTheCanonicalHalf = await WriteAsync(store, CanonicalSessionIdentity, Content);

        // The un-swept half's memory is seeded rather than written, and it is the one thing here that
        // has to be: it stands for a row the shipped code wrote before the classifier canonicalized what
        // it hands on, which is every Campaign-scoped memory on every installation this upgrade will run
        // on. No build after that change can produce it, which is the point - the row is what the sweep
        // exists to repair, and what the release has to keep working against until it has.
        string onTheUnsweptHalf = Guid.NewGuid().ToString();

        await ExecuteAsync(
            connection,
            """
            INSERT INTO saga_memories ("Id", "Content", "CreatedAt", "SessionId", ScopeKindCode, CampaignId)
            VALUES ($id, $content, $now, $session, 2, $campaign);
            """,
            ("$id", onTheUnsweptHalf),
            ("$content", Content),
            ("$session", SessionIdentity.ToString("D")),
            ("$campaign", CampaignIdentity.ToString("D").ToLowerInvariant()),
            ("$now", Timestamp));

        // One Campaign under two spellings, one on each half - which is what makes the two retirements
        // below record two different digests for what is one rejection.
        Assert.Equal(
            Canonical(CampaignIdentity),
            await ScalarStringAsync(
                connection,
                $"""SELECT CampaignId FROM saga_memories WHERE "Id" = '{onTheCanonicalHalf}';"""));

        Assert.Equal(
            CampaignIdentity.ToString("D").ToLowerInvariant(),
            await ScalarStringAsync(
                connection,
                $"""SELECT CampaignId FROM saga_memories WHERE "Id" = '{onTheUnsweptHalf}';"""));

        foreach (string retiring in new[] { onTheCanonicalHalf, onTheUnsweptHalf })
        {

            SagaCurationOutcome retired = await store.RetireAsync(
                retiring,
                contentDigest,
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            Assert.Equal(SagaCurationOutcomeKind.Applied, retired.Kind);

        }

        Assert.Equal(2, await CountAsync(connection, "SELECT COUNT(*) FROM saga_retirement_suppressions;"));

        // One call, which is what leaves the version-5 DDL committed and the sweep still pending.
        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            GrimoireSchemaVersionChains.Default,
            TestDimensions,
            CancellationToken.None);

        Assert.Equal(
            CampaignIdentity.ToString("D").ToLowerInvariant(),
            await ScalarStringAsync(
                connection,
                $"""SELECT CampaignId FROM saga_memories WHERE "Id" = '{onTheUnsweptHalf}';"""));

        SagaCurationOutcome reinstated = await store.ReinstateAsync(
            onTheUnsweptHalf,
            contentDigest,
            new float[TestDimensions],
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(SagaCurationOutcomeKind.Applied, reinstated.Kind);

        SagaMemoryWriteOutcome rewritten = await store.InsertAsync(
            Guid.NewGuid().ToString(),
            Content,
            DateTimeOffset.UtcNow,
            SessionIdentity,
            tags: null,
            source: "test",
            new float[TestDimensions],
            CancellationToken.None);

        Assert.Equal(SagaMemoryWriteOutcome.Written, rewritten);

    }

    private static async Task<string> WriteAsync(SagaMemoryStore store, Guid sessionId, string content)
    {

        string id = Guid.NewGuid().ToString();

        SagaMemoryWriteOutcome outcome = await store.InsertAsync(
            id,
            content,
            DateTimeOffset.UtcNow,
            sessionId,
            tags: null,
            source: "test",
            new float[TestDimensions],
            CancellationToken.None);

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);

        return id;

    }

    private static SagaMemoryStore CreateStore(ArcanumDbContext db) =>
        new(
            db,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings { Dimensions = TestDimensions },
                    },
                }));

    private static async Task<int> CountAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);

    }

    /// <summary>
    /// A Campaign, a Session, and the binding between them in the spelling the turn-begin repository
    /// rendered before this work - which is what every Session created on an upgrading installation
    /// carries.
    /// </summary>
    private static async Task SeedBoundSessionAsync(SqliteConnection connection)
    {

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($campaign, 'Alpha', 'alpha', '/campaigns/alpha', 0, '{}', $now, $now);
            """,
            ("$campaign", Canonical(CampaignIdentity)),
            ("$now", Timestamp));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
            VALUES ($session, $campaign, 'active', $now, $now);
            """,
            ("$session", Canonical(SessionIdentity)),
            ("$campaign", Canonical(CampaignIdentity)),
            ("$now", Timestamp));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
            VALUES ($session, $campaign, 'active', $now, $now);
            """,
            ("$session", Canonical(CanonicalSessionIdentity)),
            ("$campaign", Canonical(CampaignIdentity)),
            ("$now", Timestamp));

        using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance
            .Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
            VALUES ($session, 2, $campaign, $now);
            """,
            ("$session", Canonical(SessionIdentity)),
            // The minority spelling, on purpose and legally: at version 4 no guard refuses it, and it is
            // exactly what the unconverted writer left on every installation this upgrade will run on.
            ("$campaign", CampaignIdentity.ToString("D").ToLowerInvariant()),
            ("$now", Timestamp));

        // The other half of the same installation: a Session the core data initializer bound, which
        // canonicalized what it backfilled. One Campaign, two spellings, which is the state every
        // upgrading installation is in.
        await ExecuteAsync(
            connection,
            """
            INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
            VALUES ($session, 2, $campaign, $now);
            """,
            ("$session", Canonical(CanonicalSessionIdentity)),
            ("$campaign", Canonical(CampaignIdentity)),
            ("$now", Timestamp));

    }

    /// <summary>
    /// An object-relational context over the same scratch file the installer just wrote, because
    /// <see cref="SagaMemoryStore"/> takes one and the point of the case is to drive the real store.
    /// </summary>
    private static ArcanumDbContext CreateContext(EvolutionScratchDatabase file)
    {

        DbContextOptions<ArcanumDbContext> options = new DbContextOptionsBuilder<ArcanumDbContext>()
            .UseSqlite(file.ConnectionString)
            .UseModel(RetroDownfall.Arcanum.Infrastructure.Generated.ArcanumDbContextModel.Instance)
            .Options;

        return new ArcanumDbContext(
            options,
            new MidUpgradeSecretStore(),
            new MidUpgradePassphraseSource());

    }

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(CancellationToken.None);

        return value is DBNull or null ? null : (string)value;

    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters)
        {

            _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        }

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static string Canonical(Guid identity) => identity.ToString("D").ToUpperInvariant();

    /// <summary>
    /// The two collaborators <see cref="ArcanumDbContext"/> takes, neither of which this case reaches:
    /// the scratch database the evolution fixtures use is not encrypted, so nothing asks for a key.
    /// </summary>
    private sealed class MidUpgradePassphraseSource : IGrimoireDbPassphraseSource
    {

        public string Passphrase =>
            throw new InvalidOperationException(
                "The evolution scratch database is unencrypted; nothing should ask it for a passphrase.");

        public void SetPassphrase(string passphrase) =>
            throw new InvalidOperationException(
                "The evolution scratch database is unencrypted; nothing should key it.");

    }

    private sealed class MidUpgradeSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

}
