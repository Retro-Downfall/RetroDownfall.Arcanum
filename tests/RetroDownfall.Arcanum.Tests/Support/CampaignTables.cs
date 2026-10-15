using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>A canonical Campaign resolver that answers every request with the Campaign it was built with, or with no Campaign at all.</summary>
internal sealed class FixedCampaignResolver(Guid? campaignId) : ICanonicalCampaignContextResolver
{
    public CanonicalCampaignResolutionRequest? LastRequest { get; private set; }

    public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
        CanonicalCampaignResolutionRequest request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;

        return ValueTask.FromResult(
            Result<CanonicalCampaignContext>.Success(
                campaignId is { } id
                    ? CanonicalCampaignContext.Create(
                        SessionCampaignBinding.ForCampaign(id),
                        campaignAvailabilityGeneration: 1,
                        pathIdentityPolicyVersion: 1,
                        pathIdentityRevision: null,
                        rootIdentityDigest: null)
                    : CanonicalCampaignContext.GlobalOnly));
    }
}

/// <summary>A Campaign table that finds its rows by identifier and answers one registered root as the one containing a path.</summary>
internal sealed class CampaignTable : ICampaignRepository
{
    private readonly Dictionary<Guid, Campaign> _rows;

    internal CampaignTable(params Campaign[] rows)
    {
        _rows = rows.ToDictionary(static row => row.Id);
    }

    internal Guid? ContainingRoot { get; init; }

    internal List<string> ContainedPaths { get; } = [];

    public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.GetValueOrDefault(id));

    public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<Campaign?>(null);

    public Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ContainedPaths.Add(path);

        return Task.FromResult(ContainingRoot is { } id ? _rows.GetValueOrDefault(id) : null);
    }

    public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<Campaign?>(null);

    public Task<ListPageResult<Campaign>> ListAsync(
        WorkspaceType? typeFilter,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ListPageResult<Campaign>([], false));

    public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.Count);
}
