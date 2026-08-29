using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// A memory reset and a factory reset both take the claims describing the memories they clear, and
/// neither takes the other store's.
/// </summary>
/// <remarks>
/// The rows are seeded through raw SQL for the same reason the Campaign-scoped reset suite gives: what
/// is under test is the erasure predicate — which rows a reset selects, counts, deletes, and then
/// reconciles — and a writer in the loop would only add a second thing that could be wrong.
/// </remarks>
public sealed partial class DataRetentionServiceTests
{

    private static readonly Guid AnnalsCampaignA = new("A0000000-0000-4000-8000-00000000AA11");

    private static readonly Guid AnnalsCampaignB = new("B0000000-0000-4000-8000-00000000BB22");

    [SkippableFact]
    public async Task ApplyAsync_ResetMemory_Saga_ClearsSagaClaimsAndLeavesLexiconClaimsStanding()
    {

        RequireSqlCipher();

        string memory = await SeedGlobalSagaMemoryAsync();

        await SeedClaimAsync(1, memory);

        string entry = await SeedClaimedLexiconEntryAsync("config", string.Empty);

        await ApplyUntargetedResetAsync(MemoryResetScope.Saga);

        Assert.Equal(0, await CountAnnalClaimsAsync(1, memory));

        Assert.Equal(1, await CountAnnalClaimsAsync(2, entry));

        Assert.Equal(1, await CountTableRowsAsync("annal_versions"));

        Assert.Equal(1, await CountTableRowsAsync("annal_heads"));

    }

    /// <summary>
    /// A whole-store Lexicon reset completes and leaves the index consistent with the entries.
    /// </summary>
    /// <remarks>
    /// This is a regression test for a defect that predates the Annals: the selection list cleared
    /// <c>lexicon_fts</c> directly, and the <c>lexicon_entries_ad</c> trigger then issued an FTS5 delete
    /// for a row the index no longer held. SQLite reported "database disk image is malformed" and the
    /// whole reset aborted, so <c>reset-memory --scope lexicon</c> could not complete at all while the
    /// Lexicon held any entry. The index rows are retired by the trigger instead, which is what the
    /// Campaign-targeted list has always done.
    /// </remarks>
    [SkippableFact]
    public async Task ApplyAsync_ResetMemory_Lexicon_ClearsEveryEntryAndItsIndex()
    {

        RequireSqlCipher();

        await SeedScopedLexiconEntryAsync("config", string.Empty);

        await SeedScopedLexiconEntryAsync("other", AnnalsCampaignA.ToString());

        await ApplyUntargetedResetAsync(MemoryResetScope.Lexicon);

        Assert.Equal(0, await CountTableRowsAsync("lexicon_entries"));

        // The index has to agree. A reset that emptied the entries and left index rows behind would
        // leave the Lexicon answering searches for entities that no longer exist.
        Assert.Equal(0, await CountLexiconFtsMatchesAsync("config"));

        Assert.Equal(0, await CountLexiconFtsMatchesAsync("other"));

    }

    /// <summary>
    /// Both memory-reset arms report every version they removed, including the ones a cascade took.
    /// </summary>
    /// <remarks>
    /// <c>annal_versions.PredecessorVersionId</c> references its own table <c>ON DELETE CASCADE</c> and
    /// SQLite counts only what a statement deletes directly, so one delete over a claim carrying two
    /// revisions removed both and reported one. The reset does not abort on it — its conflict check
    /// compares a pre-delete count rather than this sum — so what an operator sees is a removal reported
    /// as smaller than it was.
    ///
    /// <para>The rehearsal's own number is the expectation rather than a literal, so this stays a
    /// statement about the two agreeing rather than about how many versions a retirement happens to
    /// write.</para>
    /// </remarks>
    [SkippableTheory]

    [InlineData(false)]

    [InlineData(true)]

    public async Task ApplyAsync_ResetMemory_Saga_ReportsEveryVersionItRemoved(bool forOneCampaign)
    {

        RequireSqlCipher();

        Guid? campaign = forOneCampaign ? AnnalsCampaignA : null;

        Guid? session = null;

        if (campaign is { } owned)
        {

            await SeedCampaignRowAsync(owned);

            session = await SessionBindingWriters.BoundByTheRepositoryAsync(
                _db!, owned, CancellationToken.None);

        }

        _ = await WriteAndRetireSagaMemoryAsync(session, "the operator prefers tabs");

        Assert.Equal(2, await CountTableRowsAsync("annal_versions"));

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(
            DataRetentionOperation.ResetMemory,
            campaign,
            MemoryResetScope.Saga);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(plan.DerivedRecords, applied.Value.DerivedRecordsDeleted);

        Assert.Equal(0, await CountTableRowsAsync("annal_versions"));

    }

    /// <summary>
    /// A factory reset clears a claim that carries more than one revision.
    /// </summary>
    /// <remarks>
    /// <c>annal_versions.PredecessorVersionId</c> references its own table <c>ON DELETE CASCADE</c>, and
    /// SQLite reports only the rows a statement deletes directly. A whole-table delete therefore emptied
    /// the table while reporting one row too few for every superseded version — and the reset compared
    /// that report against its own preview, read the shortfall as the data having changed underneath it,
    /// and aborted as a conflict. On every retry, for as long as one corrected or retired memory existed.
    ///
    /// <para>Two revisions is the smallest chain that shows it, and one retirement writes exactly two:
    /// the assertion it reconstructs at the memory's own timestamp, and the tombstone that ends it. The
    /// count before the reset is asserted so that a retirement which stopped writing the pair would fail
    /// here rather than leave this case passing against a chain of one.</para>
    /// </remarks>
    [SkippableFact]
    public async Task ApplyAsync_FactoryReset_ClearsAClaimThatCarriesMoreThanOneRevision()
    {

        RequireSqlCipher();

        _ = await WriteAndRetireSagaMemoryAsync(sessionId: null, "the operator prefers tabs");

        Assert.Equal(2, await CountTableRowsAsync("annal_versions"));

        (LongRunningOperationReconciliationSummary recovery, _) =
            await ReconcileFactoryResetV0Async(
                CreateService(),
                "annals-revision-factory-recovery-test");

        Assert.Equal(1, recovery.Completed);

        Assert.Equal(0, recovery.RequiresAttention);

        Assert.Equal(0, await CountTableRowsAsync("annal_versions"));

        Assert.Equal(0, await CountTableRowsAsync("annal_heads"));

        Assert.Equal(0, await CountTableRowsAsync("annal_claims"));

    }

    /// <summary>
    /// A Lexicon reset leaves the Saga store's retirement evidence and its key exactly where they were.
    /// </summary>
    /// <remarks>
    /// The two tables belong to one store, and a reset that named the other one has no business
    /// reaching them. Taking them here would un-retire, silently and for the whole installation, every
    /// conclusion an operator had rejected — and the operator who asked to clear the Lexicon would have
    /// no reason to look.
    /// </remarks>
    [SkippableFact]
    public async Task ApplyAsync_ResetMemory_Lexicon_LeavesSagaRetirementEvidenceStanding()
    {

        RequireSqlCipher();

        await SeedScopedLexiconEntryAsync("config", string.Empty);

        const string retired = "the operator prefers tabs";

        _ = await WriteAndRetireSagaMemoryAsync(sessionId: null, retired);

        await ApplyUntargetedResetAsync(MemoryResetScope.Lexicon);

        Assert.Equal(0, await CountTableRowsAsync("lexicon_entries"));

        Assert.Equal(1, await CountAllAsync("saga_retirement_suppressions"));

        Assert.Equal(1, await CountAllAsync("saga_suppression_key"));

        Assert.Equal(
            SagaMemoryWriteOutcome.Suppressed,
            await CreateSagaMemoryStore().InsertAsync(
                Guid.NewGuid().ToString(), retired, DateTimeOffset.UtcNow, sessionId: null,
                tags: null, source: "test", SagaEmbedding(), CancellationToken.None));

    }

    [SkippableFact]
    public async Task ApplyAsync_ResetMemory_Lexicon_ClearsLexiconClaimsAndLeavesSagaClaimsStanding()
    {

        RequireSqlCipher();

        string memory = await SeedGlobalSagaMemoryAsync();

        await SeedClaimAsync(1, memory);

        string entry = await SeedClaimedLexiconEntryAsync("config", string.Empty);

        await ApplyUntargetedResetAsync(MemoryResetScope.Lexicon);

        Assert.Equal(0, await CountAnnalClaimsAsync(2, entry));

        Assert.Equal(1, await CountAnnalClaimsAsync(1, memory));

    }

    /// <summary>
    /// A Campaign-targeted reset reaches exactly the claims over that Campaign's memories, and leaves
    /// every other Campaign's and every installation-scoped memory's claim where it was.
    /// </summary>
    [SkippableFact]
    public async Task ApplyAsync_ResetMemory_ForOneCampaign_ClearsOnlyThatCampaignsClaims()
    {

        RequireSqlCipher();

        string ownedByA = await SeedScopedSagaMemoryAsync(AnnalsCampaignA);

        await SeedClaimAsync(1, ownedByA);

        string ownedByB = await SeedScopedSagaMemoryAsync(AnnalsCampaignB);

        await SeedClaimAsync(1, ownedByB);

        string installationScoped = await SeedGlobalSagaMemoryAsync();

        await SeedClaimAsync(1, installationScoped);

        await ApplyCampaignResetAsync(MemoryResetScope.Saga, AnnalsCampaignA);

        Assert.Equal(0, await CountAnnalClaimsAsync(1, ownedByA));

        Assert.Equal(1, await CountAnnalClaimsAsync(1, ownedByB));

        Assert.Equal(1, await CountAnnalClaimsAsync(1, installationScoped));

    }

    /// <summary>
    /// Pruning an aged memory takes its claim with it, and says so in the rehearsal beforehand.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A prune that removed the memory and left the claim would leave a record
    /// describing content that no longer exists; a rehearsal that under-counted what the apply removes is
    /// a rehearsal an operator cannot rely on, and the apply's own reconciliation would still call it
    /// clean because it never looked.
    /// </remarks>
    [SkippableFact]
    public async Task ApplyAsync_Prune_RemovesAnAgedMemorysClaimAndCountsItFirst()
    {

        RequireSqlCipher();

        string memory = await SeedGlobalSagaMemoryAsync();

        await SeedClaimAsync(1, memory);

        ArcanumSettings settings = new()
        {

            Retention = new RetentionSettings
            {

                AutomaticSweepsEnabled = false,

                SagaMemories = new RetentionRuleSettings { Enabled = true, Days = 1 },

            },

        };

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        long sagaDerived = plan.Items
            .Where(static item => item.DataClass == RetentionDataClass.SagaMemories)
            .Sum(static item => item.DerivedRecords);

        // The embedding plus the claim. Without the claim in the count this is 1.
        Assert.Equal(2, sagaDerived);

        Core.Primitives.Result<DataRetentionApplyResult> applied = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.True(applied.Value.Reconciled);

        Assert.Equal(0, await CountAnnalClaimsAsync(1, memory));

        Assert.Equal(0, await CountTableRowsAsync("annal_versions"));

        Assert.Equal(0, await CountTableRowsAsync("annal_heads"));

    }

    private async Task ApplyUntargetedResetAsync(MemoryResetScope scope)
    {

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.ResetMemory, null, scope);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Core.Primitives.Result<DataRetentionApplyResult> applied = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.True(applied.Value.Reconciled);

    }

    /// <summary>Seeds one claim, one version, and one head over an existing durable row.</summary>
    private async Task SeedClaimAsync(int subjectStoreCode, string subjectId)
    {

        string claimId = Guid.NewGuid().ToString();

        string versionId = Guid.NewGuid().ToString();

        await ExecuteAsync(
            """
            INSERT INTO annal_claims (ClaimId, SubjectStoreCode, SubjectId, CreatedAtUtc)
            VALUES (@claimId, @storeCode, @subjectId, @at)
            """,
            ("@claimId", claimId),
            ("@storeCode", subjectStoreCode),
            ("@subjectId", subjectId),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO annal_versions (
                VersionId, ClaimId, Revision, OperationCode, OriginCode, ScopeKindCode, CampaignId,
                SensitivityCode, ContentHash, ValidFromUtc, ValidToUtc, RecordedAtUtc,
                PredecessorVersionId, SourceSessionId)
            VALUES (@versionId, @claimId, 1, 1, 4, 1, NULL, 0, zeroblob(32), @at, NULL, @at, NULL, NULL)
            """,
            ("@versionId", versionId),
            ("@claimId", claimId),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO annal_heads (
                ClaimId, SubjectStoreCode, CurrentVersionId, CurrentRevision, CurrentOperationCode, UpdatedAtUtc)
            VALUES (@claimId, @storeCode, @versionId, 1, 1, @at)
            """,
            ("@claimId", claimId),
            ("@storeCode", subjectStoreCode),
            ("@versionId", versionId),
            ("@at", OldTimestamp));

    }

    private async Task<string> SeedClaimedLexiconEntryAsync(string nameNormalized, string scopeCampaignId)
    {

        string id = Guid.NewGuid().ToString("N");

        await ExecuteAsync(
            """
            INSERT INTO lexicon_entries
                (Id, Name, NameNormalized, ScopeCampaignId, Type, FactsJson, FactsText, UpdatedAt)
            VALUES (@id, @name, @name, @scope, 'Concept', '[]', @name, @at)
            """,
            ("@id", id),
            ("@name", nameNormalized),
            ("@scope", scopeCampaignId),
            ("@at", OldTimestamp));

        await SeedClaimAsync(2, id);

        return id;

    }

    private Task<int> CountAnnalClaimsAsync(int subjectStoreCode, string subjectId) =>
        ScalarCountAsync(
            "SELECT COUNT(*) FROM annal_claims WHERE SubjectStoreCode = @storeCode AND SubjectId = @subjectId",
            ("@storeCode", subjectStoreCode),
            ("@subjectId", subjectId));

    private Task<int> CountTableRowsAsync(string table) =>
        ScalarCountAsync($"SELECT COUNT(*) FROM {table}");

    private async Task<int> ScalarCountAsync(string sql, params (string Name, object Value)[] parameters)
    {

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            _ = command.Parameters.AddWithValue(name, value);

        }

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);

    }

}
