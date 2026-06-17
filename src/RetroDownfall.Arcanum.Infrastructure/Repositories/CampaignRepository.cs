using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed class CampaignRepository : ICampaignRepository
{

    private const int DefaultListLimit = 100;

    private readonly ArcanumDbContext _db;

    private readonly ILogger<CampaignRepository> _logger;

    private readonly IOptionsSnapshot<ArcanumSettings> _arcOptions;

    public CampaignRepository(
        ArcanumDbContext db,
        ILogger<CampaignRepository> logger,
        IOptionsSnapshot<ArcanumSettings> arcOptions)
    {
        _db = db;

        _logger = logger;

        _arcOptions = arcOptions;
    }

    public async Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Campaigns
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        string normalized;

        try
        {
            normalized = Path.GetFullPath(path.Trim());
        }
        catch (Exception)
        {
            return null;
        }

        return await _db.Campaigns
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Path == normalized, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string nameLower = name.Trim().ToLowerInvariant();

        return await _db.Campaigns
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.NameLower == nameLower, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ListPageResult<Campaign>> ListAsync(
        WorkspaceType? typeFilter,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        int pageSize = ArcanumSettingClamps.ListQueryLimit(limit ?? DefaultListLimit);

        int skip = Math.Max(0, offset);

        IQueryable<Campaign> query = _db.Campaigns.AsNoTracking();

        if (typeFilter is { } type)
        {
            query = query.Where(c => c.Type == type);
        }

        List<Campaign> page = await query
            .OrderBy(c => c.Name)
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

        return new ListPageResult<Campaign>(page.ToArray(), hasMore, nextOffset);
    }

    public async Task<Campaign> AddAsync(Campaign campaign, CancellationToken cancellationToken = default)
    {
        CampaignsSettings settings = _arcOptions.Value.Campaigns ?? new CampaignsSettings();

        int maxCampaigns = ArcanumSettingClamps.MaxCampaigns(settings.MaxCampaigns);

        int count = await _db.Campaigns.CountAsync(cancellationToken).ConfigureAwait(false);

        if (count >= maxCampaigns)
        {
            throw new InvalidOperationException("Campaign.MaxReached");
        }

        campaign.NameLower = campaign.Name.Trim().ToLowerInvariant();

        _db.Campaigns.Add(campaign);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return campaign;
    }

    public async Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default)
    {
        campaign.NameLower = campaign.Name.Trim().ToLowerInvariant();

        _db.Campaigns.Update(campaign);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return campaign;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        int deleted = await _db.Campaigns
            .Where(c => c.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return deleted > 0;
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        _db.Campaigns.CountAsync(cancellationToken);

    public static CampaignSettings DeserializeSettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CampaignSettings(
                DefaultModel: null,
                ModelMap: null,
                McpServerProfiles: null,
                SpellRoots: null,
                LoreNamespace: null,
                AllowedTools: null,
                RequireWardForForbiddenArts: false);
        }

        return JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.CampaignSettings)
            ?? new CampaignSettings(
                DefaultModel: null,
                ModelMap: null,
                McpServerProfiles: null,
                SpellRoots: null,
                LoreNamespace: null,
                AllowedTools: null,
                RequireWardForForbiddenArts: false);
    }

    public static string SerializeSettings(CampaignSettings settings) =>
        JsonSerializer.Serialize(settings, TheForgeJsonContext.Default.CampaignSettings);

    public static SanctumConfig GetSanctumConfig(Campaign campaign) =>
        DeserializeSanctumConfig(campaign.SanctumConfigJson);

    public static void SetSanctumConfig(Campaign campaign, SanctumConfig config) =>
        campaign.SanctumConfigJson = SerializeSanctumConfig(config);

    public static SanctumConfig DefaultSanctumConfig() => new();

    public static SanctumConfig DeserializeSanctumConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
        {
            return DefaultSanctumConfig();
        }

        return JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.SanctumConfig)
            ?? DefaultSanctumConfig();
    }

    public static string SerializeSanctumConfig(SanctumConfig config) =>
        JsonSerializer.Serialize(config, TheForgeJsonContext.Default.SanctumConfig);

}
