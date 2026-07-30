using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SessionContextPinStoreTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private ArcanumDbContext? _db;

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();
        _db = fixture.CreateContext(_dbPath);
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
    public async Task Upsert_list_delete_round_trip_survives_new_store_instance()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        Session session = new()
        {
            Id = Guid.NewGuid(),
            Title = "pins",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db!.Sessions.Add(session);
        await _db.SaveChangesAsync();

        SessionContextPinStore first = new(_db, TimeProvider.System);
        SessionContextPinRecord created = await first.UpsertAsync(
            session.Id, SessionContextPinKind.File, "docs/readme.md", "README", "abc");
        SessionContextPinRecord updated = await first.UpsertAsync(
            session.Id, SessionContextPinKind.File, "docs/readme.md", "README updated", "def");

        Assert.Equal(created.Id, updated.Id);
        SessionContextPinStore restarted = new(_db, TimeProvider.System);
        SessionContextPinRecord listed = Assert.Single(await restarted.ListAsync(session.Id));
        Assert.Equal("def", listed.ContentVersion);
        Assert.Equal("README updated", listed.DisplayLabel);
        Assert.True(await restarted.DeleteAsync(session.Id, listed.Id));
        Assert.Empty(await restarted.ListAsync(session.Id));
    }
}
