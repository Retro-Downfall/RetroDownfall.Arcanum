using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

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

        IReadOnlyList<Apprentice> interrupted = await repository.GetInterruptedPlanningAsync(CancellationToken.None);

        Assert.Empty(interrupted);

        running.Status = ApprenticeStatus.Completed.ToString();

        await repository.UpdateAsync(running, CancellationToken.None);

        Apprentice? updated = await repository.GetByIdAsync(running.Id, CancellationToken.None);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), updated!.Status);

        bool deleted = await repository.DeleteAsync(idle.Id, CancellationToken.None);

        Assert.True(deleted);

        Assert.Null(await repository.GetByIdAsync(idle.Id, CancellationToken.None));
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
