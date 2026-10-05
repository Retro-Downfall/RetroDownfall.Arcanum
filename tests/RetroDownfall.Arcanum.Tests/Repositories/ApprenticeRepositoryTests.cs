using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Repositories;

[Collection("Grimoire")]
public sealed class ApprenticeRepositoryTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public ApprenticeRepositoryTests(GrimoireFixture fixture)
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
    public async Task AddAsync_GetByIdAsync_hydrates_parent_from_checkpoint()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApprenticeRepository repository = new(_db!, NullLogger<ApprenticeRepository>.Instance);

        Guid parentId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Apprentice apprentice = new()
        {
            Id = Guid.NewGuid(),
            Name = "Scribe",
            Goal = "Catalog spells",
            Plan = "[]",
            CurrentStep = 2,
            Status = ApprenticeStatus.Running.ToString(),
            WorkspacePath = "/tmp/workspace",
            CheckpointData = ApprenticeRepository.SerializeCheckpoint(
                new ApprenticeCheckpoint
                {
                    CurrentStep = 2,
                    ParentApprenticeId = parentId,
                    Timestamp = now,
                }),
            ErrorMessage = "Recoverable warning",
            CreatedAt = now,
            UpdatedAt = now,
        };

        Apprentice saved = await repository.AddAsync(apprentice, CancellationToken.None);

        Apprentice? loaded = await repository.GetByIdAsync(saved.Id, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(parentId, loaded!.ParentApprenticeId);

        Assert.Equal(apprentice.Name, loaded.Name);

        Assert.Equal(apprentice.Goal, loaded.Goal);

        Assert.Equal(apprentice.Plan, loaded.Plan);

        Assert.Equal(apprentice.CurrentStep, loaded.CurrentStep);

        Assert.Equal(apprentice.Status, loaded.Status);

        Assert.Equal(apprentice.WorkspacePath, loaded.WorkspacePath);

        Assert.Equal(apprentice.CheckpointData, loaded.CheckpointData);

        Assert.Equal(apprentice.ErrorMessage, loaded.ErrorMessage);

        Assert.Equal(apprentice.CreatedAt, loaded.CreatedAt);

        Assert.Equal(apprentice.UpdatedAt, loaded.UpdatedAt);
    }

    [SkippableFact]
    public async Task GetResumableAsync_UpdateAsync_and_DeleteAsync_manage_apprentices()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApprenticeRepository repository = new(_db!, NullLogger<ApprenticeRepository>.Instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Apprentice running = await repository.AddAsync(
            new Apprentice
            {
                Id = Guid.NewGuid(),
                Name = "Runner",
                Goal = "Run",
                Status = ApprenticeStatus.Running.ToString(),
                WorkspacePath = "/tmp/run",
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        Apprentice idle = await repository.AddAsync(
            new Apprentice
            {
                Id = Guid.NewGuid(),
                Name = "Idle",
                Goal = "Wait",
                Status = ApprenticeStatus.Idle.ToString(),
                WorkspacePath = "/tmp/idle",
                CreatedAt = now,
                UpdatedAt = now.AddMinutes(-5),
            },
            CancellationToken.None);

        Apprentice launchRequested = await repository.AddAsync(
            new Apprentice
            {
                Id = Guid.NewGuid(),
                Name = "Crash-window child",
                Goal = "Launch after restart",
                Status = ApprenticeStatus.Idle.ToString(),
                WorkspacePath = "/tmp/recover-launch",
                CheckpointData = ApprenticeRepository.SerializeCheckpoint(new ApprenticeCheckpoint
                {
                    ParentApprenticeId = Guid.NewGuid(),
                    LaunchRequested = true,
                }),
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        IReadOnlyList<Apprentice> resumable = await repository.GetResumableAsync(CancellationToken.None);

        Assert.Contains(resumable, a => a.Id == running.Id);

        Assert.DoesNotContain(resumable, a => a.Id == idle.Id);

        Assert.Contains(resumable, a => a.Id == launchRequested.Id);

        Apprentice planning = await repository.AddAsync(
            new Apprentice
            {
                Id = Guid.NewGuid(),
                Name = "Planner",
                Goal = "Plan",
                Status = ApprenticeStatus.Planning.ToString(),
                Plan = ApprenticeRepository.SerializePlan([]),
                WorkspacePath = "/tmp/plan",
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        resumable = await repository.GetResumableAsync(CancellationToken.None);

        Assert.Contains(resumable, a => a.Id == planning.Id);

        Apprentice queuedRestart = await repository.AddAsync(
            new Apprentice
            {
                Id = Guid.NewGuid(),
                Name = "Queued restart",
                Goal = "Run the plan it already has",
                Status = ApprenticeStatus.Planning.ToString(),
                Plan = ApprenticeRepository.SerializePlan([new PlanStep { Index = 0, Description = "Known step" }]),
                WorkspacePath = "/tmp/queued-restart",
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        resumable = await repository.GetResumableAsync(CancellationToken.None);

        Assert.Contains(resumable, a => a.Id == queuedRestart.Id);

        running.Status = ApprenticeStatus.Completed.ToString();

        await repository.UpdateAsync(running, CancellationToken.None);

        Apprentice? updated = await repository.GetByIdAsync(running.Id, CancellationToken.None);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), updated!.Status);

        bool deleted = await repository.DeleteAsync(idle.Id, CancellationToken.None);

        Assert.True(deleted);

        Assert.Null(await repository.GetByIdAsync(idle.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task UpdateProgressAsync_writes_execution_columns_and_leaves_an_operator_status_alone()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApprenticeRepository repository = new(_db!, NullLogger<ApprenticeRepository>.Instance);

        Guid sessionId = await AddSessionAsync();

        Apprentice stored = await repository.AddAsync(
            NewApprentice(ApprenticeStatus.Paused, currentStep: 0, errorMessage: "operator paused"),
            CancellationToken.None);

        Apprentice progressed = CopyOf(stored);

        progressed.Status = ApprenticeStatus.Running.ToString();

        progressed.ErrorMessage = null;

        progressed.Plan = ApprenticeRepository.SerializePlan([new PlanStep { Index = 0, Status = "completed" }]);

        progressed.CurrentStep = 1;

        progressed.SessionId = sessionId;

        progressed.CheckpointData = ApprenticeRepository.SerializeCheckpoint(new ApprenticeCheckpoint { CurrentStep = 1 });

        Assert.True(await repository.UpdateProgressAsync(progressed, CancellationToken.None));

        Apprentice loaded = (await repository.GetByIdAsync(stored.Id, CancellationToken.None))!;

        Assert.Equal(ApprenticeStatus.Paused.ToString(), loaded.Status);

        Assert.Equal("operator paused", loaded.ErrorMessage);

        Assert.Equal(progressed.Plan, loaded.Plan);

        Assert.Equal(1, loaded.CurrentStep);

        Assert.Equal(sessionId, loaded.SessionId);

        Assert.Equal(progressed.CheckpointData, loaded.CheckpointData);

        Assert.False(await repository.UpdateProgressAsync(
            NewApprentice(ApprenticeStatus.Running, currentStep: 0),
            CancellationToken.None));
    }

    [SkippableFact]
    public async Task TryUpdateAsync_writes_only_while_status_and_current_step_are_unchanged()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApprenticeRepository repository = new(_db!, NullLogger<ApprenticeRepository>.Instance);

        Apprentice stored = await repository.AddAsync(
            NewApprentice(ApprenticeStatus.Cancelled, currentStep: 1),
            CancellationToken.None);

        Apprentice failed = CopyOf(stored);

        failed.Status = ApprenticeStatus.Failed.ToString();

        failed.ErrorMessage = "step failed";

        string[] executing = [ApprenticeStatus.Running.ToString(), ApprenticeStatus.Planning.ToString()];

        Assert.False(await repository.TryUpdateAsync(failed, executing, 1, CancellationToken.None));

        Assert.False(await repository.TryUpdateAsync(
            failed,
            [ApprenticeStatus.Cancelled.ToString()],
            0,
            CancellationToken.None));

        Apprentice unchanged = (await repository.GetByIdAsync(stored.Id, CancellationToken.None))!;

        Assert.Equal(ApprenticeStatus.Cancelled.ToString(), unchanged.Status);

        Assert.Null(unchanged.ErrorMessage);

        Assert.True(await repository.TryUpdateAsync(
            failed,
            [ApprenticeStatus.Cancelled.ToString()],
            1,
            CancellationToken.None));

        Apprentice written = (await repository.GetByIdAsync(stored.Id, CancellationToken.None))!;

        Assert.Equal(ApprenticeStatus.Failed.ToString(), written.Status);

        Assert.Equal("step failed", written.ErrorMessage);
    }

    [SkippableFact]
    public async Task TryUpdateStatusAsync_sets_only_the_status_and_only_from_an_expected_status()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApprenticeRepository repository = new(_db!, NullLogger<ApprenticeRepository>.Instance);

        Apprentice stored = await repository.AddAsync(
            NewApprentice(ApprenticeStatus.Running, currentStep: 0),
            CancellationToken.None);

        // The execution commits a step after an operator's snapshot of the row was taken.
        Apprentice progressed = CopyOf(stored);

        progressed.CurrentStep = 1;

        Assert.True(await repository.UpdateProgressAsync(progressed, CancellationToken.None));

        Assert.False(await repository.TryUpdateStatusAsync(
            stored.Id,
            ApprenticeStatus.Paused.ToString(),
            [ApprenticeStatus.Escalated.ToString()],
            CancellationToken.None));

        Assert.True(await repository.TryUpdateStatusAsync(
            stored.Id,
            ApprenticeStatus.Paused.ToString(),
            [ApprenticeStatus.Running.ToString(), ApprenticeStatus.Planning.ToString()],
            CancellationToken.None));

        Apprentice paused = (await repository.GetByIdAsync(stored.Id, CancellationToken.None))!;

        Assert.Equal(ApprenticeStatus.Paused.ToString(), paused.Status);

        Assert.Equal(1, paused.CurrentStep);

        Assert.False(await repository.TryUpdateStatusAsync(
            Guid.NewGuid(),
            ApprenticeStatus.Paused.ToString(),
            [ApprenticeStatus.Running.ToString()],
            CancellationToken.None));
    }

    private static Apprentice NewApprentice(
        ApprenticeStatus status,
        int currentStep,
        string? errorMessage = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return new Apprentice
        {
            Id = Guid.NewGuid(),
            Name = "Conditional writer",
            Goal = "Keep concurrent writers from reverting each other",
            Plan = ApprenticeRepository.SerializePlan([new PlanStep { Index = 0 }]),
            CurrentStep = currentStep,
            Status = status.ToString(),
            WorkspacePath = "/tmp/conditional",
            ErrorMessage = errorMessage,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static Apprentice CopyOf(Apprentice source) => new()
    {
        Id = source.Id,
        CampaignId = source.CampaignId,
        Name = source.Name,
        Goal = source.Goal,
        Plan = source.Plan,
        CurrentStep = source.CurrentStep,
        Status = source.Status,
        SessionId = source.SessionId,
        WorkspacePath = source.WorkspacePath,
        CheckpointData = source.CheckpointData,
        ErrorMessage = source.ErrorMessage,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
    };

    private async Task<Guid> AddSessionAsync()
    {
        Session session = new()
        {
            Id = Guid.NewGuid(),
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db!.Sessions.Add(session);

        await _db.SaveChangesAsync();

        return session.Id;
    }

    [SkippableFact]
    public async Task GetResumableAsync_filters_unrequested_idle_rows_before_materialization()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApprenticeRepository repository = new(_db!, NullLogger<ApprenticeRepository>.Instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Apprentice running = await repository.AddAsync(
            new Apprentice
            {
                Id = Guid.NewGuid(),
                Name = "Runner",
                Goal = "Run",
                Status = ApprenticeStatus.Running.ToString(),
                WorkspacePath = "/tmp/run",
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db!,
            """
            INSERT INTO "Apprentices" (
                "Id", "Name", "Goal", "Plan", "CurrentStep", "Status", "WorkspacePath", "CreatedAt", "UpdatedAt")
            VALUES (
                'not-a-guid', 'Dormant', 'Wait', '[]', 0, 'Idle', '/tmp/dormant', $createdAt, $updatedAt);
            """,
            CancellationToken.None);

        GrimoireEntitySql.AddParameter(command, "$createdAt", UtcInstantText.Format(now));
        GrimoireEntitySql.AddParameter(command, "$updatedAt", UtcInstantText.Format(now));

        await command.ExecuteNonQueryAsync(CancellationToken.None);

        IReadOnlyList<Apprentice> resumable = await repository.GetResumableAsync(CancellationToken.None);

        Apprentice only = Assert.Single(resumable);

        Assert.Equal(running.Id, only.Id);
    }

    [SkippableFact]
    public async Task ListAsync_applies_each_optional_filter_combination()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid firstCampaignId = Guid.NewGuid();
        Guid secondCampaignId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _db!.Campaigns.AddRange(
            Campaign(firstCampaignId, "First", "/tmp/first", now),
            Campaign(secondCampaignId, "Second", "/tmp/second", now));
        _ = await _db.SaveChangesAsync(CancellationToken.None);

        ApprenticeRepository repository = new(_db, NullLogger<ApprenticeRepository>.Instance);
        Apprentice firstRunning = await repository.AddAsync(
            Apprentice(firstCampaignId, "First running", ApprenticeStatus.Running, now),
            CancellationToken.None);
        Apprentice firstIdle = await repository.AddAsync(
            Apprentice(firstCampaignId, "First idle", ApprenticeStatus.Idle, now),
            CancellationToken.None);
        Apprentice secondRunning = await repository.AddAsync(
            Apprentice(secondCampaignId, "Second running", ApprenticeStatus.Running, now),
            CancellationToken.None);
        Apprentice secondIdle = await repository.AddAsync(
            Apprentice(secondCampaignId, "Second idle", ApprenticeStatus.Idle, now),
            CancellationToken.None);

        ListPageResult<Apprentice> unfiltered = await repository.ListAsync(
            null,
            null,
            limit: 10,
            cancellationToken: CancellationToken.None);
        ListPageResult<Apprentice> campaignOnly = await repository.ListAsync(
            firstCampaignId,
            null,
            limit: 10,
            cancellationToken: CancellationToken.None);
        ListPageResult<Apprentice> statusOnly = await repository.ListAsync(
            null,
            " Running ",
            limit: 10,
            cancellationToken: CancellationToken.None);
        ListPageResult<Apprentice> campaignAndStatus = await repository.ListAsync(
            firstCampaignId,
            " Running ",
            limit: 10,
            cancellationToken: CancellationToken.None);

        Assert.Equal(
            new[] { firstRunning.Id, firstIdle.Id, secondRunning.Id, secondIdle.Id }.Order(),
            unfiltered.Items.Select(static item => item.Id).Order());
        Assert.Equal(
            new[] { firstRunning.Id, firstIdle.Id }.Order(),
            campaignOnly.Items.Select(static item => item.Id).Order());
        Assert.Equal(
            new[] { firstRunning.Id, secondRunning.Id }.Order(),
            statusOnly.Items.Select(static item => item.Id).Order());
        Assert.Equal([firstRunning.Id], campaignAndStatus.Items.Select(static item => item.Id));
    }

    /// <summary>
    /// A Conclave fan-out creates a batch of child Apprentices inside one clock tick, so ties on
    /// <c>UpdatedAt</c> are ordinary rather than exotic. The paging cursor is a bare timestamp consumed
    /// as a strict <c>&lt;</c>, so a tie straddling a page boundary must not strand the rows sharing
    /// that timestamp: walking every page has to yield every Apprentice exactly once.
    /// </summary>
    [SkippableFact]
    public async Task ListAsync_paging_yields_every_apprentice_when_a_tie_straddles_the_boundary()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApprenticeRepository repository = new(_db!, NullLogger<ApprenticeRepository>.Instance);

        DateTimeOffset newest = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        // Page size is 2. The three tied rows cannot fit in one page, so the boundary necessarily
        // falls inside the tie group whichever way the page is cut.
        DateTimeOffset[] stamps =
        [
            newest,
            newest.AddMinutes(-1),
            newest.AddMinutes(-1),
            newest.AddMinutes(-1),
            newest.AddMinutes(-2),
        ];

        HashSet<Guid> expected = [];

        foreach (DateTimeOffset stamp in stamps)
        {
            Apprentice created = await repository.AddAsync(
                new Apprentice
                {
                    Id = Guid.NewGuid(),
                    Name = "Cohort",
                    Goal = "Tied timestamps",
                    Status = ApprenticeStatus.Idle.ToString(),
                    WorkspacePath = "/tmp/workspace",
                    CreatedAt = stamp,
                    UpdatedAt = stamp,
                },
                CancellationToken.None);

            _ = expected.Add(created.Id);
        }

        List<Guid> walked = [];

        DateTimeOffset? cursor = null;

        for (int page = 0; page < 10; page++)
        {
            ListPageResult<Apprentice> result = await repository.ListAsync(
                campaignId: null,
                status: null,
                limit: 2,
                beforeUpdatedAt: cursor,
                CancellationToken.None);

            walked.AddRange(result.Items.Select(static a => a.Id));

            if (!result.HasMore)
            {
                break;
            }

            Assert.NotNull(result.NextBeforeUpdatedAt);

            // A cursor that does not advance would loop forever on the tie group.
            Assert.True(cursor is null || result.NextBeforeUpdatedAt < cursor);

            cursor = result.NextBeforeUpdatedAt;
        }

        Assert.Equal(expected.Count, walked.Count);

        Assert.Equal(expected, walked.ToHashSet());
    }

    /// <summary>
    /// Ordering must be a total order, not merely "descending by UpdatedAt". With no identity
    /// tie-breaker the relative order of tied rows is undefined, so two identical queries can disagree
    /// and the keyset cursor cannot reason about the boundary at all.
    /// </summary>
    [SkippableFact]
    public async Task ListAsync_orders_tied_timestamps_deterministically()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApprenticeRepository repository = new(_db!, NullLogger<ApprenticeRepository>.Instance);

        DateTimeOffset tied = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

        for (int index = 0; index < 5; index++)
        {
            _ = await repository.AddAsync(
                new Apprentice
                {
                    Id = Guid.NewGuid(),
                    Name = "Tied",
                    Goal = "Deterministic order",
                    Status = ApprenticeStatus.Idle.ToString(),
                    WorkspacePath = "/tmp/workspace",
                    CreatedAt = tied,
                    UpdatedAt = tied,
                },
                CancellationToken.None);
        }

        ListPageResult<Apprentice> first = await repository.ListAsync(
            campaignId: null,
            status: null,
            limit: 5,
            beforeUpdatedAt: null,
            CancellationToken.None);

        ListPageResult<Apprentice> second = await repository.ListAsync(
            campaignId: null,
            status: null,
            limit: 5,
            beforeUpdatedAt: null,
            CancellationToken.None);

        Assert.Equal(
            first.Items.Select(static a => a.Id),
            second.Items.Select(static a => a.Id));
    }

    /// <summary>
    /// A list page must be cut in SQL, not after loading every matching row. The Plan and
    /// CheckpointData blobs of rows that are never returned are the expensive part, and an Apprentice
    /// table has no retention rule that would keep it small.
    /// </summary>
    [SkippableFact]
    public async Task ListAsync_reads_only_the_requested_page_from_storage()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApprenticeRepository repository = new(_db!, NullLogger<ApprenticeRepository>.Instance);

        DateTimeOffset newest = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        List<Guid> newestFirst = [];

        for (int index = 0; index < 12; index++)
        {
            Apprentice created = await repository.AddAsync(
                new Apprentice
                {
                    Id = Guid.NewGuid(),
                    Name = $"Page {index}",
                    Goal = "Read only the page",
                    Plan = new string('p', 20_000),
                    CheckpointData = new string('c', 20_000),
                    Status = ApprenticeStatus.Idle.ToString(),
                    WorkspacePath = "/tmp/workspace",
                    CreatedAt = newest.AddMinutes(-index),
                    UpdatedAt = newest.AddMinutes(-index),
                },
                CancellationToken.None);

            newestFirst.Add(created.Id);
        }

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        List<string> statements = [];

        raw.sqlite3_trace(connection.Handle, (object _, string sql) => statements.Add(sql), null);

        ListPageResult<Apprentice> page;

        try
        {
            page = await repository.ListAsync(
                campaignId: null,
                status: null,
                limit: 3,
                beforeUpdatedAt: null,
                CancellationToken.None);
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        Assert.Equal(newestFirst.Take(3), page.Items.Select(static item => item.Id));

        Assert.True(page.HasMore);

        string[] selects =
        [
            .. statements.Where(static sql =>
                sql.Contains("FROM \"Apprentices\"", StringComparison.Ordinal)),
        ];

        Assert.NotEmpty(selects);

        Assert.All(selects, static sql => Assert.Matches(@"\bLIMIT\b", sql));

        // The cursor still works: the next page starts exactly where this one stopped.
        ListPageResult<Apprentice> next = await repository.ListAsync(
            campaignId: null,
            status: null,
            limit: 3,
            beforeUpdatedAt: page.NextBeforeUpdatedAt,
            CancellationToken.None);

        Assert.Equal(newestFirst.Skip(3).Take(3), next.Items.Select(static item => item.Id));
    }

    private static Campaign Campaign(
        Guid id,
        string name,
        string path,
        DateTimeOffset timestamp) =>
        new()
        {
            Id = id,
            Name = name,
            NameLower = name.ToLowerInvariant(),
            Path = path,
            Type = WorkspaceType.Campaign,
            Settings = "{}",
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };

    private static Apprentice Apprentice(
        Guid campaignId,
        string name,
        ApprenticeStatus status,
        DateTimeOffset timestamp) =>
        new()
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Name = name,
            Goal = "Exercise the static query branch",
            Status = status.ToString(),
            WorkspacePath = "/tmp/workspace",
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
}
