using System.Text.Json;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class SessionSerializationTests
{

    [Fact]
    public void Session_WithEntries_RoundTripsWithoutCycle()
    {
        Session session = new()
        {
            Id = Guid.NewGuid(),
            Title = "Test session",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        Entry entry = new()
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = "hello",
            ModelUsed = "mistral",
            CreatedAt = DateTimeOffset.UtcNow,
            Session = session,
        };

        session.Entries = new List<Entry> { entry };

        string json = JsonSerializer.Serialize(session, TheForgeJsonContext.Default.Session);

        Assert.NotNull(json);

        Assert.Contains("\"title\":\"Test session\"", json, StringComparison.Ordinal);

        Session? deserialized = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.Session);

        Assert.NotNull(deserialized);

        Assert.Equal(session.Title, deserialized.Title);

        Assert.Single(deserialized.Entries);

        Assert.Equal("hello", deserialized.Entries.First().Content);
    }

}
