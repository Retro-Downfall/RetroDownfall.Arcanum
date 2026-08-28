using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Annals;

/// <summary>
/// Every Saga memory written while the Annals is enabled records what it claimed, in the memory's own
/// transaction.
/// </summary>
/// <remarks>
/// The entry point is <see cref="ISagaMemoryStore"/>, which is what every production caller holds — the
/// extraction service, the endpoints, and the tool surface all take the interface. Nothing here writes an
/// <c>annal_*</c> row; every claim asserted about is one production produced.
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SagaAnnalsWriteThroughTests : IAsyncLifetime
{

    private const int TestDimensions = 64;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public SagaAnnalsWriteThroughTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

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

    [SkippableFact]
    public async Task An_inserted_memory_receives_a_claim_asserting_the_content_that_was_stored()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ISagaMemoryStore store = CreateStore(annals: true);

        const string Content = "The operator prefers dark mode.";

        DateTimeOffset createdAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);

        _ = await store.InsertAsync("mem-1", Content, createdAt, sessionId: null, null, "extraction", Vec(1f), CancellationToken.None);

        ClaimRow claim = await ReadClaimAsync("mem-1");

        Assert.Equal(1, claim.Revision);

        Assert.Equal(AnnalOperation.Assert, claim.Operation);

        // Saga has no operator write path and no scribe tool, so every row is a headless extraction's
        // inference from a finished transcript. Any other origin here would be a claim about a warrant
        // nothing in the product can produce.
        Assert.Equal(AnnalOrigin.AgentExtracted, claim.Origin);

        Assert.Equal(AnnalContentDigest.ForSagaMemory(Content), claim.ContentHash);

        Assert.Equal(createdAt, claim.RecordedAtUtc);

        Assert.Equal(createdAt, claim.ValidFromUtc);

    }

    /// <summary>
    /// The claim reuses the scope the store just derived from the owning Session's canonical binding
    /// rather than deriving a second one. Two derivations of one authority eventually disagree, and the
    /// disagreement would land on what a turn may recall.
    /// </summary>
    [SkippableFact]
    public async Task An_inserted_memorys_claim_carries_the_scope_the_store_derived()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ISagaMemoryStore store = CreateStore(annals: true);

        Guid sessionId = Guid.NewGuid();

        _ = await store.InsertAsync(
            "mem-scope",
            "a conclusion from an unbound session",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            sessionId,
            null,
            "extraction",
            Vec(1f),
            CancellationToken.None);

        ClaimRow claim = await ReadClaimAsync("mem-scope");

        (SagaMemoryScopeKind rowKind, string? rowCampaign) = await ReadMemoryScopeAsync("mem-scope");

        Assert.Equal(rowKind, claim.ScopeKind);

        Assert.Equal(rowCampaign, claim.CampaignId);

        // A Session with no resolvable binding supplies no authority, and the claim must say so rather
        // than rounding up to installation-global.
        Assert.NotEqual(SagaMemoryScopeKind.Global, claim.ScopeKind);

        Assert.Equal(sessionId.ToString(), claim.SourceSessionId);

    }

    [SkippableFact]
    public async Task With_the_gate_off_an_inserted_memory_receives_no_claim_and_is_stored_unchanged()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ISagaMemoryStore store = CreateStore(annals: false);

        _ = await store.InsertAsync(
            "mem-off",
            "a conclusion nothing claimed",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            sessionId: null,
            null,
            "extraction",
            Vec(1f),
            CancellationToken.None);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM annal_claims;"));

        SagaMemoryDto[] page = await store.ListAsync(
            null,
            null,
            MemoryScope.Installation,
            100,
            0,
            CancellationToken.None);

        Assert.Equal("a conclusion nothing claimed", Assert.Single(page).Content);

    }

    /// <summary>
    /// A claim and the memory it describes share one transaction, so a store can never hold a memory the
    /// Annals cannot explain, or a claim describing a memory that was never written.
    /// </summary>
    [SkippableFact]
    public async Task Every_memory_the_store_holds_has_exactly_one_claim()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ISagaMemoryStore store = CreateStore(annals: true);

        for (int index = 0; index < 5; index++)
        {

            _ = await store.InsertAsync(
                $"mem-{index}",
                $"conclusion {index}",
                DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                sessionId: null,
                null,
                "extraction",
                Vec(index + 1),
                CancellationToken.None);

        }

        Assert.Equal(5, await store.CountAsync(CancellationToken.None));

        Assert.Equal(5, await CountAsync("SELECT COUNT(*) FROM annal_claims;"));

        Assert.Equal(5, await CountAsync("SELECT COUNT(*) FROM annal_versions;"));

        Assert.Equal(5, await CountAsync("SELECT COUNT(*) FROM annal_heads;"));

        Assert.Equal(
            0,
            await CountAsync(
                """
                SELECT COUNT(*) FROM saga_memories AS memory
                WHERE NOT EXISTS (
                    SELECT 1 FROM annal_claims AS claim
                    WHERE claim.SubjectStoreCode = 1 AND claim.SubjectId = memory.Id);
                """));

    }

    /// <summary>
    /// The converse of the pairing above: no claim outlives the memory it describes, whichever removal
    /// took the memory.
    /// </summary>
    /// <remarks>
    /// A claim is reached through the row that names it, so one left behind is a record no surface can
    /// read and no reset can clear. It is also what lets a count over this store's own tables answer for
    /// its Annals rows as well, which a reset interrupted before its commit relies on: that inference is
    /// sound only while a memory and the claim explaining it go in one transaction or neither goes.
    ///
    /// <para>The memory removed singly is retired first, so its claim carries a tombstone beside the
    /// assertion. A removal that released the head alone would leave the versions behind it standing,
    /// and against a single-revision claim that is indistinguishable from taking the whole claim.</para>
    /// </remarks>
    [SkippableFact]
    public async Task No_claim_outlives_the_memory_it_describes()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true).ConfigureAwait(false);

        const string Retired = "the operator prefers tabs";

        _ = await harness.Store.InsertAsync(
            "m-retired", Retired, DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-standing", "the operator prefers spaces", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(1), CancellationToken.None).ConfigureAwait(false);

        _ = await harness.Store.RetireAsync(
            "m-retired", AnnalContentDigest.ForSagaMemory(Retired),
            DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);

        Assert.True(await harness.Store.DeleteAsync("m-retired", CancellationToken.None).ConfigureAwait(false));

        Assert.Equal(0, await OrphanedSagaClaimsAsync(harness).ConfigureAwait(false));

        // The memory left standing is what makes the line above bite: its own claim belongs where it
        // is, so what a delete failed to take is the only thing an orphan count can be counting.
        Assert.Equal(1, await harness.CountAsync("annal_claims", "SubjectStoreCode = 1").ConfigureAwait(false));

        await harness.Store.DeleteAllAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(0, await OrphanedSagaClaimsAsync(harness).ConfigureAwait(false));

        Assert.Equal(0, await harness.CountAsync("annal_claims", "SubjectStoreCode = 1").ConfigureAwait(false));

    }

    [SkippableFact]
    public async Task A_retirement_appends_a_tombstone_that_supersedes_the_version_it_ends()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true).ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        bool appended = await AnnalsClaimWriter.AppendRetirementAsync(
            harness.Connection,
            null,
            AnnalSubjectStore.Saga,
            "m-1",
            AnnalOrigin.OperatorStated,
            SagaMemoryScopeKind.Global,
            null,
            ContentSensitivity.None,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(appended);

        AnnalClaimHead? head = await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None)
            .ConfigureAwait(false);

        Assert.NotNull(head);

        Assert.Equal(AnnalOperation.Retire, head.CurrentOperation);

        Assert.Equal(2, head.CurrentRevision);

        IReadOnlyList<AnnalClaimVersion> history = await harness.Annals
            .GetVersionsAsync(head.ClaimId, CancellationToken.None)
            .ConfigureAwait(false);

        // The tombstone binds to nothing, and it names the version it ended.
        AnnalClaimVersion tombstone = history[^1];

        Assert.Equal(AnnalOperation.Retire, tombstone.Operation);

        Assert.Equal(history[0].VersionId, tombstone.PredecessorVersionId);

        IReadOnlyList<AnnalDependencyEdge> edges = await harness.Annals
            .GetDependenciesAsync(tombstone.VersionId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(AnnalDependencyRelation.Supersedes, Assert.Single(edges).Relation);

    }

    [SkippableFact]
    public async Task Retiring_a_claim_less_memory_records_who_asserted_it_before_who_ended_it()
    {

        // A memory written while the Annals was disabled has no claim. Opening one at the retirement with
        // the operator as its author would rewrite history: extraction asserted this memory, and the
        // operator only ended it.
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: false).ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        Assert.Null(await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None)
            .ConfigureAwait(false));

        _ = await AnnalsClaimWriter.AppendAssertAsync(
            harness.Connection, null, AnnalSubjectStore.Saga, "m-1",
            AnnalOrigin.AgentExtracted, SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
            CancellationToken.None).ConfigureAwait(false);

        _ = await AnnalsClaimWriter.AppendRetirementAsync(
            harness.Connection, null, AnnalSubjectStore.Saga, "m-1",
            AnnalOrigin.OperatorStated, SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
            CancellationToken.None).ConfigureAwait(false);

        AnnalClaimHead head = (await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None)
            .ConfigureAwait(false))!;

        IReadOnlyList<AnnalClaimVersion> history = await harness.Annals
            .GetVersionsAsync(head.ClaimId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(AnnalOrigin.AgentExtracted, history[0].Origin);

        Assert.Equal(AnnalOrigin.OperatorStated, history[1].Origin);

    }

    /// <summary>
    /// The refusal is not just a reported "no" -- opening a claim here would be this method guessing an
    /// origin for a version it never saw asserted, so nothing may land in <c>annal_claims</c> either.
    /// </summary>
    [SkippableFact]
    public async Task Retiring_a_subject_with_no_claim_writes_nothing_and_says_so()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: false).ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        bool appended = await AnnalsClaimWriter.AppendRetirementAsync(
            harness.Connection, null, AnnalSubjectStore.Saga, "m-1",
            AnnalOrigin.OperatorStated, SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
            CancellationToken.None).ConfigureAwait(false);

        Assert.False(appended);

        // Not merely "no claim comes back" -- nothing was written for this subject at all.
        Assert.Equal(
            0,
            await harness.CountAsync("annal_claims", "SubjectStoreCode = 1 AND SubjectId = 'm-1'")
                .ConfigureAwait(false));

    }

    /// <summary>
    /// A second retirement records no change, so the claim's version count and its head's revision must
    /// come back exactly as they were before the second call -- not merely "some" unspecified value.
    /// </summary>
    [SkippableFact]
    public async Task Retiring_an_already_retired_claim_writes_nothing_and_says_so()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: false).ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        _ = await AnnalsClaimWriter.AppendAssertAsync(
            harness.Connection, null, AnnalSubjectStore.Saga, "m-1",
            AnnalOrigin.AgentExtracted, SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
            CancellationToken.None).ConfigureAwait(false);

        _ = await AnnalsClaimWriter.AppendRetirementAsync(
            harness.Connection, null, AnnalSubjectStore.Saga, "m-1",
            AnnalOrigin.OperatorStated, SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
            CancellationToken.None).ConfigureAwait(false);

        AnnalClaimHead headBefore = (await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None)
            .ConfigureAwait(false))!;

        int revisionBefore = headBefore.CurrentRevision;

        int versionCountBefore = await harness.CountAsync("annal_versions", $"ClaimId = '{headBefore.ClaimId}'")
            .ConfigureAwait(false);

        bool appendedAgain = await AnnalsClaimWriter.AppendRetirementAsync(
            harness.Connection, null, AnnalSubjectStore.Saga, "m-1",
            AnnalOrigin.OperatorStated, SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
            CancellationToken.None).ConfigureAwait(false);

        Assert.False(appendedAgain);

        AnnalClaimHead headAfter = (await harness.Annals
            .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", CancellationToken.None)
            .ConfigureAwait(false))!;

        Assert.Equal(revisionBefore, headAfter.CurrentRevision);

        Assert.Equal(
            versionCountBefore,
            await harness.CountAsync("annal_versions", $"ClaimId = '{headBefore.ClaimId}'").ConfigureAwait(false));

    }

    /// <summary>Saga claims whose subject row is no longer there.</summary>
    private static Task<int> OrphanedSagaClaimsAsync(SagaStoreHarness harness) =>
        harness.CountAsync(
            "annal_claims",
            """SubjectStoreCode = 1 AND SubjectId NOT IN (SELECT "Id" FROM "saga_memories")""");

    private static float[] Vec(params float[] leading)
    {

        float[] result = new float[TestDimensions];

        leading.AsSpan().CopyTo(result);

        return result;

    }

    private ISagaMemoryStore CreateStore(bool annals) =>
        new SagaMemoryStore(
            _db!,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Features = new FeatureSettings { Annals = annals },
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings { Dimensions = TestDimensions },
                    },
                }));

    private async Task<ClaimRow> ReadClaimAsync(string memoryId)
    {

        await using SqliteCommand command = (SqliteCommand)_db!.Database.GetDbConnection().CreateCommand();

        command.CommandText =
            """
            SELECT version.Revision, version.OperationCode, version.OriginCode, version.ScopeKindCode,
                   version.CampaignId, version.ContentHash, version.ValidFromUtc, version.RecordedAtUtc,
                   version.SourceSessionId
            FROM annal_claims AS claim
            JOIN annal_heads AS head ON head.ClaimId = claim.ClaimId
            JOIN annal_versions AS version ON version.VersionId = head.CurrentVersionId
            WHERE claim.SubjectStoreCode = 1 AND claim.SubjectId = $subjectId;
            """;

        _ = command.Parameters.AddWithValue("$subjectId", memoryId);

        await OpenAsync();

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None), $"no claim for {memoryId}");

        return new ClaimRow(
            reader.GetInt32(0),
            (AnnalOperation)reader.GetInt32(1),
            (AnnalOrigin)reader.GetInt32(2),
            (SagaMemoryScopeKind)reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            (byte[])reader.GetValue(5),
            Parse(reader.GetString(6)),
            Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8));

    }

    private async Task<(SagaMemoryScopeKind Kind, string? CampaignId)> ReadMemoryScopeAsync(string memoryId)
    {

        await OpenAsync();

        await using SqliteCommand command = (SqliteCommand)_db!.Database.GetDbConnection().CreateCommand();

        // Deliberately unquoted. SQLite reads a double-quoted identifier matching no column as a string
        // literal, so a quoted name would return the word rather than failing.
        command.CommandText = """SELECT ScopeKindCode, CampaignId FROM "saga_memories" WHERE "Id" = $id;""";

        _ = command.Parameters.AddWithValue("$id", memoryId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));

        return ((SagaMemoryScopeKind)reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));

    }

    private async Task<int> CountAsync(string sql)
    {

        await OpenAsync();

        await using SqliteCommand command = (SqliteCommand)_db!.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);

    }

    private async Task OpenAsync()
    {

        if (_db!.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
        {

            await _db.Database.OpenConnectionAsync(CancellationToken.None);

        }

    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record ClaimRow(
        int Revision,
        AnnalOperation Operation,
        AnnalOrigin Origin,
        SagaMemoryScopeKind ScopeKind,
        string? CampaignId,
        byte[] ContentHash,
        DateTimeOffset ValidFromUtc,
        DateTimeOffset RecordedAtUtc,
        string? SourceSessionId);

}
