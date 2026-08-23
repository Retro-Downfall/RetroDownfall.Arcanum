using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

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
            TotalCostUsd = 0.125m,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        SessionDetailDto dto = SessionMapping.ToDetailDto(session, entryCount: 3);

        Assert.Equal(sessionId, dto.Id);

        Assert.Equal(3, dto.EntryCount);

        Assert.Equal("rolled up", dto.Summary);

        Assert.Equal(42, dto.TotalTokensUsed);

        Assert.Equal(0.125m, dto.TotalCostUsd);

        string json = JsonSerializer.Serialize(dto, ArcanumJsonContext.Default.SessionDetailDto);

        Assert.Contains("\"totalCostUsd\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToAttachmentDto_MapsBoundRecordFields()
    {
        Guid id = Guid.NewGuid();

        Guid sessionId = Guid.NewGuid();

        DateTimeOffset createdAt = DateTimeOffset.Parse("2026-07-19T18:00:00Z");

        SessionAttachmentRecord record = new(
            id,
            sessionId,
            EntryId: null,
            PendingTurnId: null,
            SessionAttachmentState.Bound,
            "notes",
            "notes.txt",
            2,
            $"{sessionId:N}/notes/v2/notes.txt",
            "deadbeef",
            "text/plain",
            42,
            SessionAttachmentKind.Text,
            createdAt);

        SessionAttachmentDto dto = SessionMapping.ToAttachmentDto(record);

        Assert.Equal(id, dto.Id);
        Assert.Equal("notes", dto.LogicalKey);
        Assert.Equal("notes.txt", dto.OriginalFileName);
        Assert.Equal(2, dto.Version);
        Assert.Equal($"{sessionId:N}/notes/v2/notes.txt", dto.RelativePath);
        Assert.Equal("text/plain", dto.MimeType);
        Assert.Equal(42, dto.ByteLength);
        Assert.Equal(SessionAttachmentKind.Text, dto.Kind);
        Assert.Equal("deadbeef", dto.ContentSha256);
        Assert.Equal(createdAt, dto.CreatedAt);
    }

}
