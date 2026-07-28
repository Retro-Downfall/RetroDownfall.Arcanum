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

    internal Func<int, Exception, CancellationToken, ValueTask>? RetryingForTesting { get; set; }

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

        // EF Core's SQLite provider cannot translate DateTimeOffset in ORDER BY (see
        // PromptRepository.ListAsync for the same constraint). Materialize the name+campaign-
        // scoped rows (small set) and sort client-side.
        List<Prompt> matched = await _db.Prompts
            .AsNoTracking()
            .Where(p => p.Name == trimmedName && p.CampaignId == campaignId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return matched
            .OrderByDescending(p => p.UpdatedAt)
            .ToArray();
    }

    public async Task<ListPageResult<Prompt>> ListAsync(
        Guid? campaignId,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        int pageSize = ArcanumSettingClamps.ListQueryLimit(limit ?? DefaultListLimit);

        int skip = Math.Max(0, offset);

        // W: composite server-side ORDER BY (Name, UpdatedAt) combined with Skip/Take triggers
        // "SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses" from the
        // EF Core Sqlite provider's paging translator. Sort and page client-side instead; prompt tables
        // are workspace-scoped and small, so this is not a performance concern.
        List<Prompt> matched = await _db.Prompts
            .AsNoTracking()
            .Where(p => p.CampaignId == campaignId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Prompt[] ordered = matched
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenByDescending(p => p.UpdatedAt)
            .ToArray();

        Prompt[] page = ordered.Skip(skip).Take(pageSize + 1).ToArray();

        bool hasMore = page.Length > pageSize;

        if (hasMore)
        {
            page = page.Take(pageSize).ToArray();
        }

        int? nextOffset = hasMore ? skip + pageSize : null;

        return new ListPageResult<Prompt>(page, hasMore, nextOffset);
    }

    public async Task<Prompt> AddAsync(Prompt prompt, CancellationToken cancellationToken = default)
    {
        _db.Prompts.Add(prompt);

        _ = await EfSaveChangesRetry
            .ExecuteAsync(_db, cancellationToken, RetryingForTesting)
            .ConfigureAwait(false);

        return prompt;
    }

    public async Task<Prompt> UpdateAsync(Prompt prompt, CancellationToken cancellationToken = default)
    {
        _db.Prompts.Update(prompt);

        _ = await EfSaveChangesRetry
            .ExecuteAsync(_db, cancellationToken, RetryingForTesting)
            .ConfigureAwait(false);

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
