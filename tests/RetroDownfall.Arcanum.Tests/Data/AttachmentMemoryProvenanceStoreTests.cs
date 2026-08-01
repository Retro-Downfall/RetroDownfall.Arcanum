using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class AttachmentMemoryProvenanceStoreTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public AttachmentMemoryProvenanceStoreTests(GrimoireFixture fixture)
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

    public async Task Consultations_RoundTripMetadataAndRemainUnresolvedAfterSourceDeletion()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        DateTimeOffset turnCreatedAt = DateTimeOffset.Parse("2026-08-01T12:00:00Z");

        DateTimeOffset materializedAt = turnCreatedAt.AddMinutes(1);

        Session session = new()
        {
            Id = sessionId,
            Status = "active",
            CreatedAt = turnCreatedAt.AddMinutes(-1),
            UpdatedAt = materializedAt,
        };

        Entry assistantEntry = new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.Assistant,
            Content = "Done",
            ModelUsed = "test-model",
            CreatedAt = turnCreatedAt,
            Sequence = 1,
        };

        _db!.Sessions.Add(session);

        _db.Entries.Add(assistantEntry);

        await _db.SaveChangesAsync();

        AttachmentMemoryProvenanceStore store = new(_db);

        AttachmentMemoryProvenance provenance = new(
            sessionId,
            Guid.NewGuid(),
            "requirements",
            5,
            "content-hash",
            materializedAt,
            "WorkspaceFile",
            AttachmentSourceAvailability.Available);

        await store.RecordConsultationsAsync(
            assistantEntry.Id,
            [provenance],
            CancellationToken.None);

        AttachmentMemoryProvenance reloaded = Assert.Single(
            await store.ListConsultationsAsync(
                sessionId,
                turnCreatedAt.AddSeconds(-1),
                turnCreatedAt.AddSeconds(1),
                CancellationToken.None));

        Assert.Equal(provenance.AttachmentId, reloaded.AttachmentId);

        Assert.Equal("requirements", reloaded.LogicalKey);

        Assert.Equal("content-hash", reloaded.ContentHash);

        Assert.Equal(AttachmentSourceAvailability.Unavailable, reloaded.Availability);

    }

}
