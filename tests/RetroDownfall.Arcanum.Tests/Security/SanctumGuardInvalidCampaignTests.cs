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
