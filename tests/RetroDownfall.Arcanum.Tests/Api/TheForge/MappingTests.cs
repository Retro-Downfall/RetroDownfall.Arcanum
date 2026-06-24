using System.Text.Json;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

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

public sealed class PromptMappingTests
{

    [Fact]
    public void ToSummaryDto_DeserializesTags()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Prompt prompt = new()
        {
            Id = Guid.NewGuid(),
            Name = "greeting",
            Version = "1.0.0",
            Tags = PromptRepository.SerializeTags(["intro", "wizard"]),
            UpdatedAt = now,
        };

        PromptSummaryDto dto = PromptMapping.ToSummaryDto(prompt);

        Assert.Equal(["intro", "wizard"], dto.Tags);

        Assert.Equal("greeting", dto.Name);
    }

    [Fact]
    public void DeserializeJsonDocument_RoundTripsRawText()
    {
        string raw = """{"type":"object"}""";

        JsonDocument? doc = PromptMapping.DeserializeJsonDocument(raw);

        Assert.NotNull(doc);

        Assert.Equal(raw, PromptMapping.SerializeJsonDocument(doc));
    }

    [Fact]
    public void DeserializeJsonDocument_Blank_ReturnsNull()
    {
        Assert.Null(PromptMapping.DeserializeJsonDocument(" "));

        Assert.Null(PromptMapping.SerializeJsonDocument(null));
    }

}

public sealed class SessionMappingTests
{

    [Fact]
    public void ToEntryDto_LowercasesRole()
    {
        Entry entry = new()
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            Role = MessageRole.Assistant,
            Content = "reply",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        EntryDto dto = SessionMapping.ToEntryDto(entry);

        Assert.Equal("assistant", dto.Role);

        Assert.Equal("reply", dto.Content);
    }

    [Fact]
    public void ToDetailDto_MapsSessionMetadata()
    {
        Guid sessionId = Guid.NewGuid();

        Session session = new()
        {
            Id = sessionId,
            Title = "Quest",
            Status = "active",
            Summary = "rolled up",
            TotalTokensUsed = 42,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        SessionDetailDto dto = SessionMapping.ToDetailDto(session, entryCount: 3);

        Assert.Equal(sessionId, dto.Id);

        Assert.Equal(3, dto.EntryCount);

        Assert.Equal("rolled up", dto.Summary);

        Assert.Equal(42, dto.TotalTokensUsed);
    }

}
