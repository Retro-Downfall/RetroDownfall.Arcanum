using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;


namespace RetroDownfall.Arcanum.Infrastructure.Repositories;


/// <summary>
/// Owns the cross-cutting invariants for session entry writes: per-session lock acquisition,
/// SQLite busy retry, entry-limit checks, unsummarized-entry counter maintenance, and UpdatedAt bumps.
/// </summary>
internal sealed class SessionEntryPersistence
{

    private readonly ArcanumDbContext _db;


    public SessionEntryPersistence(ArcanumDbContext db)
    {

        _db = db;

    }


    public static Task<IDisposable> AcquireWriteLockAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {

        return SessionWriteLock.AcquireAsync(sessionId, cancellationToken);

    }


    public static Error? CheckEntryLimits(
        int currentEntryCount,
        int entriesToAdd,
        SessionSettings? settings,
        params string?[] contents)
    {

        return GrimoireLimits.EnforceEntryLimits(currentEntryCount, entriesToAdd, settings, contents);

    }


    public Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {

        return _db.Entries.CountAsync(e => e.SessionId == sessionId, cancellationToken);

    }


    public Task BumpSessionUpdatedAtAsync(
        Guid sessionId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {

        return SqliteBusyRetry.ExecuteAsync(
            () => _db.Sessions
                .Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.UpdatedAt, updatedAt),
                    cancellationToken),
            cancellationToken);

    }


    public Task SaveChangesWithRetryAsync(CancellationToken cancellationToken = default)
    {

        return SqliteBusyRetry.ExecuteAsync(
            () => _db.SaveChangesAsync(cancellationToken),
            cancellationToken);

    }


    public Task IncrementUnsummarizedEntryCountIfKnownAsync(
        Guid sessionId,
        int delta,
        CancellationToken cancellationToken = default)
    {

        if (delta <= 0)
        {

            return Task.CompletedTask;

        }

        return SqliteBusyRetry.ExecuteAsync(
            () => _db.Sessions
                .Where(s => s.Id == sessionId && s.UnsummarizedEntryCount >= 0)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.UnsummarizedEntryCount, x => x.UnsummarizedEntryCount + delta),
                    cancellationToken),
            cancellationToken);

    }


    public Task DecrementUnsummarizedEntryCountIfKnownAsync(
        Guid sessionId,
        int delta,
        CancellationToken cancellationToken = default)
    {

        if (delta <= 0)
        {

            return Task.CompletedTask;

        }

        return SqliteBusyRetry.ExecuteAsync(
            () => _db.Sessions
                .Where(s => s.Id == sessionId && s.UnsummarizedEntryCount > 0)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.UnsummarizedEntryCount, x => x.UnsummarizedEntryCount - delta),
                    cancellationToken),
            cancellationToken);

    }

}
