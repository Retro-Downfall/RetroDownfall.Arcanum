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

    /// <summary>The registered root that contains one particular path, for a path that is not the one <see cref="ContainingRoot"/> answers.</summary>
    internal Dictionary<string, Guid> ContainingRootByPath { get; } = [];

    /// <summary>The registered root whose text contains the way a path is spelled, answered by <see cref="GetBySpelledPathAsync"/>.</summary>
    internal Guid? SpelledRoot { get; init; }

    internal List<string> ContainedPaths { get; } = [];

    internal List<string> SpelledPaths { get; } = [];

    public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.GetValueOrDefault(id));

    public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<Campaign?>(null);

    public Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ContainedPaths.Add(path);

        Guid? id = ContainingRootByPath.TryGetValue(path, out Guid byPath) ? byPath : ContainingRoot;

        return Task.FromResult(id is { } found ? _rows.GetValueOrDefault(found) : null);
    }

    public Task<Campaign?> GetBySpelledPathAsync(string path, CancellationToken cancellationToken = default)
    {
        SpelledPaths.Add(path);

        return Task.FromResult(SpelledRoot is { } id ? _rows.GetValueOrDefault(id) : null);
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
