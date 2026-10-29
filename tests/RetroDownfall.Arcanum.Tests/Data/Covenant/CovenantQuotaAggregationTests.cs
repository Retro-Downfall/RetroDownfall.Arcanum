using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using SQLitePCL;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed class CovenantQuotaAggregationTests(ITestOutputHelper output)
{
    private static CancellationToken Token => CancellationToken.None;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_versions_are_counted_with_one_bounded_pass(bool campaignScoped)
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid? campaign = campaignScoped ? CovenantOperationGateFixture.CampaignOne : null;

        if (campaign.HasValue)
        {
            await fixture.AddCampaignAsync(campaign.Value, "measured", Token);
        }

        CovenantScope scope = campaignScoped ? CovenantScope.Campaign : CovenantScope.Global;

        SeededHead head = await fixture.SeedHeadAsync(scope, campaign, "measured.key",
            CovenantLane.Confirmed, CovenantOperation.Set, "Same content.", Token);

        var small = Measure(fixture.Connection, campaign, []);

        for (int revision = 2; revision <= 65; revision++)
        {
            head = await fixture.SeedHeadAsync(scope, campaign, "measured.key", CovenantLane.Confirmed,
                CovenantOperation.Set, "Same content.", Token, entryId: head.EntryId,
                laneRevision: revision, predecessorVersionId: head.VersionId);
        }

        var large = Measure(fixture.Connection, campaign, []);

        Assert.Equal(1, large.Values[0]);

        Assert.Equal(65, large.Values[1]);

        Assert.Equal(65, large.Values[2]);

        Assert.Equal(small.Values[3] * 65, large.Values[3]);

        Assert.Equal(0, large.Values[4]);

        Assert.Equal(0, large.Values[5]);

        output.WriteLine($"VM small={small.Vm}; large={large.Vm}; growth={large.Vm - small.Vm}");

        // Native instruction work, independent of scheduler/clock noise. Four redundant history
        // joins breach this per-record budget even when every returned counter remains correct.
        Assert.InRange(large.Vm - small.Vm, 1, 64 * 30);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_scope_returns_one_complete_zero_row(bool campaignScoped)
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Assert.Equal(new long[10], Measure(fixture.Connection,
            campaignScoped ? CovenantOperationGateFixture.CampaignOne : null, []).Values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_and_provenance_survive_head_exclusion_in_both_consumers(bool campaignScoped)
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid campaign = CovenantOperationGateFixture.CampaignOne;

        await fixture.AddCampaignAsync(campaign, "target-or-noise", Token);

        CovenantScope scope = campaignScoped ? CovenantScope.Campaign : CovenantScope.Global;

        Guid? owner = campaignScoped ? campaign : null;

        (CovenantLane Lane, CovenantOrigin Origin)[] authors = campaignScoped
            ? [(CovenantLane.Confirmed, CovenantOrigin.Operator), (CovenantLane.Proposed, CovenantOrigin.Operator),
               (CovenantLane.Proposed, CovenantOrigin.AgentProposed), (CovenantLane.Confirmed, CovenantOrigin.AgentApproved),
               (CovenantLane.Proposed, CovenantOrigin.AgentApproved)]
            : [(CovenantLane.Confirmed, CovenantOrigin.Operator)];

        long bytes = 0, agentBytes = 0;

        for (int index = 0; index < authors.Length; index++)
        {
            var (lane, origin) = authors[index];

            string key = $"historical.key{index}";

            SeededHead set = await fixture.SeedHeadAsync(scope, owner, key, lane, CovenantOperation.Set,
                "Historical content.", Token, origin: origin,
                sources: [Source(1), Source(2)]);

            long cost = System.Text.Encoding.UTF8.GetByteCount(set.Compiled!.Fragment);

            bytes += cost;

            if (origin != CovenantOrigin.Operator) agentBytes += cost;

            _ = await fixture.SeedHeadAsync(scope, owner, key, lane, CovenantOperation.Retire, null, Token,
                origin: origin, entryId: set.EntryId, laneRevision: 2, predecessorVersionId: set.VersionId);
        }

        SeededHead active = await fixture.SeedHeadAsync(scope, owner, "touched.key", CovenantLane.Confirmed,
            CovenantOperation.Set, "Live content.", Token);

        bytes += System.Text.Encoding.UTF8.GetByteCount(active.Compiled!.Fragment);

        _ = await fixture.SeedHeadAsync(campaignScoped ? CovenantScope.Global : CovenantScope.Campaign,
            campaignScoped ? null : campaign, "outside.scope", CovenantLane.Confirmed,
            CovenantOperation.Set, "Other scope.", Token);

        for (int count = 0; count <= 5; count++)
        {
            string[] excluded = Enumerable.Range(0, count).Select(i => i == 0 ? "touched.key" : $"absent.key{i}").ToArray();

            long live = count == 0 ? 1 : 0;

            // Counts are hand-derived from the five legal Campaign author/lane pairs (one Global
            // pair), each with a Set and Retire, plus one live operator Set and one other-scope row.
            long[] expected = [live, campaignScoped ? 11 : 3, campaignScoped ? 6 : 2, bytes,
                campaignScoped ? 6 : 0, agentBytes, 0, campaignScoped ? 10 : 2,
                campaignScoped ? 12 : 4, live + 1];

            Assert.Equal(expected, Measure(fixture.Connection, owner, excluded).Values);

            CovenantOperationScope operationScope = campaignScoped
                ? CovenantOperationScope.ForCampaign(campaign) : CovenantOperationScope.Global;

            var result = await CovenantCapacityFixture.InTransactionAsync(fixture,
                transaction => new CovenantQuotaGuard().CheckCanonicalCapacityAsync(operationScope,
                    new CovenantQuotaDemand(0, 0, 0, 0, 0, 0, 0, 0, 0,
                        [new CovenantSectionDemand(CovenantLane.Confirmed, [.. excluded], 0, 0, 0)]), transaction, Token).AsTask(),
                Token, commit: false);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            CovenantQuotaSnapshot snapshot = result.Value;

            Assert.Equal(expected, new long[] { snapshot.ActiveEntriesInScope, snapshot.VersionsInScope,
                snapshot.SetVersionsInScope, snapshot.CanonicalBytesInScope, snapshot.AgentVersionsInCampaign,
                snapshot.AgentBytesInCampaign, snapshot.MutationReceiptsInScope, snapshot.ProvenanceRowsInCampaign,
                snapshot.PendingOutboxRows, snapshot.ActiveEntriesInWidestTurnLoad });
        }

        var gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantInstallationReadLease lease = (await gate.AcquireInstallationReadAsync(Token)).Value;

        var preview = await fixture.Store.ReadMutationPreflightSnapshotAsync(new(
            campaignScoped ? CovenantOperationScope.ForCampaign(campaign) : CovenantOperationScope.Global,
            "touched.key", CovenantLane.Confirmed, CovenantOperation.Set), lease, Token);

        Assert.True(preview.IsSuccess, preview.IsFailure ? preview.Error.Message : null);

        CovenantQuotaSnapshot readSnapshot = preview.Value.Quota;

        Assert.Equal(Measure(fixture.Connection, owner, ["touched.key"]).Values,
            new long[] { readSnapshot.ActiveEntriesInScope, readSnapshot.VersionsInScope,
                readSnapshot.SetVersionsInScope, readSnapshot.CanonicalBytesInScope, readSnapshot.AgentVersionsInCampaign,
                readSnapshot.AgentBytesInCampaign, readSnapshot.MutationReceiptsInScope, readSnapshot.ProvenanceRowsInCampaign,
                readSnapshot.PendingOutboxRows, readSnapshot.ActiveEntriesInWidestTurnLoad });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_history_preserves_independent_scalar_counters(bool campaignScoped)
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await CreateScalarContractTablesAsync(fixture.Connection);

        await using SqliteCommand seed = fixture.Connection.CreateCommand();

        seed.CommandText = """
            INSERT INTO covenant_heads VALUES (NULL, 1, 'global.key'), ($campaign, 1, 'campaign.key');
            INSERT INTO covenant_mutation_receipts VALUES (1, NULL), (2, $campaign);
            INSERT INTO covenant_search_outbox VALUES (1), (2);
            """;

        _ = seed.Parameters.AddWithValue("$campaign", CovenantOperationGateFixture.CampaignOne.ToString("D"));

        _ = await seed.ExecuteNonQueryAsync(Token);

        Assert.Equal(new long[] { 1, 0, 0, 0, 0, 0, 1, 0, 2, 2 }, Measure(fixture.Connection,
            campaignScoped ? CovenantOperationGateFixture.CampaignOne : null, []).Values);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Conditional_count_does_not_depend_on_nullable_byte_values(int origin)
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await CreateScalarContractTablesAsync(fixture.Connection);

        await using SqliteCommand seed = fixture.Connection.CreateCommand();

        seed.CommandText = "INSERT INTO covenant_entries VALUES ('entry', NULL); INSERT INTO covenant_versions VALUES ('v', 'entry', 1, $origin, NULL);";

        _ = seed.Parameters.AddWithValue("$origin", origin);

        _ = await seed.ExecuteNonQueryAsync(Token);

        Assert.Equal(new long[] { 0, 1, 1, 0, 1, 0, 0, 0, 0, 0 }, Measure(fixture.Connection, null, []).Values);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Integer_sum_overflow_remains_a_sqlite_refusal(int origin)
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await CreateScalarContractTablesAsync(fixture.Connection);

        await using SqliteCommand seed = fixture.Connection.CreateCommand();

        seed.CommandText = """
            INSERT INTO covenant_entries VALUES ('entry', NULL);
            INSERT INTO covenant_versions VALUES ('v1', 'entry', 1, $origin, 9223372036854775807);
            INSERT INTO covenant_versions VALUES ('v2', 'entry', 1, $origin, 1);
            """;

        _ = seed.Parameters.AddWithValue("$origin", origin);

        _ = await seed.ExecuteNonQueryAsync(Token);

        await using SqliteCommand read = fixture.Connection.CreateCommand();

        read.CommandText = CovenantStoreSql.QuotaSnapshot(false, 0);

        SqliteException error = await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using SqliteDataReader reader = await read.ExecuteReaderAsync(Token);

            while (await reader.ReadAsync(Token)) { }
        });

        Assert.Equal(raw.SQLITE_ERROR, error.SqliteErrorCode);

        Assert.Contains("integer overflow", error.Message, StringComparison.Ordinal);
    }

    private static async Task CreateScalarContractTablesAsync(SqliteConnection connection)
    {
        // TEMP tables isolate the aggregate's SQL scalar contract, including values that the real
        // canonical schema rejects. They do not relax a persisted constraint or a mutation guard.
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            CREATE TEMP TABLE covenant_entries (EntryId TEXT, CampaignId TEXT);
            CREATE TEMP TABLE covenant_versions (VersionId TEXT, EntryId TEXT, OperationCode INTEGER, OriginCode INTEGER, CompiledByteCost INTEGER);
            CREATE TEMP TABLE covenant_heads (CampaignId TEXT, CurrentOperationCode INTEGER, NormalizedKey TEXT);
            CREATE TEMP TABLE covenant_mutation_receipts (ScopeCode INTEGER, CampaignId TEXT);
            CREATE TEMP TABLE covenant_version_attachment_provenance (VersionId TEXT);
            CREATE TEMP TABLE covenant_search_outbox (Id INTEGER);
            """;

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    private static MaterializationSourceDigestInput Source(byte ordinal) => new(Guid.NewGuid(), Guid.NewGuid(),
        $"source/{ordinal}", CovenantOperationGateFixture.Digest(ordinal), CovenantMaterializationSourceRange.WholeSource,
        null, null, []);

    private static (long[] Values, int Vm) Measure(SqliteConnection connection, Guid? campaign, string[] excluded)
    {
        string sql = CovenantStoreSql.QuotaSnapshot(campaign.HasValue, excluded.Length);

        Assert.Equal(raw.SQLITE_OK, raw.sqlite3_prepare_v2(connection.Handle, sql, out sqlite3_stmt statement));

        try
        {
            if (campaign.HasValue)
            {
                Assert.Equal(raw.SQLITE_OK, raw.sqlite3_bind_text(statement,
                    raw.sqlite3_bind_parameter_index(statement, "$campaign"), campaign.Value.ToString("D")));
            }

            for (int index = 0; index < excluded.Length; index++)
            {
                Assert.Equal(raw.SQLITE_OK, raw.sqlite3_bind_text(statement,
                    raw.sqlite3_bind_parameter_index(statement, $"$xkey{index}"), excluded[index]));
            }

            Assert.Equal(raw.SQLITE_ROW, raw.sqlite3_step(statement));

            Assert.Equal(10, raw.sqlite3_column_count(statement));

            long[] values = new long[10];

            for (int index = 0; index < values.Length; index++)
            {
                Assert.Equal(raw.SQLITE_INTEGER, raw.sqlite3_column_type(statement, index));

                values[index] = raw.sqlite3_column_int64(statement, index);
            }

            Assert.Equal(raw.SQLITE_DONE, raw.sqlite3_step(statement));

            return (values, raw.sqlite3_stmt_status(statement, raw.SQLITE_STMTSTATUS_VM_STEP, 0));
        }
        finally
        {
            raw.sqlite3_finalize(statement);
        }
    }
}
