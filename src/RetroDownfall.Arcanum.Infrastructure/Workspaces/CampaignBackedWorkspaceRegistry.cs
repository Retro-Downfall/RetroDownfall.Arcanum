using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

public sealed class CampaignBackedWorkspaceRegistry : IWorkspaceRegistry
{

    private readonly IServiceScopeFactory _scopeFactory;

    private readonly IGrimoireDbReadiness _dbReadiness;

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    private readonly InMemoryWorkspaceRegistry _fallback;

    public CampaignBackedWorkspaceRegistry(
        IServiceScopeFactory scopeFactory,
        IGrimoireDbReadiness dbReadiness,
        IOptionsMonitor<ArcanumSettings> settings)
    {
        _scopeFactory = scopeFactory;

        _dbReadiness = dbReadiness;

        _settings = settings;

        _fallback = new InMemoryWorkspaceRegistry(settings);
    }

    public async Task<WorkspaceInfo[]> GetAllAsync(CancellationToken ct)
    {
        if (!_dbReadiness.IsReady)
        {
            return await _fallback.GetAllAsync(ct).ConfigureAwait(false);
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        ICampaignRepository repo = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        int pageSize = ArcanumSettingClamps.ListQueryLimit(10_000);

        int offset = 0;

        List<Campaign> campaigns = [];

        while (true)
        {

            ListPageResult<Campaign> page = await repo
                .ListAsync(typeFilter: null, limit: pageSize, offset: offset, cancellationToken: ct)
                .ConfigureAwait(false);

            campaigns.AddRange(page.Items);

            if (!page.HasMore)
            {

                break;

            }

            int nextOffset = page.NextOffset ?? checked(offset + page.Items.Length);

            if (nextOffset <= offset)
            {

                throw new InvalidOperationException(
                    "Campaign pagination returned a non-advancing continuation offset. Retry the request; if the problem persists, inspect the campaign repository.");

            }

            offset = nextOffset;

        }

        WorkspaceInfo[] result = new WorkspaceInfo[campaigns.Count];

        for (int i = 0; i < campaigns.Count; i++)
        {
            result[i] = ToWorkspaceInfo(campaigns[i]);
        }

        return result;
    }

    public async Task<WorkspaceInfo?> GetAsync(string id, CancellationToken ct)
    {
        if (!_dbReadiness.IsReady)
        {
            return await _fallback.GetAsync(id, ct).ConfigureAwait(false);
        }

        if (!Guid.TryParse(id, out Guid guid))
        {
            return null;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        ICampaignRepository repo = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        Campaign? campaign = await repo.GetByIdAsync(guid, ct).ConfigureAwait(false);

        return campaign is null ? null : ToWorkspaceInfo(campaign);
    }

    public async Task<Result<WorkspaceInfo>> RegisterAsync(CreateWorkspaceRequest request, CancellationToken ct)
    {
        if (!_dbReadiness.IsReady)
        {
            return await _fallback.RegisterAsync(request, ct).ConfigureAwait(false);
        }

        Result<string> pathResult = CampaignPathPolicy.ValidateAndNormalizePath(request.Path, _settings.CurrentValue);

        if (pathResult.IsFailure)
        {
            return MapPathValidationError(pathResult.Error);
        }

        string normalizedPath = pathResult.Value;

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        ICampaignRepository repo = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        Campaign? existingName = await repo.GetByNameAsync(request.Name.Trim(), ct).ConfigureAwait(false);

        if (existingName is not null)
        {
            return new Error("Workspace.NameDuplicate", "A workspace with this name is already registered.");
        }

        Campaign? existingPath = await repo.GetByPathAsync(normalizedPath, ct).ConfigureAwait(false);

        if (existingPath is not null)
        {
            return new Error("Workspace.PathDuplicate", "A workspace with this path is already registered.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Campaign campaign = new()
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Path = normalizedPath,
            Type = request.Type,
            Description = null,
            Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
            CreatedAt = now,
            UpdatedAt = now,
        };

        Result<Campaign> addResult = await repo
            .AddAsync(campaign, ct)
            .ConfigureAwait(false);

        if (addResult.IsFailure)
        {
            return addResult.Error;
        }

        Directory.CreateDirectory(Path.Combine(normalizedPath, ".arcanum"));

        return ToWorkspaceInfo(campaign);
    }

    public async Task<Result<WorkspaceInfo>> UpdateAsync(string id, UpdateWorkspaceRequest request, CancellationToken ct)
    {
        if (!_dbReadiness.IsReady)
        {
            return await _fallback.UpdateAsync(id, request, ct).ConfigureAwait(false);
        }

        if (!Guid.TryParse(id, out Guid guid))
        {
            return new Error(ErrorCodes.Workspace.NotFound, "The workspace was not found.");
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        ICampaignRepository repo = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        Campaign? existing = await repo.GetByIdAsync(guid, ct).ConfigureAwait(false);

        if (existing is null)
        {
            return new Error(ErrorCodes.Workspace.NotFound, "The workspace was not found.");
        }

        string updatedName = existing.Name;

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return new Error(ErrorCodes.Workspace.NameEmpty, "Workspace name cannot be empty.");
            }

            updatedName = request.Name.Trim();

            Campaign? nameConflict = await repo.GetByNameAsync(updatedName, ct).ConfigureAwait(false);

            if (nameConflict is not null && nameConflict.Id != guid)
            {
                return new Error("Workspace.NameDuplicate", "A workspace with this name is already registered.");
            }
        }

        WorkspaceType updatedType = request.Type ?? existing.Type;

        existing.Name = updatedName;

        existing.Type = updatedType;

        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await repo.UpdateAsync(existing, ct).ConfigureAwait(false);

        return ToWorkspaceInfo(existing);
    }

    public async Task<Result<bool>> UnregisterAsync(string id, CancellationToken ct)
    {
        if (!_dbReadiness.IsReady)
        {
            return await _fallback.UnregisterAsync(id, ct).ConfigureAwait(false);
        }

        if (!Guid.TryParse(id, out Guid guid))
        {
            return new Error(ErrorCodes.Workspace.NotFound, "The workspace was not found.");
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        ICampaignRepository repo = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        bool deleted = await repo.DeleteAsync(guid, ct).ConfigureAwait(false);

        if (!deleted)
        {
            return new Error(ErrorCodes.Workspace.NotFound, "The workspace was not found.");
        }

        return true;
    }

    private static WorkspaceInfo ToWorkspaceInfo(Campaign campaign) =>
        new(
            campaign.Id.ToString("N"),
            campaign.Name,
            campaign.Path,
            campaign.Type,
            campaign.CreatedAt,
            Persisted: true);

    private static Error MapPathValidationError(Error error) =>
        error.Code switch
        {
            ErrorCodes.Campaign.InvalidPath => new Error("Workspace.PathNotFound", error.Message),
            ErrorCodes.Campaign.PathNotAllowed => new Error(ErrorCodes.Workspace.PathNotAllowed, error.Message),
            _ => error,
        };

}
