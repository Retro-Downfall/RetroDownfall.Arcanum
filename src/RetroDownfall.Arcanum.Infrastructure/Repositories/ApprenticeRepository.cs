using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed class ApprenticeRepository : IApprenticeRepository
{

    private const int DefaultListLimit = 100;

    private readonly ArcanumDbContext _db;

    private readonly ILogger<ApprenticeRepository> _logger;

    public ApprenticeRepository(ArcanumDbContext db, ILogger<ApprenticeRepository> logger)
    {
        _db = db;

        _logger = logger;
    }

    public async Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Apprentices
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ListPageResult<Apprentice>> ListAsync(
        Guid? campaignId,
        string? status,
        int? limit = null,
        DateTimeOffset? beforeUpdatedAt = null,
        CancellationToken cancellationToken = default)
    {
        int pageSize = ArcanumSettingClamps.ListQueryLimit(limit ?? DefaultListLimit);

        IQueryable<Apprentice> query = _db.Apprentices.AsNoTracking();

        if (campaignId is { } cid)
        {
            query = query.Where(a => a.CampaignId == cid);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            string statusFilter = status.Trim();

            query = query.Where(a => a.Status == statusFilter);
        }

        if (beforeUpdatedAt is DateTimeOffset before)
        {
            query = query.Where(a => a.UpdatedAt < before);
        }

        List<Apprentice> page = await query
            .OrderByDescending(a => a.UpdatedAt)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        bool hasMore = page.Count > pageSize;

        if (hasMore)
        {
            page = page.Take(pageSize).ToList();
        }

        DateTimeOffset? nextBefore = hasMore && page.Count > 0 ? page[^1].UpdatedAt : null;

        return new ListPageResult<Apprentice>(page.ToArray(), hasMore, NextBeforeUpdatedAt: nextBefore);
    }

    public async Task<Apprentice> AddAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
    {
        _db.Apprentices.Add(apprentice);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return apprentice;
    }

    public async Task<Apprentice> UpdateAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
    {
        apprentice.UpdatedAt = DateTimeOffset.UtcNow;

        _db.Apprentices.Update(apprentice);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return apprentice;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        int deleted = await _db.Apprentices
            .Where(a => a.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return deleted > 0;
    }

    public async Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default)
    {
        string running = ApprenticeStatus.Running.ToString();

        return await _db.Apprentices
            .AsNoTracking()
            .Where(a => a.Status == running)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public static List<PlanStep> DeserializePlan(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ListPlanStep) ?? [];
    }

    public static string SerializePlan(IReadOnlyList<PlanStep> plan) =>
        JsonSerializer.Serialize(plan, TheForgeJsonContext.Default.ListPlanStep);

    public static ApprenticeCheckpoint? DeserializeCheckpoint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApprenticeCheckpoint);
    }

    public static string SerializeCheckpoint(ApprenticeCheckpoint checkpoint) =>
        JsonSerializer.Serialize(checkpoint, TheForgeJsonContext.Default.ApprenticeCheckpoint);

}
