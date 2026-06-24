using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
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
            Status = ApprenticeStatus.Running.ToString(),
            WorkspacePath = "/tmp/workspace",
            CheckpointData = ApprenticeRepository.SerializeCheckpoint(
                new ApprenticeCheckpoint
                {
                    CurrentStep = 2,
                    ParentApprenticeId = parentId,
                    Timestamp = now,
                }),
            CreatedAt = now,
            UpdatedAt = now,
        };

        Apprentice saved = await repository.AddAsync(apprentice, CancellationToken.None);

        Apprentice? loaded = await repository.GetByIdAsync(saved.Id, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(parentId, loaded!.ParentApprenticeId);

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

        IReadOnlyList<Apprentice> resumable = await repository.GetResumableAsync(CancellationToken.None);

        Assert.Contains(resumable, a => a.Id == running.Id);

        Assert.DoesNotContain(resumable, a => a.Id == idle.Id);

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

}
