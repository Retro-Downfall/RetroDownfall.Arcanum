using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

/// <summary>
/// External exposure an erase reports: fixed channel rules per store, and receipt windows measured
/// from the disclosure journal without ever counting or naming a disclosure.
/// </summary>
/// <remarks>
/// Every memory is written by its store's production writer and every receipt by the journal's own
/// transaction writer, so each window is measured against rows spelled exactly as a host spells them.
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class MemoryErasureExposureTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private const string CovenantKey = "preference.builds";

    private static readonly Guid Installation = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly Guid BootId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");

    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly MemoryExternalChannel[] ChannelOrder =
    [
        MemoryExternalChannel.InferenceProviderAuthorship,
        MemoryExternalChannel.InferenceProviderContext,
        MemoryExternalChannel.EmbeddingProvider,
        MemoryExternalChannel.EncryptedBackup,
        MemoryExternalChannel.OtherExternal,
    ];

    private static readonly string[] DisclosureObjects =
    [
        "external_disclosure_receipts",
        "disclosure_subject_state",
        "external_disclosure_receipts_guard_delete",
        "external_disclosure_receipts_guard_update",
        "disclosure_subject_state_guard_delete",
        "external_disclosure_state",
    ];

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private static CancellationToken Token => CancellationToken.None;

    private SqliteConnection Connection => (SqliteConnection)_db!.Database.GetDbConnection();

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();

        _db = fixture.CreateContext(_dbPath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [SkippableTheory]
    [InlineData("saga")]
    [InlineData("lexicon-agent")]
    [InlineData("lexicon-operator")]
    [InlineData("covenant-agent")]
    [InlineData("covenant-operator")]
    public async Task Each_store_reports_its_fixed_channel_rules(string row)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const MemoryExternalEvidence Known = MemoryExternalEvidence.Known;

        const MemoryExternalEvidence NotRecorded = MemoryExternalEvidence.NotRecorded;

        const MemoryExternalEvidence NotApplicable = MemoryExternalEvidence.NotApplicable;

        MemoryErasureExternalExposureDto exposure = row switch
        {
            "saga" => await ReadSagaAsync([await InsertSagaAsync("The operator prefers dark mode.", T0)]),
            "lexicon-agent" => await ReadLexiconAsync(await ScribeAsync("Mill Warden", annals: true)),
            "lexicon-operator" => await ReadLexiconAsync(await OperatorOnlyLexiconAsync("Mill Warden")),
            "covenant-agent" => await ReadCovenantAsync(agentProposes: true),
            _ => await ReadCovenantAsync(agentProposes: false),
        };

        MemoryExternalEvidence[] expected = row switch
        {
            "saga" => [Known, NotRecorded, Known, NotRecorded, NotRecorded],
            "lexicon-agent" or "covenant-agent" => [Known, NotRecorded, NotApplicable, NotRecorded, NotRecorded],
            _ => [NotRecorded, NotRecorded, NotApplicable, NotRecorded, NotRecorded],
        };

        Assert.Equal(MemoryExternalRevocation.NotPerformed, exposure.Revocation);

        Assert.Equal(ChannelOrder, exposure.Channels.Select(static channel => channel.Channel));

        Assert.Equal(expected, exposure.Channels.Select(static channel => channel.Evidence));

        Assert.Equal(expected, MemoryErasureExposure.EvidenceCodes(exposure));
    }

    [SkippableFact]
    public async Task A_backup_operation_counts_by_its_latest_receipt()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string id = await InsertSagaAsync("The operator prefers dark mode.", T0);

        // Both of this operation's receipts precede the memory, so nothing it archived could hold it.
        Guid earlier = Guid.NewGuid();

        await BackupReceiptAsync(Connection, earlier, 1, T0.AddDays(-2));

        await BackupReceiptAsync(Connection, earlier, 2, T0.AddDays(-1));

        Assert.Equal(MemoryExternalEvidence.NotRecorded, Backup(await ReadSagaAsync([id])));

        // This one began before the memory existed, and its archive was written after.
        Guid spanning = Guid.NewGuid();

        await BackupReceiptAsync(Connection, spanning, 3, T0.AddDays(-2));

        await BackupReceiptAsync(Connection, spanning, 4, T0.AddDays(1));

        Assert.Equal(MemoryExternalEvidence.ReceiptWindow, Backup(await ReadSagaAsync([id])));
    }

    [SkippableFact]
    public async Task The_saga_backup_window_starts_at_the_earliest_twin()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string content = "The operator prefers dark mode.";

        string first = await InsertSagaAsync(content, T0);

        string second = await InsertSagaAsync(content, T0.AddDays(9));

        await BackupReceiptAsync(Connection, Guid.NewGuid(), 1, T0.AddDays(4));

        Assert.Equal(MemoryExternalEvidence.ReceiptWindow, Backup(await ReadSagaAsync([second, first])));

        Assert.Equal(MemoryExternalEvidence.NotRecorded, Backup(await ReadSagaAsync([second])));

        // Stored ids match whatever spelling the caller holds.
        Assert.Equal(
            MemoryExternalEvidence.ReceiptWindow,
            Backup(await ReadSagaAsync([Guid.Parse(first).ToString("N").ToUpperInvariant(), second])));
    }

    [SkippableFact]
    public async Task A_lexicon_entry_without_a_claim_has_an_unbounded_backup_window()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LexiconEntryDto entry = await ScribeAsync("Mill Warden", annals: false);

        Assert.Equal(0L, await ScalarAsync(Connection, "SELECT count(*) FROM annal_claims WHERE SubjectStoreCode = 2;"));

        Assert.Equal(MemoryExternalEvidence.NotRecorded, Backup(await ReadLexiconAsync(entry)));

        await BackupReceiptAsync(Connection, Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch.AddDays(1));

        Assert.Equal(MemoryExternalEvidence.ReceiptWindow, Backup(await ReadLexiconAsync(entry)));
    }

    [SkippableFact]
    public async Task A_lexicon_backup_window_starts_at_its_claim()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LexiconEntryDto entry = await ScribeAsync("Mill Warden", annals: true);

        await BackupReceiptAsync(Connection, Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch.AddDays(1));

        Assert.Equal(MemoryExternalEvidence.NotRecorded, Backup(await ReadLexiconAsync(entry)));

        await BackupReceiptAsync(Connection, Guid.NewGuid(), 2, DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(MemoryExternalEvidence.ReceiptWindow, Backup(await ReadLexiconAsync(entry)));
    }

    [Fact]
    public async Task The_covenant_provider_window_is_time_only()
    {
        await using CovenantServiceHarness harness =
            await CovenantServiceHarness.StartAsync(Token, coreObjects: DisclosureObjects);

        await harness.SetAsync(CovenantScope.Global, null, CovenantKey, "Build from the repository root.", Token);

        SqliteConnection connection = harness.Fixture.Connection;

        Guid entryId = await CovenantEntryAsync(connection);

        DateTimeOffset created = UtcInstantText.Parse(await TextAsync(
            connection,
            $"SELECT CreatedAtUtc FROM covenant_entries WHERE EntryId = '{entryId:D}';"));

        Guid otherGeneration = Guid.NewGuid();

        Assert.NotEqual(await harness.Fixture.ReadDatasetGenerationAsync(Token), otherGeneration);

        Assert.Equal(MemoryExternalEvidence.NotRecorded, Context(await MemoryErasureExposure.ReadCovenantAsync(connection, null, entryId, Token)));

        // Before the entry existed, a dispatch could not have carried it.
        await ProviderReceiptAsync(connection, 1, created.AddDays(-1), otherGeneration);

        Assert.Equal(MemoryExternalEvidence.NotRecorded, Context(await MemoryErasureExposure.ReadCovenantAsync(connection, null, entryId, Token)));

        // A clean dispatch carried nothing Covenant-derived. The journal's writer refuses to record one
        // (a nonsensitive call carries no provenance, and the table requires provenance), so this row
        // is written directly: it pins the predicate against a row the schema would still admit.
        await InsertCleanProviderReceiptAsync(connection, created.AddDays(1));

        Assert.Equal(MemoryExternalEvidence.NotRecorded, Context(await MemoryErasureExposure.ReadCovenantAsync(connection, null, entryId, Token)));

        // A Covenant-derived dispatch after the entry existed is a window, whichever generation it names.
        await ProviderReceiptAsync(connection, 2, created.AddDays(1), otherGeneration);

        MemoryErasureExternalExposureDto exposure = await MemoryErasureExposure.ReadCovenantAsync(connection, null, entryId, Token);

        Assert.Equal(MemoryExternalEvidence.ReceiptWindow, Context(exposure));

        Assert.Equal(MemoryExternalEvidence.NotRecorded, Backup(exposure));
    }

    [Fact]
    public async Task The_covenant_backup_window_starts_at_the_entry()
    {
        await using CovenantServiceHarness harness =
            await CovenantServiceHarness.StartAsync(Token, coreObjects: DisclosureObjects);

        await harness.SetAsync(CovenantScope.Global, null, CovenantKey, "Build from the repository root.", Token);

        SqliteConnection connection = harness.Fixture.Connection;

        Guid entryId = await CovenantEntryAsync(connection);

        DateTimeOffset created = UtcInstantText.Parse(await TextAsync(
            connection,
            $"SELECT CreatedAtUtc FROM covenant_entries WHERE EntryId = '{entryId:D}';"));

        await BackupReceiptAsync(connection, Guid.NewGuid(), 1, created.AddDays(-1));

        Assert.Equal(MemoryExternalEvidence.NotRecorded, Backup(await MemoryErasureExposure.ReadCovenantAsync(connection, null, entryId, Token)));

        await BackupReceiptAsync(connection, Guid.NewGuid(), 2, created.AddDays(1));

        Assert.Equal(MemoryExternalEvidence.ReceiptWindow, Backup(await MemoryErasureExposure.ReadCovenantAsync(connection, null, entryId, Token)));
    }

    [Fact]
    public void Evidence_codes_follow_channel_order_and_refuse_an_incomplete_exposure()
    {
        MemoryErasureExternalExposureDto shuffled = new(
            MemoryExternalRevocation.NotPerformed,
            [
                new(MemoryExternalChannel.OtherExternal, MemoryExternalEvidence.NotRecorded),
                new(MemoryExternalChannel.EncryptedBackup, MemoryExternalEvidence.ReceiptWindow),
                new(MemoryExternalChannel.EmbeddingProvider, MemoryExternalEvidence.NotApplicable),
                new(MemoryExternalChannel.InferenceProviderContext, MemoryExternalEvidence.NotRecorded),
                new(MemoryExternalChannel.InferenceProviderAuthorship, MemoryExternalEvidence.Known),
            ]);

        Assert.Equal(
            [
                MemoryExternalEvidence.Known,
                MemoryExternalEvidence.NotRecorded,
                MemoryExternalEvidence.NotApplicable,
                MemoryExternalEvidence.ReceiptWindow,
                MemoryExternalEvidence.NotRecorded,
            ],
            MemoryErasureExposure.EvidenceCodes(shuffled));

        MemoryErasureExternalExposureDto missing = shuffled with { Channels = shuffled.Channels[1..] };

        _ = Assert.Throws<ArgumentException>(() => MemoryErasureExposure.EvidenceCodes(missing));
    }

    private static MemoryExternalEvidence Context(MemoryErasureExternalExposureDto exposure) =>
        Assert.Single(exposure.Channels, static channel => channel.Channel == MemoryExternalChannel.InferenceProviderContext).Evidence;

    private static MemoryExternalEvidence Backup(MemoryErasureExternalExposureDto exposure) =>
        Assert.Single(exposure.Channels, static channel => channel.Channel == MemoryExternalChannel.EncryptedBackup).Evidence;

    private Task<MemoryErasureExternalExposureDto> ReadSagaAsync(IReadOnlyList<string> ids) =>
        MemoryErasureExposure.ReadSagaAsync(Connection, null, ids, Token);

    private Task<MemoryErasureExternalExposureDto> ReadLexiconAsync(LexiconEntryDto entry) =>
        MemoryErasureExposure.ReadLexiconAsync(Connection, null, entry.Id, entry.Name.Trim().ToUpperInvariant(), entry.ScopeCampaignId, Token);

    private async Task<string> InsertSagaAsync(string content, DateTimeOffset createdAt)
    {
        ArcanumSettings settings = new();

        SagaMemoryStore store = new(
            _db!,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            MemoryErasureTestKeys.Isolated());

        string id = Guid.NewGuid().ToString();

        SagaMemoryWriteOutcome outcome = await store.InsertAsync(
            id,
            content,
            createdAt,
            null,
            null,
            "exposure-test",
            new float[settings.Integrations.Embeddings.Dimensions],
            Token);

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);

        return id;
    }

    private LexiconService Lexicon(bool annals) => new(
        _db!,
        NullLogger<LexiconService>.Instance,
        new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { Annals = annals } }),
        MemoryErasureTestKeys.Isolated());

    private async Task<LexiconEntryDto> ScribeAsync(string name, bool annals)
    {
        Result<LexiconEntryDto> scribed = await Lexicon(annals).UpsertAsync(name, "person", ["Keeps the north gate."], LexiconScope.Global, Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : string.Empty);

        return scribed.Value;
    }

    /// <summary>
    /// An entry whose every recorded version is the operator's or an upgrade's: written before the
    /// Annals existed, claimed by the upgrade's backfill, then corrected by the operator.
    /// </summary>
    /// <remarks>
    /// Lexicon has no operator create path, and a correction of an entry with no claim records the
    /// content it replaces as the agent's (which is what it was), so a scribe followed by a correction
    /// is always agent-authored. The entry an operator alone can answer for is the legacy one.
    /// </remarks>
    private async Task<LexiconEntryDto> OperatorOnlyLexiconAsync(string name)
    {
        LexiconEntryDto scribed = await ScribeAsync(name, annals: false);

        await using (SqliteTransaction transaction = Connection.BeginTransaction())
        {
            MemoryAnnalsBackfill backfill = new();

            while (!(await backfill.AdvanceBatchAsync(Connection, transaction, null, Token)).IsComplete)
            {
            }

            await transaction.CommitAsync(Token);
        }

        LexiconService lexicon = Lexicon(annals: false);

        Result<LexiconInspectionResult<LexiconEntryDetail>> shown =
            await lexicon.ShowExactAsync(new LexiconCurationScope(LexiconScopeKind.Global, null), name, null, Token);

        Assert.True(shown.IsSuccess, shown.IsFailure ? shown.Error.Message : string.Empty);

        Result<LexiconCurationResult> corrected = await lexicon.CorrectAsync(
            shown.Value.Value.Target,
            new("person", ["Keeps the north gate.", "Answers to the reeve."]),
            null,
            Token);

        Assert.True(corrected.IsSuccess, corrected.IsFailure ? corrected.Error.Message : string.Empty);

        Assert.Equal(LexiconCurationOutcomeKind.Applied, corrected.Value.Outcome);

        Assert.Equal(0L, await ScalarAsync(
            Connection,
            "SELECT count(*) FROM annal_versions WHERE OriginCode IN (2, 3);"));

        Assert.True(await ScalarAsync(Connection, "SELECT count(*) FROM annal_versions WHERE OriginCode = 1;") >= 1);

        return scribed;
    }

    private static async Task<MemoryErasureExternalExposureDto> ReadCovenantAsync(bool agentProposes)
    {
        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        Guid campaign = CovenantOperationGateFixture.CampaignOne;

        await harness.AddCampaignAsync(campaign, Token);

        await harness.SetAsync(CovenantScope.Campaign, campaign, CovenantKey, "Build from the repository root.", Token);

        if (agentProposes)
        {
            long keyEpoch = await ScalarAsync(
                harness.Fixture.Connection,
                $"SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = '{CovenantKey}';");

            CovenantMutationBatch batch = await CovenantMutationFixture.LiveBatchAsync(
                harness.Fixture,
                Token,
                CovenantMutationFixture.AgentPropose(campaign, CovenantKey, "The model suggests building from tools.", 0, keyEpoch));

            Result<IReadOnlyList<CovenantMutationReceipt>> applied =
                await CovenantMutationFixture.ApplyAsync(harness.Fixture, batch, Token);

            Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

            Assert.Equal(1L, await ScalarAsync(harness.Fixture.Connection, "SELECT count(*) FROM covenant_versions WHERE OriginCode = 2;"));
        }

        return await MemoryErasureExposure.ReadCovenantAsync(
            harness.Fixture.Connection,
            null,
            await CovenantEntryAsync(harness.Fixture.Connection),
            Token);
    }

    private static async Task<Guid> CovenantEntryAsync(SqliteConnection connection) =>
        Guid.Parse(
            await TextAsync(connection, $"SELECT EntryId FROM covenant_entries WHERE NormalizedKey = '{CovenantKey}';"),
            CultureInfo.InvariantCulture);

    /// <summary>One receipt of a backup operation, recorded the way the backup boundary records it.</summary>
    private static async Task BackupReceiptAsync(SqliteConnection connection, Guid operationId, byte seed, DateTimeOffset at)
    {
        GenerationProvenance provenance = GenerationProvenance.CreateExact([operationId]);

        ProviderCallSensitivity sensitivity = Sensitivity(ContentSensitivity.CovenantDerived, provenance);

        CovenantDisclosureDraft draft = new(
            Installation,
            CovenantDisclosureSubjectKind.Operation,
            operationId,
            CovenantTask6Fixture.D(seed),
            CovenantEgressDestination.EncryptedBackup,
            CovenantDisclosureRevocability.Nonrevocable,
            CovenantTask6Fixture.D(80),
            sensitivity.Digest,
            null,
            null,
            CovenantTask6Fixture.D(83),
            at.ToUnixTimeMilliseconds());

        Result<CovenantDisclosureReceipt> acknowledged = await new CovenantDisclosureTransactionWriter(BootId).AcknowledgeAsync(
            connection,
            draft,
            CovenantDisclosureEffectCategory.EncryptedBackup,
            sensitivity,
            Token);

        Assert.True(acknowledged.IsSuccess, acknowledged.IsFailure ? acknowledged.Error.Message : string.Empty);
    }

    /// <summary>One nonrevocable Covenant-derived provider dispatch of a turn.</summary>
    private static async Task ProviderReceiptAsync(SqliteConnection connection, byte seed, DateTimeOffset at, Guid generation)
    {
        GenerationProvenance provenance = GenerationProvenance.CreateExact([generation]);

        ProviderCallSensitivity sensitivity = Sensitivity(ContentSensitivity.CovenantDerived, provenance);

        CovenantDisclosureDraft draft = new(
            Installation,
            CovenantDisclosureSubjectKind.Turn,
            Guid.NewGuid(),
            CovenantTask6Fixture.D(seed),
            CovenantEgressDestination.Provider,
            CovenantDisclosureRevocability.Nonrevocable,
            CovenantTask6Fixture.D(80),
            sensitivity.Digest,
            null,
            CovenantTask6Fixture.D(82),
            null,
            at.ToUnixTimeMilliseconds());

        Result<CovenantDisclosureReceipt> acknowledged = await new CovenantDisclosureTransactionWriter(BootId).AcknowledgeAsync(
            connection,
            draft,
            CovenantDisclosureEffectCategory.ProviderDispatch,
            sensitivity,
            Token);

        Assert.True(acknowledged.IsSuccess, acknowledged.IsFailure ? acknowledged.Error.Message : string.Empty);
    }

    private static async Task InsertCleanProviderReceiptAsync(SqliteConnection connection, DateTimeOffset at)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO external_disclosure_receipts (
                OriginInstallationId, SubjectKind, SubjectId, SubjectOrdinal, EffectCategoryCode,
                CategoryPhysicalAttemptOrdinal, EffectIdentityDigest, DestinationCode, RevocabilityCode,
                DestinationDigest, SensitivityCode, GenerationProvenanceModeCode, ExactGenerationIds,
                GenerationBloom, WardEvidenceDigest, AdmissionEvidenceDigest, BackupEvidenceDigest, DisclosedAtUtc)
            VALUES ($installation, 1, $subject, 1, 1, 1, randomblob(32), 1, 2, randomblob(32), 0, 1, randomblob(16),
                NULL, NULL, NULL, NULL, $disclosedAtUtc);
            """;

        _ = command.Parameters.AddWithValue("$installation", Installation.ToString("D"));

        _ = command.Parameters.AddWithValue("$subject", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue("$disclosedAtUtc", UtcInstantText.Format(at));

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    private static ProviderCallSensitivity Sensitivity(ContentSensitivity level, GenerationProvenance provenance) =>
        new(
            level,
            provenance,
            CovenantDigests.Sensitivity(new SensitivityDigestInput(
                level,
                provenance.Mode,
                provenance.ExactGenerationIds,
                provenance.BloomBits)));

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(Token);
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);
    }

    private static async Task<string> TextAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return (string)(await command.ExecuteScalarAsync(Token))!;
    }
}
