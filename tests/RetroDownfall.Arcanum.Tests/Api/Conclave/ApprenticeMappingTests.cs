using RetroDownfall.Arcanum.Api.Conclave;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Api.Conclave;

public sealed class ApprenticeMappingTests
{

    [Fact]
    public void ToSummaryDto_MapsPlanStepCount()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Apprentice apprentice = new()
        {
            Id = Guid.NewGuid(),
            CampaignId = Guid.NewGuid(),
            Name = "Scribe",
            Goal = "organize",
            Status = ApprenticeStatus.Running.ToString(),
            CurrentStep = 1,
            Plan = """[{"title":"step one","description":"do it"}]""",
            CreatedAt = now,
            UpdatedAt = now,
        };

        ApprenticeSummaryDto dto = ApprenticeMapping.ToSummaryDto(apprentice);

        Assert.Equal(apprentice.Id, dto.Id);

        Assert.Equal(1, dto.PlanStepCount);

        Assert.Equal(apprentice.Name, dto.Name);
    }

    [Fact]
    public void ToDetailDto_IncludesCheckpointAndParent()
    {
        Guid parentId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Apprentice apprentice = new()
        {
            Id = Guid.NewGuid(),
            Name = "Child",
            Goal = "help",
            Plan = "[]",
            CheckpointData = ApprenticeRepository.SerializeCheckpoint(new ApprenticeCheckpoint
            {
                ParentApprenticeId = parentId,
            }),
            CreatedAt = now,
            UpdatedAt = now,
        };

        ApprenticeDetailDto dto = ApprenticeMapping.ToDetailDto(apprentice);

        Assert.Equal(parentId, dto.ParentApprenticeId);

        Assert.NotNull(dto.Checkpoint);

        Assert.Equal(parentId, dto.Checkpoint!.ParentApprenticeId);
    }

    [Fact]
    public void ToDetailDto_PrefersParentApprenticeIdColumnOverCheckpoint()
    {
        Guid columnParentId = Guid.NewGuid();
        Guid checkpointParentId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Apprentice apprentice = new()
        {
            Id = Guid.NewGuid(),
            ParentApprenticeId = columnParentId,
            Name = "Child",
            Goal = "help",
            Plan = "[]",
            CheckpointData = ApprenticeRepository.SerializeCheckpoint(new ApprenticeCheckpoint
            {
                ParentApprenticeId = checkpointParentId,
            }),
            CreatedAt = now,
            UpdatedAt = now,
        };

        ApprenticeDetailDto dto = ApprenticeMapping.ToDetailDto(apprentice);

        Assert.Equal(columnParentId, dto.ParentApprenticeId);
    }

}
