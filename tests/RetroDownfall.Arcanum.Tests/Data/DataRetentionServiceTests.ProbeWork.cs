using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using SQLitePCL;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData("entries", 1, false)]
    [InlineData("entries", 8, false)]
    [InlineData("entries", 64, false)]
    [InlineData("entries-distinct", 64, false)]
    [InlineData("embeddings", 1, false)]
    [InlineData("embeddings", 8, false)]
    [InlineData("embeddings", 64, false)]
    [InlineData("sessions", 1, false)]
    [InlineData("sessions", 8, false)]
    [InlineData("sessions", 64, false)]
    [InlineData("entries", 8, true)]
    [InlineData("embeddings", 8, true)]
    [InlineData("sessions", 8, true)]
    public async Task ProbeWork_Native_statement_census_preserves_actual_plan_and_apply(
        string family,
        int count,
        bool apply)
    {
        RequireSqlCipher();

        List<string> expectedCandidates = [];

        if (family is "sessions" or "entries-distinct")
        {
            for (int index = 0; index < count; index++)
            {
                (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

                expectedCandidates.Add(family == "sessions" ? "session:" + sessionId.ToString("D") : "entry:" + entryId.ToString("D"));
            }
        }
        else
        {
            Guid[] entryIds = await SeedEntryEmbeddingBatchAsync(count);

            expectedCandidates.AddRange(entryIds.Select(id => family == "embeddings"
                ? "entry-embedding:" + Canonical(id)
                : "entry:" + id.ToString("D")));
        }

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = family.StartsWith("entries", StringComparison.Ordinal) ? EnabledRule() : DisabledRule();

        settings.Retention.SessionEntryEmbeddings = family == "embeddings" ? EnabledRule() : DisabledRule();

        settings.Retention.ArchivedSessions = family == "sessions" ? EnabledRule() : DisabledRule();

        var service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan? plan = null;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync();
        }

        await MeasureProbeWorkAsync(
            connection,
            family,
            count,
            "plan",
            async () => plan = await service.PlanAsync(request));

        Assert.NotNull(plan);

        Assert.Equal(count, plan.CandidateIds.Length);

        Assert.Equal(expectedCandidates.Order(StringComparer.Ordinal), plan.CandidateIds.Order(StringComparer.Ordinal));

        Assert.Equal(count, plan.CandidateIds.Distinct(StringComparer.Ordinal).Count());

        if (apply)
        {
            await MeasureProbeWorkAsync(
                connection,
                family,
                count,
                "apply",
                async () =>
                {
                    var result = await service.ApplyAsync(new(request, plan.PlanId));

                    Assert.True(result.IsSuccess, result.Error.Message);

                    Assert.True(result.Value.Reconciled);

                    Assert.True(family == "embeddings"
                        ? result.Value.DerivedRecordsDeleted >= count
                        : result.Value.RowsDeleted > 0);
                });

            Assert.Equal(0, await CountAllAsync(family == "embeddings" ? "entry_embeddings" : family == "sessions" ? "Sessions" : "Entries"));
        }
    }

    [SkippableTheory]
    [InlineData("entries", false)]
    [InlineData("embeddings", false)]
    [InlineData("sessions", false)]
    [InlineData("entries", true)]
    [InlineData("embeddings", true)]
    [InlineData("sessions", true)]
    public async Task ProbeWork_bounds_native_catalog_work(string family, bool apply)
    {
        RequireSqlCipher();

        List<string> expectedCandidates = [];

        int count = apply ? 8 : 64;

        if (family == "sessions")
        {
            for (int index = 0; index < count; index++)
            {
                (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

                expectedCandidates.Add(family == "sessions" ? "session:" + sessionId.ToString("D") : "entry:" + entryId.ToString("D"));
            }
        }
        else
        {
            Guid[] entryIds = await SeedEntryEmbeddingBatchAsync(count);

            expectedCandidates.AddRange(entryIds.Select(id => family == "embeddings"
                ? "entry-embedding:" + Canonical(id)
                : "entry:" + id.ToString("D")));
        }

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = family == "entries" ? EnabledRule() : DisabledRule();

        settings.Retention.SessionEntryEmbeddings = family == "embeddings" ? EnabledRule() : DisabledRule();

        settings.Retention.ArchivedSessions = family == "sessions" ? EnabledRule() : DisabledRule();

        var service = CreateService(settings);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await _db.Database.OpenConnectionAsync();

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan? plan = apply ? await service.PlanAsync(request) : null;

        long catalogSteps = 0;

        long outsideSteps = 0;

        bool insideTransaction = false;

        int catalogStatements = 0;

        bool readingCatalog = false;

        raw.sqlite3_trace(connection.Handle, (strdelegate_trace)((_, sql) =>
        {
            if (sql.StartsWith("--", StringComparison.Ordinal)) return;

            readingCatalog = sql.Contains("sqlite_master", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("sqlite_schema", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("schema_version", StringComparison.OrdinalIgnoreCase);

            insideTransaction = raw.sqlite3_get_autocommit(connection.Handle) == 0;

            if (readingCatalog) catalogStatements++;
        }), null);

        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) =>
        {
            if (readingCatalog)
            {
                catalogSteps++;

                if (!insideTransaction) outsideSteps++;
            }

            return 0;
        }, null);

        try
        {
            if (apply)
            {
                var result = await service.ApplyAsync(new(request, plan!.PlanId));

                Assert.True(result.IsSuccess, result.Error.Message);

                Assert.True(result.Value.Reconciled);

                Assert.Equal((family == "sessions" ? 2 : family == "entries" ? 1 : 0) * count, result.Value.RowsDeleted);

                Assert.Equal((family == "entries" ? 2 : 1) * count, result.Value.DerivedRecordsDeleted);
            }
            else
            {
                plan = await service.PlanAsync(request);
            }
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);

            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        Assert.NotNull(plan);

        Assert.Equal(count, plan.CandidateIds.Length);

        Assert.Equal(expectedCandidates.Order(StringComparer.Ordinal), plan.CandidateIds.Order(StringComparer.Ordinal));

        Assert.Equal(count, plan.CandidateIds.Distinct(StringComparer.Ordinal).Count());

        Console.WriteLine($"RV237 metadata family={family} phase={(apply ? "apply" : "plan")} candidates={count} statements={catalogStatements} vmSteps={catalogSteps} outsideVmSteps={outsideSteps} transactionVmSteps={catalogSteps - outsideSteps}");

        if (apply)
        {
            Assert.Equal(0, await CountAllAsync(family == "embeddings" ? "entry_embeddings" : family == "sessions" ? "Sessions" : "Entries"));

            Assert.Equal(family == "sessions" ? 0 : 1, await CountAllAsync("Sessions"));

            Assert.Equal(family == "embeddings" ? count : 0, await CountAllAsync("Entries"));
        }

        // Each candidate may still need fresh mutable probes, but schema classification must not
        // rescan the full catalog for every one of those probes. Count actual native work, including
        // the freshness checks, rather than rewarding a reduced number of expensive statements.
        // Each Session writer boundary invalidates the generic census. Bound the fresh metadata
        // phase per candidate rather than incorrectly requiring one cache across all transactions.
        long outsideBudget = family == "sessions" ? count * 32_000L : 50_000;

        Assert.True(apply ? outsideSteps < outsideBudget : catalogSteps < 200_000,
            $"{family}: catalog classification used {catalogSteps:N0} native VM steps, {outsideSteps:N0} outside transactions.");

        if (apply && family == "sessions")
        {
            Assert.True(catalogSteps - outsideSteps < 400_000,
                $"Session writer metadata used {catalogSteps - outsideSteps:N0} native VM steps.");
        }
    }

    private static async Task MeasureProbeWorkAsync(
        SqliteConnection connection,
        string family,
        int count,
        string phase,
        Func<Task> action)
    {
        long steps = 0;

        using SqliteStatementTrace trace = new(connection);

        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) =>
        {
            steps++;

            return 0;
        }, null);

        try
        {
            await action();
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        IReadOnlyList<string> statements = trace.Statements;

        int schema = statements.Count(static sql => sql.Contains("sqlite_master", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("sqlite_schema", StringComparison.OrdinalIgnoreCase));

        int pins = statements.Count(static sql => sql.Contains("SessionContextPins", StringComparison.Ordinal));

        int sessions = statements.Count(static sql => sql.Contains("SessionId", StringComparison.Ordinal)
            && (sql.Contains("InferenceRuns", StringComparison.Ordinal)
                || sql.Contains("BudgetReservations", StringComparison.Ordinal)
                || sql.Contains("LongRunningOperations", StringComparison.Ordinal)));

        int outsideSchema = 0;

        int deferredSchema = 0;

        int immediateSchema = 0;

        string transaction = "none";

        for (int index = 0; index < statements.Count; index++)
        {
            string sql = statements[index];

            if (sql.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                transaction = sql.Contains("IMMEDIATE", StringComparison.OrdinalIgnoreCase)
                    || sql.Contains("EXCLUSIVE", StringComparison.OrdinalIgnoreCase) ? "immediate" : "deferred";

                Console.WriteLine($"RV237 boundary family={family} count={count} phase={phase} index={index} kind={transaction}");
            }

            if (sql.Contains("sqlite_master", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("sqlite_schema", StringComparison.OrdinalIgnoreCase))
            {
                if (transaction == "immediate") immediateSchema++;
                else if (transaction == "deferred") deferredSchema++;
                else outsideSchema++;
            }

            if (sql.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)
                || sql.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"RV237 boundary family={family} count={count} phase={phase} index={index} kind=end-{transaction}");

                transaction = "none";
            }
        }

        Console.WriteLine($"RV237 family={family} count={count} phase={phase} statements={trace.Count} schema={schema} pinMentions={pins} sessionStateMentions={sessions} progressCallbacksAtOneIncludingTriggers={steps} immediate={trace.ImmediateTransactions} deferred={trace.DeferredTransactions} outsideSchema={outsideSchema} deferredSchema={deferredSchema} immediateSchema={immediateSchema}");

        foreach ((string shape, int calls) in trace.ByText().OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"RV237 shape calls={calls}: {shape.Replace('\n', ' ').Replace('\r', ' ')}");
        }

        Assert.True(trace.Count > 0);

        Assert.True(steps > 0);
    }
}
