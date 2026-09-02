using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class SanctumGuardInvalidCampaignTests
{

    [Fact]
    public async Task ValidatePathAsync_InvalidCampaignId_DeniesBeforeRepositoryLookup()
    {

        StubCampaignRepository repository = new();

        StubSanctumBreachRepository breachRepository = new();

        SanctumGuard guard = new(
            repository,
            breachRepository,
            NullLogger<SanctumGuard>.Instance);

        SanctumResult result = await guard.ValidatePathAsync(
            "not-a-valid-guid",
            "/tmp/secret.txt",
            "read",
            "read_file_chunk");

        Assert.False(result.Allowed);

        Assert.Contains("Invalid campaign identifier", result.DenyReason, StringComparison.Ordinal);

        Assert.NotNull(result.Breach);

        Assert.Equal("PathEscape", result.Breach!.BreachType);

        Assert.False(repository.WasQueried);

        // Invalid campaign ids are log-only: persisting would violate the SanctumBreaches ->
        // Campaigns foreign key since no campaign row exists to reference.
        Assert.False(breachRepository.WasCalled);

    }

    /// <summary>
    /// A network check with no campaign context is allowed, and no row is ever read for it.
    /// </summary>
    /// <remarks>
    /// "No campaign was supplied" and "a campaign was supplied and did not resolve" are the two
    /// answers the loader exists to keep apart: the first has nothing to enforce and grants full
    /// permission, the second can never check a resolved config's restrictions and must deny. Only
    /// <c>ValidatePathAsync</c> had a test taking the first branch, so the other two gates could have
    /// fallen through to a deny - or, worse, reached this allow through the resolved path - without
    /// anything noticing. Asserting the repository was never asked is what separates "allowed with
    /// nothing to enforce" from "allowed because a lookup happened to come back empty".
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateNetworkAsync_NoCampaignContext_AllowsWithoutReadingACampaign(string campaignId)
    {

        StubCampaignRepository repository = new();

        StubSanctumBreachRepository breachRepository = new();

        SanctumGuard guard = new(
            repository,
            breachRepository,
            NullLogger<SanctumGuard>.Instance);

        SanctumResult result = await guard.ValidateNetworkAsync(
            campaignId,
            "https://example.invalid/resource",
            "fetch_url");

        Assert.True(result.Allowed);

        Assert.Null(result.Breach);

        Assert.False(repository.WasQueried);

        Assert.False(breachRepository.WasCalled);

    }

    /// <summary>
    /// The tool gate's half of the same distinction: no campaign context, nothing to disable.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateToolAsync_NoCampaignContext_AllowsWithoutReadingACampaign(string campaignId)
    {

        StubCampaignRepository repository = new();

        StubSanctumBreachRepository breachRepository = new();

        SanctumGuard guard = new(
            repository,
            breachRepository,
            NullLogger<SanctumGuard>.Instance);

        SanctumResult result = await guard.ValidateToolAsync(campaignId, "read_file_chunk");

        Assert.True(result.Allowed);

        Assert.Null(result.Breach);

        Assert.False(repository.WasQueried);

        Assert.False(breachRepository.WasCalled);

    }

    /// <summary>
    /// A malformed id is denied by the network and tool gates too, and never reaches a lookup.
    /// </summary>
    /// <remarks>
    /// The same classification the loader now shares with that gate, exercised through the two entry
    /// points that only had the "no context" side pinned above: a supplied id that cannot name a row
    /// has to deny rather than fall through to the full permission a blank one gets.
    /// </remarks>
    [Fact]
    public async Task Network_and_tool_gates_deny_a_malformed_campaign_id_before_any_lookup()
    {

        StubCampaignRepository repository = new();

        StubSanctumBreachRepository breachRepository = new();

        SanctumGuard guard = new(
            repository,
            breachRepository,
            NullLogger<SanctumGuard>.Instance);

        SanctumResult network = await guard.ValidateNetworkAsync(
            "not-a-valid-guid",
            "https://example.invalid/resource",
            "fetch_url");

        SanctumResult tool = await guard.ValidateToolAsync("not-a-valid-guid", "read_file_chunk");

        Assert.False(network.Allowed);

        Assert.Equal("NetworkEgress", network.Breach!.BreachType);

        Assert.False(tool.Allowed);

        Assert.Equal("DisabledTool", tool.Breach!.BreachType);

        Assert.False(repository.WasQueried);

        Assert.False(breachRepository.WasCalled);

    }

    private sealed class StubCampaignRepository : ICampaignRepository
    {

        public bool WasQueried { get; private set; }

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {

            WasQueried = true;

            return Task.FromResult<Campaign?>(null);

        }

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

    }

    private sealed class StubSanctumBreachRepository : ISanctumBreachRepository
    {

        public bool WasCalled { get; private set; }

        public Task RecordAsync(SanctumBreachRecord breach, int maxBreachCount, CancellationToken ct = default)
        {

            WasCalled = true;

            return Task.CompletedTask;

        }

        public Task<IReadOnlyList<SanctumBreachRecord>> QueryAsync(
            string campaignId,
            int limit,
            DateTimeOffset? before = null,
            string? toolName = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SanctumBreachRecord>>([]);

        public Task<int> GetCountAsync(string campaignId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<int> DeleteOldestAsync(string campaignId, int count, CancellationToken ct = default) =>
            Task.FromResult(0);

    }

}
