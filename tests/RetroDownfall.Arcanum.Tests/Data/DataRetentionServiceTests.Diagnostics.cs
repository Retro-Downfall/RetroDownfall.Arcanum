using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false, 4_000)]
    [InlineData(true, 4_000)]
    public Task PlanAsync_Diagnostics_BoundsProtectedEntryDetailsWithoutLosingSurvivors(bool pinned, int count) =>
        VerifyProtectedEntryDiagnosticsAsync(pinned, count);

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PlanAsync_Diagnostics_LargeProtectedEntryDetails(bool pinned) =>
        VerifyProtectedEntryDiagnosticsAsync(pinned, 100_000);

    private async Task VerifyProtectedEntryDiagnosticsAsync(bool pinned, int count)
    {
        RequireSqlCipher();

        (Guid holder, _) = await SeedSessionAsync(pinned);

        (_, Guid survivor) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync("""
            WITH RECURSIVE rows(n) AS (SELECT 2 UNION ALL SELECT n+1 FROM rows WHERE n < @count)
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence,IsPinned)
            SELECT printf('%08X-0000-0000-0000-%012X',n,n),@holder,1,'test','test',@at,n,@pinned FROM rows;
            """, ("@count", count), ("@holder", Canonical(holder)), ("@at", OldTimestamp), ("@pinned", pinned ? 1 : 0));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        if (!pinned)
        {
            settings.Retention.ProtectedSessionIds = [holder];
        }

        DataRetentionPlan plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Contains("entry:" + survivor.ToString("D"), plan.CandidateIds);

        Assert.InRange(plan.Blockers.Length, 1, 20);

        string json = JsonSerializer.Serialize(plan, ArcanumJsonContext.Default.DataRetentionPlan);

        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement diagnostics = document.RootElement.GetProperty("diagnostics");

        Assert.Equal(count, diagnostics.GetProperty("blockerCount").GetInt64());

        Assert.Equal(count - 20, diagnostics.GetProperty("omittedBlockerCount").GetInt64());

        Assert.Equal(plan.GeneratedAt, diagnostics.GetProperty("observedAt").GetDateTimeOffset());

        Assert.Equal(1, diagnostics.GetProperty("holdingSessionCount").GetInt64());

        Assert.Equal(holder.ToString("D"), Assert.Single(diagnostics.GetProperty("holdingSessions").EnumerateArray()).GetProperty("sessionId").GetString());

        Assert.True(json.Length < 32_000, $"Protected-entry preview serialized {json.Length} characters.");

        var applied = await CreateService(settings).ApplyAsync(new(new(DataRetentionOperation.Prune), plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(0, await CountAsync("Entries", "Id", Canonical(survivor)));

        Assert.Equal(count, await CountAsync("Entries", "SessionId", Canonical(holder)));

        using JsonDocument appliedJson = JsonDocument.Parse(JsonSerializer.Serialize(applied.Value,
            ArcanumJsonContext.Default.DataRetentionApplyResult));

        Assert.Equal(count, appliedJson.RootElement.GetProperty("blockers").GetArrayLength());
    }

    [SkippableFact]
    public async Task PlanAsync_Diagnostics_BoundsRetainedAccountingAndIdentifiesItsHolder()
    {
        RequireSqlCipher();

        const int count = 4_000;

        (Guid holder, _) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync("""
            WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM rows WHERE n < @count)
            INSERT INTO InferenceRuns(Id,RequestId,SessionId,Surface,Purpose,StartedAt,CompletedAt,Status)
            SELECT printf('%032x',n),printf('request-%d',n),@holder,'test','test',@at,@at,1 FROM rows;
            """, ("@count", count), ("@holder", holder.ToString("D")), ("@at", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = EnabledRule();

        settings.Retention.AccountingMinimumDays = 1;

        DataRetentionPlan plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Empty(plan.CandidateIds);

        Assert.InRange(plan.Blockers.Length, 1, 20);

        string json = JsonSerializer.Serialize(plan, ArcanumJsonContext.Default.DataRetentionPlan);

        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement diagnostics = document.RootElement.GetProperty("diagnostics");

        Assert.Equal(count, diagnostics.GetProperty("blockerCount").GetInt64());

        Assert.Equal(holder.ToString("D"), Assert.Single(diagnostics.GetProperty("holdingSessions").EnumerateArray()).GetProperty("sessionId").GetString());

        Assert.True(json.Length < 32_000, $"Retained-accounting preview serialized {json.Length} characters.");
    }

    [SkippableFact]
    public async Task PlanAsync_Diagnostics_AsksEachHoldingSessionAboutConflictsOnce()
    {
        RequireSqlCipher();

        (Guid holder, Guid entry) = await SeedSessionAsync(pinned: false);

        _ = await SeedAttachmentAsync(holder, entry);

        await ExecuteAsync("""
            WITH RECURSIVE rows(n) AS (SELECT 2 UNION ALL SELECT n+1 FROM rows WHERE n < 64)
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence,IsPinned)
            SELECT printf('%08X-0000-0000-0000-%012X',n,n),@holder,1,'test','test',@at,n,0 FROM rows;
            """, ("@holder", Canonical(holder)), ("@at", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        settings.Retention.Attachments = EnabledRule();

        settings.Retention.ProtectedSessionIds = [holder];

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        List<string> statements = [];

        raw.sqlite3_trace(connection.Handle, (object _, string sql) => statements.Add(sql), null);

        try
        {
            _ = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        Assert.Single(statements, static sql => sql.Contains("AS ConflictOrder", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task PlanAsync_Diagnostics_HoldingSessionsHaveOneGlobalBoundAndExactTotal()
    {
        RequireSqlCipher();

        List<Guid> holders = [];

        for (int index = 0; index < 32; index++)
        {
            (Guid holder, _) = await SeedSessionAsync(pinned: false);

            holders.Add(holder);
        }

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        settings.Retention.ProtectedSessionIds = [.. holders];

        DataRetentionPlan plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Equal(32, plan.Diagnostics!.BlockerCount);

        Assert.Equal(32, plan.Diagnostics.HoldingSessionCount);

        Assert.Equal(20, plan.Diagnostics.HoldingSessions.Length);

        Assert.Equal(12, plan.Diagnostics.OmittedHoldingSessionCount);

        Assert.Equal(12, plan.Diagnostics.OmittedBlockerCount);

        Assert.Equal(20, plan.Blockers.Length);

        Assert.False(plan.Diagnostics.DetailsComplete);
    }

    [SkippableFact]
    public async Task PlanAsync_Diagnostics_ContextPinsNeverExposeSubmittedTarget()
    {
        RequireSqlCipher();

        (Guid holder, _) = await SeedSessionAsync(pinned: false);

        const string privateTarget = "PRIVATE_CONTEXT_TARGET_";

        await ExecuteAsync("""
            INSERT INTO SessionContextPins(Id,SessionId,Kind,TargetIdentifier,DisplayLabel,CreatedAt,UpdatedAt)
            VALUES(@pin,@holder,0,@target,'test',@at,@at);
            """, ("@pin", Guid.NewGuid().ToString("D")), ("@holder", Canonical(holder)),
            ("@target", privateTarget + new string('x', 100_000)), ("@at", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.ArchivedSessions = EnabledRule();

        DataRetentionPlan plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));

        string json = JsonSerializer.Serialize(plan, ArcanumJsonContext.Default.DataRetentionPlan);

        Assert.DoesNotContain(privateTarget, json, StringComparison.Ordinal);

        Assert.Contains(plan.Blockers, static blocker => blocker.ReasonCode == "Data.ContextPin");

        Assert.True(json.Length < 32_000);
    }

    [SkippableFact]
    public async Task PlanAsync_Diagnostics_ProtectedSessionDoesNotHydrateDeletionSnapshots()
    {
        RequireSqlCipher();

        (Guid holder, _) = await SeedSessionAsync(pinned: false);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.ArchivedSessions = EnabledRule();

        settings.Retention.ProtectedSessionIds = [holder];

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        List<string> statements = [];

        raw.sqlite3_trace(connection.Handle, (object _, string sql) => statements.Add(sql), null);

        DataRetentionPlan plan;

        try
        {
            plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        Assert.Empty(plan.CandidateIds);

        Assert.Contains(plan.Blockers, static blocker => blocker.ReasonCode == "Data.SessionHold");

        Assert.DoesNotContain(statements, static sql => sql.Contains("SELECT Id, IsPinned, rowid FROM Entries", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task PlanAsync_Diagnostics_HiddenHoldingSessionMoveChangesTheDiagnosticSnapshot()
    {
        RequireSqlCipher();

        (Guid first, _) = await SeedSessionAsync(pinned: false);

        (Guid second, _) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync("""
            WITH RECURSIVE rows(n) AS (SELECT 2 UNION ALL SELECT n+1 FROM rows WHERE n < 32)
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence,IsPinned)
            SELECT printf('%08X-0000-0000-0000-%012X',n,n),@holder,1,'test','test',@at,n,0 FROM rows;
            """, ("@holder", Canonical(first)), ("@at", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        settings.Retention.ProtectedSessionIds = [first, second];

        RetroDownfall.Arcanum.Tests.Support.FakeTimeProvider clock = new();

        DataRetentionService service = CreateService(settings, timeProvider: clock);

        DataRetentionPlan before = await service.PlanAsync(new(DataRetentionOperation.Prune));

        await ExecuteAsync("""
            UPDATE Entries SET SessionId=@holder,Sequence=1001
            WHERE Id='00000020-0000-0000-0000-000000000020';
            """, ("@holder", Canonical(second)));

        DataRetentionPlan after = await service.PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Equal(before.PlanId, after.PlanId);

        Assert.Equal(before.Blockers, after.Blockers);

        Assert.Equal(before.Diagnostics!.BlockerCount, after.Diagnostics!.BlockerCount);

        Assert.NotEqual(before.Diagnostics.SnapshotId, after.Diagnostics.SnapshotId);
    }
}
