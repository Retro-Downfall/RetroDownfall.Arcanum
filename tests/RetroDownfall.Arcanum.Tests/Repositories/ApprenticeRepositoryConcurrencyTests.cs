using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Repositories;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class ApprenticeRepositoryConcurrencyTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public ApprenticeRepositoryConcurrencyTests(GrimoireFixture fixture)
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
    public async Task Multi_step_get_update_cycle_reaches_completed_without_tracker_conflict()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApprenticeRepository repository = new(_db!, NullLogger<ApprenticeRepository>.Instance);

        List<PlanStep> plan =
        [
            new PlanStep { Index = 0, Description = "Step one" },
            new PlanStep { Index = 1, Description = "Step two" },
        ];

        Apprentice apprentice = new()
        {
            Id = Guid.NewGuid(),
            Name = "Tracker probe",
            Goal = "Verify multi-step persistence",
            Plan = ApprenticeRepository.SerializePlan(plan),
            Status = ApprenticeStatus.Running.ToString(),
            CurrentStep = 0,
            WorkspacePath = "/tmp/workspace",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _ = await repository.AddAsync(apprentice, CancellationToken.None);

        for (int step = 0; step < plan.Count; step++)
        {

            Apprentice? loaded = await repository.GetByIdAsync(apprentice.Id, CancellationToken.None);

            Assert.NotNull(loaded);

            loaded!.CurrentStep = step + 1;

            loaded.Status = step + 1 < plan.Count
                ? ApprenticeStatus.Running.ToString()
                : ApprenticeStatus.Completed.ToString();

            _ = await repository.UpdateAsync(loaded, CancellationToken.None);

        }

        Apprentice? completed = await repository.GetByIdAsync(apprentice.Id, CancellationToken.None);

        Assert.NotNull(completed);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), completed!.Status);

        Assert.Equal(plan.Count, completed.CurrentStep);

    }

}
