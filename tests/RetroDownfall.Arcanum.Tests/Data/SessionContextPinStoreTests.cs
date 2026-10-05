using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    [SkippableFact]
    public async Task UpsertAsync_RetriesWhenTheDatabaseIsBusyThenSucceeds()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Session session = await AddSessionAsync();

        SessionContextPinStore store = new(_db!, TimeProvider.System);

        SessionContextPinRecord pinned = await RunWhileAnotherConnectionHoldsTheWriteLockAsync(
            () => store.UpsertAsync(session.Id, SessionContextPinKind.File, "docs/busy.md", "Busy", "v1"));

        SessionContextPinRecord listed = Assert.Single(await store.ListAsync(session.Id));

        Assert.Equal(pinned.Id, listed.Id);

        Assert.Equal("v1", listed.ContentVersion);
    }

    [SkippableFact]
    public async Task DeleteAsync_RetriesWhenTheDatabaseIsBusyThenSucceeds()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Session session = await AddSessionAsync();

        SessionContextPinStore store = new(_db!, TimeProvider.System);

        SessionContextPinRecord pinned = await store.UpsertAsync(
            session.Id, SessionContextPinKind.File, "docs/busy-delete.md", "Busy delete", "v1");

        bool deleted = await RunWhileAnotherConnectionHoldsTheWriteLockAsync(
            () => store.DeleteAsync(session.Id, pinned.Id));

        Assert.True(deleted);

        Assert.Empty(await store.ListAsync(session.Id));
    }

    private async Task<Session> AddSessionAsync()
    {
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

        return session;
    }

    /// <summary>
    /// Starts <paramref name="operation"/> while a second connection holds the write lock, lets its first attempts fail
    /// busy, then releases the lock. The operation's connection is set to refuse a busy attempt after one second, so an
    /// operation without its own retry fails here instead of waiting the lock out inside the driver.
    /// </summary>
    private async Task<T> RunWhileAnotherConnectionHoldsTheWriteLockAsync<T>(Func<Task<T>> operation)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        // One second is the shortest bounded wait the driver offers (0 means wait forever), so a busy statement is
        // refused after one second instead of waiting the lock out.
        connection.DefaultTimeout = 1;

        await using (SqliteCommand timeout = connection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout = 0";

            _ = await timeout.ExecuteNonQueryAsync();
        }

        await using ArcanumDbContext holderDb = fixture.CreateContext(_dbPath);

        SqliteConnection holder = (SqliteConnection)holderDb.Database.GetDbConnection();

        await holder.OpenAsync();

        SqliteTransaction writeLock = holder.BeginTransaction(deferred: false);

        Task<T> running;

        try
        {
            running = Task.Run(operation);

            // Longer than the one-second driver wait, so the operation has been refused busy at least once; a stalled
            // runner only lengthens the time the lock is held, which cannot make a retrying operation fail.
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
        }
        finally
        {
            await writeLock.RollbackAsync();

            await writeLock.DisposeAsync();
        }

        return await running.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
