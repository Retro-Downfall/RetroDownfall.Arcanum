using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed class PromptRepository : IPromptRepository
{

    private const int DefaultListLimit = 100;

    private readonly ArcanumDbContext _db;

    private readonly ILogger<PromptRepository> _logger;

    public PromptRepository(ArcanumDbContext db, ILogger<PromptRepository> logger)
    {
        _db = db;

        _logger = logger;
    }

    public async Task<Prompt?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Prompts
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Prompt?> GetByNameAndVersionAsync(
        string name,
        string version,
        Guid? campaignId,
        CancellationToken cancellationToken = default)
    {
        string trimmedName = name.Trim();

        string trimmedVersion = version.Trim();

        return await _db.Prompts
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.Name == trimmedName
                    && p.Version == trimmedVersion
                    && p.CampaignId == campaignId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Prompt>> ListVersionsAsync(
        string name,
        Guid? campaignId,
        CancellationToken cancellationToken = default)
    {
        string trimmedName = name.Trim();

        return await _db.Prompts
            .AsNoTracking()
            .Where(p => p.Name == trimmedName && p.CampaignId == campaignId)
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ListPageResult<Prompt>> ListAsync(
        Guid? campaignId,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        int pageSize = ArcanumSettingClamps.ListQueryLimit(limit ?? DefaultListLimit);

        int skip = Math.Max(0, offset);

        List<Prompt> page = await _db.Prompts
            .AsNoTracking()
            .Where(p => p.CampaignId == campaignId)
            .OrderBy(p => p.Name)
            .ThenByDescending(p => p.UpdatedAt)
            .Skip(skip)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        bool hasMore = page.Count > pageSize;

        if (hasMore)
        {
            page = page.Take(pageSize).ToList();
        }

        int? nextOffset = hasMore ? skip + pageSize : null;

        return new ListPageResult<Prompt>(page.ToArray(), hasMore, nextOffset);
    }

    public async Task<Prompt> AddAsync(Prompt prompt, CancellationToken cancellationToken = default)
    {
        _db.Prompts.Add(prompt);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return prompt;
    }

    public async Task<Prompt> UpdateAsync(Prompt prompt, CancellationToken cancellationToken = default)
    {
        _db.Prompts.Update(prompt);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return prompt;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        int deleted = await _db.Prompts
            .Where(p => p.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return deleted > 0;
    }

    public static string[] DeserializeTags(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.StringArray) ?? [];
    }

    public static string SerializeTags(string[] tags) =>
        JsonSerializer.Serialize(tags, TheForgeJsonContext.Default.StringArray);

}
