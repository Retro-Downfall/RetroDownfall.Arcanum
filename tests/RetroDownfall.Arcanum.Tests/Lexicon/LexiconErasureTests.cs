using System.Data.Common;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit.Sdk;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

/// <summary>
/// The Lexicon erase verbs on the service itself: the full-text verdict, the fingerprint's exact scope,
/// the refusals that keep a target honest, and the commit rules that keep a result honest.
/// </summary>
/// <remarks>
/// <para>Every entry is written through the service's own scribe, correction, retirement and pin, and
/// every fingerprint comes from an actual erase. The service is composed the way the host composes it,
/// with its erasure dependencies built over the fixture database and an isolated keyring.</para>
///
/// <para>Every test that erases ends by asking the database whether any Annals claim outlived its
/// row.</para>
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconErasureTests(GrimoireFixture fixture)
{
    private const string Sentinel = "xqzsentinelerase";

    private const string LiveSentinel = "yqzlivesentinel";

    private const string DaemonStateRefusal =
        "Unseen Servant daemon_state entries are managed by their daemon job and cannot be erased.";

    private static readonly LexiconCurationScope Global = new(LexiconScopeKind.Global, null);

    private static CancellationToken Token => CancellationToken.None;

    /// <summary>
    /// The end-to-end Lexicon proof: an entry corrected and retired by a version-12 build left its
    /// earliest fact in the index's segments, and after the upgrade and the erase no token of it is
    /// left, and the name cannot be scribed again in that scope.
    /// </summary>
    /// <remarks>
    /// The sentinel shares no first letter with any other token, so FTS5 prefix compression cannot
    /// hide it from the probe, and the probe is shown live before the upgrade runs. The re-scribe uses
    /// the lower-case name with spaces around it on purpose: it is the one assertion that ties the scribe
    /// chokepoint's trimming to the identity the erase recorded.
    /// </remarks>
    [SkippableFact]
    public async Task An_entry_corrected_and_retired_before_version_thirteen_leaves_no_token_in_lexicon_fts_data()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(Token);

        await InstallAsync(connection, CoreSchemaVersionTwelveFixture.ChainSet(), 12);

        // This build's initializer already enabled it, which no version-12 build ever did.
        await ExecuteAsync(connection, "INSERT INTO lexicon_fts(lexicon_fts, rank) VALUES('secure-delete', 0);");

        await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        CovenantSqliteConnectionInitializer.Instance.EnsureAuthorizationFunctions((SqliteConnection)db.Database.GetDbConnection());

        using ErasureHarness harness = ErasureHarness.Create(db);

        LexiconService lexicon = harness.Service;

        Result<LexiconEntryDto> scribed = await lexicon.UpsertAsync("Mill Warden", "Place", [$"{Sentinel} guards the mill"], LexiconScope.Global, Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);

        Result<LexiconCurationResult> corrected = await lexicon.CorrectAsync(
            (await ShowAsync(lexicon, Global, "Mill Warden")).Target,
            new("Place", ["guards the gate"]),
            null,
            Token);

        Assert.True(corrected.IsSuccess, corrected.IsFailure ? corrected.Error.Message : null);

        Result<LexiconCurationResult> retired = await lexicon.RetireAsync(corrected.Value.Entry.Target, null, Token);

        Assert.True(retired.IsSuccess, retired.IsFailure ? retired.Error.Message : null);

        Assert.True(await SentinelBlocksAsync(connection) > 0);

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, 13);

        (_, MemoryErasureResultDto result) = await harness.EraseAsync((await ShowAsync(lexicon, Global, "Mill Warden")).Target);

        Assert.Equal(0, await SentinelBlocksAsync(connection));

        Assert.Equal(MemoryLocalErasureOutcome.Verified, result.Local.Outcome);

        Result<LexiconEntryDto> scribe = await lexicon.UpsertAsync("  mill warden ", null, ["returns"], LexiconScope.Global, Token);

        Assert.Equal(ErrorCodes.Lexicon.SuppressedNameRefused, scribe.Error.Code);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(connection);
    }

    /// <summary>
    /// An index whose secure delete was switched off still holds a corrected fact's tokens. The erase
    /// turns it back on, merges the residue away, reads the setting back, and only then deletes.
    /// </summary>
    /// <remarks>
    /// Two sentinels, each sharing no first letter with any other token: one in the fact the correction
    /// superseded, which only the merge can clear, and one in the live fact, which only the erase's own
    /// delete can clear, and only while secure delete is on.
    /// </remarks>
    [SkippableFact]
    public async Task With_secure_delete_off_at_start_the_erase_enables_it_merges_and_then_deletes()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        using ErasureHarness harness = ErasureHarness.Create(db);

        await ExecuteAsync(harness.Connection, "INSERT INTO lexicon_fts(lexicon_fts, rank) VALUES('secure-delete', 0);");

        LexiconService lexicon = harness.Service;

        Result<LexiconEntryDto> scribed = await lexicon.UpsertAsync("Mill Warden", "Place", [$"{Sentinel} guards the mill"], LexiconScope.Global, Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);

        Result<LexiconCurationResult> corrected = await lexicon.CorrectAsync(
            (await ShowAsync(lexicon, Global, "Mill Warden")).Target,
            new("Place", [$"{LiveSentinel} guards the gate"]),
            null,
            Token);

        Assert.True(corrected.IsSuccess, corrected.IsFailure ? corrected.Error.Message : null);

        Assert.True(await SentinelBlocksAsync(harness.Connection) > 0);

        Assert.True(await SentinelBlocksAsync(harness.Connection, LiveSentinel) > 0);

        (_, MemoryErasureResultDto result) = await harness.EraseAsync(corrected.Value.Entry.Target);

        Assert.Equal(0, await SentinelBlocksAsync(harness.Connection));

        Assert.Equal(0, await SentinelBlocksAsync(harness.Connection, LiveSentinel));

        Assert.Equal(1L, await ScalarAsync(harness.Connection, "SELECT v FROM lexicon_fts_config WHERE k = 'secure-delete';"));

        Assert.DoesNotContain(MemoryErasureScrubPendingReason.FullTextSecureDeleteUnverified, result.Local.PendingReasons);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection);
    }

    /// <summary>
    /// A fingerprint names one name in one exact scope: after an erase in Campaign A the same name is
    /// still recorded in Global and in Campaign B, and refused in Campaign A alone.
    /// </summary>
    [SkippableFact]
    public async Task A_scribe_of_an_erased_name_is_refused_in_that_scope_only()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        using ErasureHarness harness = ErasureHarness.Create(db);

        LexiconService lexicon = harness.Service;

        Guid campaignA = Guid.NewGuid();

        Guid campaignB = Guid.NewGuid();

        Result<LexiconEntryDto> scribed = await lexicon.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.ForCampaign(campaignA), Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);

        _ = await harness.EraseAsync((await ShowAsync(lexicon, new(LexiconScopeKind.Campaign, campaignA), "Entity")).Target);

        Result<LexiconEntryDto> global = await lexicon.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.Global, Token);

        Assert.True(global.IsSuccess, global.IsFailure ? global.Error.Message : null);

        Result<LexiconEntryDto> otherCampaign = await lexicon.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.ForCampaign(campaignB), Token);

        Assert.True(otherCampaign.IsSuccess, otherCampaign.IsFailure ? otherCampaign.Error.Message : null);

        Result<LexiconEntryDto> erased = await lexicon.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.ForCampaign(campaignA), Token);

        Assert.Equal(ErrorCodes.Lexicon.SuppressedNameRefused, erased.Error.Code);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection);
    }

    [SkippableFact]
    public async Task A_daemon_state_entry_is_refused_by_name()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        using ErasureHarness harness = ErasureHarness.Create(db);

        LexiconService lexicon = harness.Service;

        Result<LexiconEntryDto> scribed = await lexicon.UpsertAsync("daemon_state:job:abc", "DaemonState", ["the trend rose"], LexiconScope.Global, Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);

        LexiconCurationTarget target = (await ShowAsync(lexicon, Global, "daemon_state:job:abc")).Target;

        Result<MemoryErasurePreflightDto> prepared = await lexicon.PrepareAsync(new(target, Guid.NewGuid()), Token);

        Assert.Equal(ErrorCodes.Lexicon.InvalidName, prepared.Error.Code);

        Assert.Equal(DaemonStateRefusal, prepared.Error.Message);

        Result<MemoryErasureResultDto> applied = await lexicon.ApplyAsync(new(target, Guid.NewGuid(), "x"), harness.OperatorContext, Token);

        Assert.Equal(ErrorCodes.Lexicon.InvalidName, applied.Error.Code);

        Assert.Equal(DaemonStateRefusal, applied.Error.Message);

        Assert.Equal(target, (await ShowAsync(lexicon, Global, "daemon_state:job:abc")).Target);

        Assert.Equal(0L, await FingerprintCountAsync(harness.Connection));
    }

    /// <summary>
    /// The one recognizer the erase and the agent's delete share: the <c>daemon_state:</c> prefix in any
    /// case, and nothing that merely contains it.
    /// </summary>
    [Theory]
    [InlineData("daemon_state:job:abc", true)]
    [InlineData("DAEMON_STATE:JOB:ABC", true)]
    [InlineData("Daemon_State:", true)]
    [InlineData("daemon_state", false)]
    [InlineData("the daemon_state:job", false)]
    [InlineData("daemon-state:job", false)]
    public void Daemon_state_names_are_recognized_by_their_prefix_in_any_case(string name, bool expected) =>
        Assert.Equal(expected, LexiconDaemonStateNames.Is(name));

    /// <summary>
    /// A pin between prepare and apply changes the curation target the erase was planned against, so
    /// apply refuses it as stale and removes nothing.
    /// </summary>
    [SkippableFact]
    public async Task A_curation_change_between_prepare_and_apply_is_a_stale_target()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        using ErasureHarness harness = ErasureHarness.Create(db);

        LexiconService lexicon = harness.Service;

        Result<LexiconEntryDto> scribed = await lexicon.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.Global, Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);

        LexiconCurationTarget target = (await ShowAsync(lexicon, Global, "Entity")).Target;

        Guid mutationId = Guid.NewGuid();

        Result<MemoryErasurePreflightDto> prepared = await lexicon.PrepareAsync(new(target, mutationId), Token);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        Result<LexiconCurationResult> pinned = await lexicon.PinAsync(target, null, Token);

        Assert.True(pinned.IsSuccess, pinned.IsFailure ? pinned.Error.Message : null);

        Result<MemoryErasureResultDto> applied = await lexicon.ApplyAsync(
            new(target, mutationId, prepared.Value.PreflightToken),
            harness.OperatorContext,
            Token);

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, applied.Error.Code);

        Assert.Equal(pinned.Value.Entry.Target, (await ShowAsync(lexicon, Global, "Entity")).Target);

        Assert.Equal(0L, await FingerprintCountAsync(harness.Connection));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection);
    }

    [SkippableFact]
    public async Task Without_erasure_dependencies_the_verbs_are_unavailable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated();

        LexiconService lexicon = new(
            db,
            new TestCapturingLogger<LexiconService>(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { Annals = true } }),
            keys);

        Result<LexiconEntryDto> scribed = await lexicon.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.Global, Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);

        LexiconCurationTarget target = (await ShowAsync(lexicon, Global, "Entity")).Target;

        Result<MemoryErasurePreflightDto> prepared = await lexicon.PrepareAsync(new(target, Guid.NewGuid()), Token);

        Assert.Equal(ErrorCodes.MemoryErasure.Unavailable, prepared.Error.Code);

        Result<MemoryErasureResultDto> applied = await lexicon.ApplyAsync(
            new(target, Guid.NewGuid(), "x"),
            CovenantErasureAuthorityFixture.OperatorContext(new FakeCovenantAuthorityProvider()),
            Token);

        Assert.Equal(ErrorCodes.MemoryErasure.Unavailable, applied.Error.Code);

        Assert.Equal(target, (await ShowAsync(lexicon, Global, "Entity")).Target);

        Assert.Equal(0L, await FingerprintCountAsync((SqliteConnection)db.Database.GetDbConnection()));
    }

    /// <summary>
    /// The shared orphan assertion's Lexicon half fails on the one state it exists to catch: a claim
    /// whose entry row is gone.
    /// </summary>
    [SkippableFact]
    public async Task The_orphan_assertion_fails_on_a_lexicon_claim_whose_entry_row_is_gone()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        using ErasureHarness harness = ErasureHarness.Create(db);

        Result<LexiconEntryDto> scribed = await harness.Service.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.Global, Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);

        // An intact claim passes, so the failure below is about the deletion and not about the harness.
        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection);

        await ExecuteAsync(harness.Connection, $"DELETE FROM lexicon_entries WHERE Id = '{scribed.Value.Id:N}';");

        Assert.Equal(1L, await ScalarAsync(harness.Connection, $"SELECT count(*) FROM annal_claims WHERE SubjectStoreCode = 2 AND SubjectId = '{scribed.Value.Id:N}';"));

        XunitException failure = await Assert.ThrowsAnyAsync<XunitException>(
            () => AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection));

        Assert.Contains("'Lexicon'", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>COMMIT</c> that fails after its frame persisted is an erase that happened: the receipt is
    /// read back on a connection of its own and the committed erase is reported.
    /// </summary>
    [SkippableFact]
    public async Task A_commit_that_fails_after_persisting_reports_the_committed_erase()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        using ErasureHarness harness = ErasureHarness.Create(
            db,
            static async (connection, cancellationToken) =>
            {
                await ExecuteAsync(connection, "COMMIT;", cancellationToken);

                throw new SqliteException("disk I/O error", 10);
            });

        LexiconService lexicon = harness.Service;

        Result<LexiconEntryDto> scribed = await lexicon.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.Global, Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);

        Guid mutationId = Guid.NewGuid();

        (MemoryErasurePreflightDto preflight, MemoryErasureResultDto result) = await harness.EraseAsync(
            (await ShowAsync(lexicon, Global, "Entity")).Target,
            mutationId);

        Assert.False(result.Replayed);

        Assert.Equal(mutationId, result.MutationId);

        Assert.Equal(preflight.EffectDigest, result.EffectDigest);

        Assert.Equal(preflight.Plan.RowsToRemove, result.Local.RemovedRowCount);

        await AssertShowRefusedAsync(lexicon, Global, "Entity", ErrorCodes.Lexicon.NotFound);

        Assert.Equal(1L, await ReceiptCountAsync(harness.Connection, mutationId));

        Assert.Equal(1L, await FingerprintCountAsync(harness.Connection));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection);
    }

    /// <summary>
    /// A <c>COMMIT</c> that fails before anything persisted reports the failure and no result, and the
    /// post-commit scrub never runs: the receipt is absent on a fresh connection.
    /// </summary>
    [SkippableFact]
    public async Task A_commit_that_fails_without_persisting_reports_the_failure_and_no_result()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        using ErasureHarness harness = ErasureHarness.Create(
            db,
            static (_, _) => throw new SqliteException("disk I/O error", 10));

        LexiconService lexicon = harness.Service;

        Result<LexiconEntryDto> scribed = await lexicon.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.Global, Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);

        LexiconCurationTarget target = (await ShowAsync(lexicon, Global, "Entity")).Target;

        Guid mutationId = Guid.NewGuid();

        Result<MemoryErasurePreflightDto> prepared = await lexicon.PrepareAsync(new(target, mutationId), Token);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() => lexicon.ApplyAsync(
            new(target, mutationId, prepared.Value.PreflightToken),
            harness.OperatorContext,
            Token));

        Assert.Equal(10, failure.SqliteErrorCode);

        Assert.Empty(harness.ScrubLog.Entries);

        Assert.Equal(target, (await ShowAsync(lexicon, Global, "Entity")).Target);

        Assert.Equal(0L, await ReceiptCountAsync(harness.Connection, mutationId));

        Assert.Equal(0L, await FingerprintCountAsync(harness.Connection));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection);
    }

    /// <summary>
    /// A <c>COMMIT</c> that SQLite answers busy persisted nothing and is not an uncertain outcome: the
    /// attempt is rolled back and the erase runs again, committing once.
    /// </summary>
    [SkippableFact]
    public async Task A_busy_commit_is_retried_and_commits_the_erase_once()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        int commits = 0;

        using ErasureHarness harness = ErasureHarness.Create(
            db,
            async (connection, cancellationToken) =>
            {
                if (Interlocked.Increment(ref commits) == 1)
                {
                    throw new SqliteException("database is locked", 5);
                }

                await ExecuteAsync(connection, "COMMIT;", cancellationToken);
            });

        LexiconService lexicon = harness.Service;

        Result<LexiconEntryDto> scribed = await lexicon.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.Global, Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);

        Guid mutationId = Guid.NewGuid();

        (MemoryErasurePreflightDto preflight, MemoryErasureResultDto result) = await harness.EraseAsync(
            (await ShowAsync(lexicon, Global, "Entity")).Target,
            mutationId);

        Assert.Equal(2, Volatile.Read(ref commits));

        Assert.False(result.Replayed);

        Assert.Equal(preflight.Plan.RowsToRemove, result.Local.RemovedRowCount);

        await AssertShowRefusedAsync(lexicon, Global, "Entity", ErrorCodes.Lexicon.NotFound);

        Assert.Equal(1L, await ReceiptCountAsync(harness.Connection, mutationId));

        Assert.Equal(1L, await FingerprintCountAsync(harness.Connection));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection);
    }

    private static async Task<LexiconEntryDetail> ShowAsync(LexiconService lexicon, LexiconCurationScope scope, string name)
    {
        Result<LexiconInspectionResult<LexiconEntryDetail>> shown = await lexicon.ShowExactAsync(scope, name, null, Token);

        Assert.True(shown.IsSuccess, shown.IsFailure ? shown.Error.Message : null);

        return shown.Value.Value;
    }

    private static async Task AssertShowRefusedAsync(LexiconService lexicon, LexiconCurationScope scope, string name, string code)
    {
        Result<LexiconInspectionResult<LexiconEntryDetail>> shown = await lexicon.ShowExactAsync(scope, name, null, Token);

        Assert.Equal(code, shown.Error.Code);
    }

    private static async Task InstallAsync(SqliteConnection connection, GrimoireSchemaVersionChainSet chains, int version)
    {
        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 64, Token);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.Equal(version, result.Core.SchemaVersion);
    }

    /// <summary>The <c>lexicon_fts_data</c> blocks that still carry a sentinel. Assertion-only.</summary>
    private static async Task<long> SentinelBlocksAsync(SqliteConnection connection, string sentinel = Sentinel) =>
        (long)(await ScalarAsync(
            connection,
            $"SELECT count(*) FROM lexicon_fts_data WHERE instr(block, CAST('{sentinel}' AS BLOB)) > 0;"))!;

    private static async Task<long> FingerprintCountAsync(SqliteConnection connection) =>
        (long)(await ScalarAsync(connection, "SELECT count(*) FROM memory_erasure_fingerprints WHERE StoreCode = 3;"))!;

    private static async Task<long> ReceiptCountAsync(SqliteConnection connection, Guid mutationId) =>
        (long)(await ScalarAsync(
            connection,
            $"SELECT count(*) FROM memory_erasure_receipts WHERE MutationId = '{mutationId.ToString("D").ToUpperInvariant()}';"))!;

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken = default)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(Token);

        return value is DBNull ? null : value;
    }

    /// <summary>
    /// One Lexicon service composed for erasure over a fixture database: an isolated keyring as both the
    /// key provider and creator, the real token codec, a scrubber over fresh fixture connections whose
    /// log is captured, and an operator context issued for the erase routes' requirement.
    /// </summary>
    /// <remarks>
    /// The gate refuses every write lease: nothing here labels an entry, so a lease taken would be a
    /// lease no test asked for.
    /// </remarks>
    private sealed class ErasureHarness : IDisposable
    {
        private readonly ArcanumDbContext _db;

        private readonly MemoryErasureKeyring _keys;

        private readonly FakeCovenantAuthorityProvider _authority;

        private ErasureHarness(
            ArcanumDbContext db,
            MemoryErasureKeyring keys,
            FakeCovenantAuthorityProvider authority,
            TestCapturingLogger<MemoryErasureScrubber> scrubLog,
            LexiconService service)
        {
            _db = db;

            _keys = keys;

            _authority = authority;

            ScrubLog = scrubLog;

            Service = service;
        }

        internal LexiconService Service { get; }

        internal TestCapturingLogger<MemoryErasureScrubber> ScrubLog { get; }

        internal SqliteConnection Connection => (SqliteConnection)_db.Database.GetDbConnection();

        internal OperatorAuthorityContext OperatorContext => CovenantErasureAuthorityFixture.OperatorContext(_authority);

        internal static ErasureHarness Create(
            ArcanumDbContext db,
            Func<DbConnection, CancellationToken, Task>? commit = null)
        {
            MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(new InMemoryOsCredentialStore());

            TestCapturingLogger<MemoryErasureScrubber> scrubLog = new();

            FakeCovenantAuthorityProvider authority = new();

            LexiconErasureDependencies erasure = new(
                keys,
                new MemoryReviewTokenCodec(TimeProvider.System),
                new MemoryErasureScrubber(FixtureOrdinaryConnectionFactory.For(db), scrubLog),
                new RecordingCovenantOperationGate(),
                CovenantErasureAuthorityFixture.Issuer(authority),
                CovenantSqliteConnectionInitializer.Instance);

            LexiconService service = new(
                db,
                new TestCapturingLogger<LexiconService>(),
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { Annals = true } }),
                keys,
                erasure: erasure)
            {
                ErasureCommitForTesting = commit,
            };

            return new ErasureHarness(db, keys, authority, scrubLog, service);
        }

        /// <summary>Prepares and applies one erase, requiring each step to succeed.</summary>
        internal async Task<(MemoryErasurePreflightDto Preflight, MemoryErasureResultDto Result)> EraseAsync(
            LexiconCurationTarget target,
            Guid? mutationId = null)
        {
            Guid mutation = mutationId ?? Guid.NewGuid();

            Result<MemoryErasurePreflightDto> prepared = await Service.PrepareAsync(new(target, mutation), Token);

            Assert.True(prepared.IsSuccess, prepared.IsFailure ? $"{prepared.Error.Code}: {prepared.Error.Message}" : null);

            Result<MemoryErasureResultDto> applied = await Service.ApplyAsync(
                new(target, mutation, prepared.Value.PreflightToken),
                OperatorContext,
                Token);

            Assert.True(applied.IsSuccess, applied.IsFailure ? $"{applied.Error.Code}: {applied.Error.Message}" : null);

            return (prepared.Value, applied.Value);
        }

        public void Dispose() => _keys.Dispose();
    }
}
