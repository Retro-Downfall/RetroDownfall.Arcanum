using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The guard's own lookups by workspace directory (the child-process resource limits, the child-process path boundary, and the
/// resource-limit breach record) find the Campaign the way a turn does: by the registered root that contains the directory, and by the
/// physical identity of the directory when it is registered, not by an exact spelling of the registered path.
/// </summary>
public sealed class SanctumGuardWorkspaceCampaignTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcanum-guard-workspace-" + Guid.NewGuid().ToString("N"));

    private string Below => Path.Combine(_root, "src", "deep");

    [Fact]
    public async Task A_directory_below_a_campaign_root_gets_the_campaigns_resource_limits()
    {
        Campaign campaign = CreateCampaign(new SanctumConfig { Enabled = true, ResourceLimits = new ResourceLimits(MaxFileWriteMb: 7) });

        ResourceLimits limits = await CreateGuard(new ContainingRepository(campaign))
            .GetEffectiveResourceLimitsForWorkspaceAsync(Below);

        Assert.Equal(7, limits.MaxFileWriteMb);
    }

    [Fact]
    public async Task A_directory_below_a_campaign_root_gets_the_campaigns_child_process_boundary()
    {
        Campaign campaign = CreateCampaign(new SanctumConfig { Enabled = true, EnforcePathBoundary = true, AllowedPaths = ["/opt/shared"] });

        SanctumChildProcessBoundary? boundary = await CreateGuard(new ContainingRepository(campaign))
            .GetChildProcessBoundaryForWorkspaceAsync(Below);

        Assert.NotNull(boundary);

        Assert.True(boundary.PathBoundaryRequired);

        Assert.Equal(Path.GetFullPath(_root), boundary.WorkspaceRoot);

        Assert.Equal(["/opt/shared"], boundary.AllowedPaths);
    }

    [Fact]
    public async Task A_resource_limit_breach_in_a_directory_below_a_campaign_root_is_recorded_for_that_campaign()
    {
        Campaign campaign = CreateCampaign(new SanctumConfig { Enabled = true });

        InMemorySanctumBreachRepository breaches = new();

        await CreateGuard(new ContainingRepository(campaign), breaches)
            .RecordResourceLimitBreachAsync(Below, "execute_command", ResourceLimitKind.Cpu, "30s", "31s");

        SanctumBreachRecord record = Assert.Single(breaches.Records);

        Assert.Equal(campaign.Id.ToString("D"), record.CampaignId, ignoreCase: true);

        Assert.Equal("ResourceLimit", record.BreachType);
    }

    [Fact]
    public async Task A_directory_no_campaign_contains_gets_defaults_and_no_boundary_and_records_nothing()
    {
        Campaign campaign = CreateCampaign(new SanctumConfig { Enabled = true, ResourceLimits = new ResourceLimits(MaxFileWriteMb: 7) });

        string elsewhere = Path.Combine(Path.GetTempPath(), "arcanum-elsewhere-" + Guid.NewGuid().ToString("N"));

        InMemorySanctumBreachRepository breaches = new();

        SanctumGuard guard = CreateGuard(new ContainingRepository(campaign), breaches);

        Assert.Equal(100, (await guard.GetEffectiveResourceLimitsForWorkspaceAsync(elsewhere)).MaxFileWriteMb);

        Assert.Null(await guard.GetChildProcessBoundaryForWorkspaceAsync(elsewhere));

        await guard.RecordResourceLimitBreachAsync(elsewhere, "execute_command", ResourceLimitKind.Cpu, "30s", "31s");

        Assert.Empty(breaches.Records);
    }

    [Fact]
    public async Task A_registered_physical_identity_finds_the_campaign_a_path_spelling_would_miss()
    {
        Campaign campaign = CreateCampaign(new SanctumConfig { Enabled = true, ResourceLimits = new ResourceLimits(MaxFileWriteMb: 7) });

        // The containing lookup knows nothing of this spelling; only the registered identity of the directory does.
        FixedIdentityReader identities = new(Result<RegisteredCampaignIdentity?>.Success(Registered(campaign.Id)));

        SanctumGuard guard = CreateGuard(new IdentityOnlyRepository(campaign), identities: identities);

        Assert.Equal(7, (await guard.GetEffectiveResourceLimitsForWorkspaceAsync("/Case/Variant/Of/Root")).MaxFileWriteMb);

        Assert.NotNull(await guard.GetChildProcessBoundaryForWorkspaceAsync("/Case/Variant/Of/Root"));

        Assert.Equal(2, identities.Calls);
    }

    [Fact]
    public async Task An_identity_the_reader_cannot_resolve_falls_back_to_the_registered_root_that_contains_the_directory()
    {
        Campaign campaign = CreateCampaign(new SanctumConfig { Enabled = true, ResourceLimits = new ResourceLimits(MaxFileWriteMb: 7) });

        foreach (Result<RegisteredCampaignIdentity?> answer in new[]
        {
            Result<RegisteredCampaignIdentity?>.Success(null),
            Result<RegisteredCampaignIdentity?>.Failure(new Error(ErrorCodes.Covenant.IntegrityFailure, "malformed")),
        })
        {
            SanctumGuard guard = CreateGuard(new ContainingRepository(campaign), identities: new FixedIdentityReader(answer));

            Assert.Equal(7, (await guard.GetEffectiveResourceLimitsForWorkspaceAsync(Below)).MaxFileWriteMb);
        }
    }

    [Fact]
    public async Task A_blank_workspace_root_consults_nothing()
    {
        FixedIdentityReader identities = new(Result<RegisteredCampaignIdentity?>.Success(null));

        SanctumGuard guard = CreateGuard(new ContainingRepository(CreateCampaign(new SanctumConfig())), identities: identities);

        Assert.Null(await guard.GetChildProcessBoundaryForWorkspaceAsync("  "));

        _ = await guard.GetEffectiveResourceLimitsForWorkspaceAsync(null);

        Assert.Equal(0, identities.Calls);
    }

    // The physical identity of a root is only known once the root is registered, and nothing in this release registers one, so the
    // registered-root lookup is the registry every installation has. When both answer and they name different Campaigns (an
    // identity-registered Campaign with a path-only Campaign nested inside it), the stricter Sanctum governs the directory.
    [Theory]
    [InlineData(SanctumMode.AuditOnly, SanctumMode.Strict, 7)]
    [InlineData(SanctumMode.Strict, SanctumMode.AuditOnly, 3)]
    public async Task A_path_only_campaign_nested_in_an_identity_registered_one_is_governed_by_the_stricter_of_the_two(
        SanctumMode outerMode,
        SanctumMode innerMode,
        int expectedMaxFileWriteMb)
    {
        Campaign outer = CreateCampaign(new SanctumConfig { Enabled = true, Mode = outerMode, ResourceLimits = new ResourceLimits(MaxFileWriteMb: 3) });

        Campaign inner = CreateNestedCampaign(new SanctumConfig { Enabled = true, Mode = innerMode, ResourceLimits = new ResourceLimits(MaxFileWriteMb: 7) });

        FixedIdentityReader identities = new(Result<RegisteredCampaignIdentity?>.Success(Registered(outer.Id)));

        SanctumGuard guard = CreateGuard(new NestedRepository(outer, inner), identities: identities);

        string workspace = Path.Combine(inner.Path, "src");

        Assert.Equal(expectedMaxFileWriteMb, (await guard.GetEffectiveResourceLimitsForWorkspaceAsync(workspace)).MaxFileWriteMb);

        Assert.NotNull(await guard.GetChildProcessBoundaryForWorkspaceAsync(workspace));
    }

    [Fact]
    public async Task Equally_strict_campaigns_that_claim_one_directory_are_governed_by_the_identity_registered_one()
    {
        Campaign outer = CreateCampaign(new SanctumConfig { Enabled = true, ResourceLimits = new ResourceLimits(MaxFileWriteMb: 3) });

        Campaign inner = CreateNestedCampaign(new SanctumConfig { Enabled = true, ResourceLimits = new ResourceLimits(MaxFileWriteMb: 7) });

        SanctumGuard guard = CreateGuard(
            new NestedRepository(outer, inner),
            identities: new FixedIdentityReader(Result<RegisteredCampaignIdentity?>.Success(Registered(outer.Id))));

        Assert.Equal(3, (await guard.GetEffectiveResourceLimitsForWorkspaceAsync(Path.Combine(inner.Path, "src"))).MaxFileWriteMb);
    }

    [Fact]
    public async Task A_path_only_campaign_nested_in_an_identity_registered_one_with_the_sanctum_off_is_still_governed_by_the_outer_strict_one()
    {
        Campaign outer = CreateCampaign(new SanctumConfig { Enabled = true, ResourceLimits = new ResourceLimits(MaxFileWriteMb: 3) });

        Campaign inner = CreateNestedCampaign(new SanctumConfig { Enabled = false, ResourceLimits = new ResourceLimits(MaxFileWriteMb: 7) });

        SanctumGuard guard = CreateGuard(
            new NestedRepository(outer, inner),
            identities: new FixedIdentityReader(Result<RegisteredCampaignIdentity?>.Success(Registered(outer.Id))));

        Assert.Equal(3, (await guard.GetEffectiveResourceLimitsForWorkspaceAsync(Path.Combine(inner.Path, "src"))).MaxFileWriteMb);
    }

    public void Dispose() =>
        _ = TestDirectoryCleanup.TryDelete(_root, nameof(SanctumGuardWorkspaceCampaignTests));

    private static RegisteredCampaignIdentity Registered(Guid campaignId) =>
        new(campaignId, CampaignPathIdentityPolicy.Version, 1, 1, new CovenantDigest(new byte[32]));

    private static SanctumGuard CreateGuard(
        ICampaignRepository campaigns,
        ISanctumBreachRepository? breaches = null,
        ICampaignPathIdentityReader? identities = null) =>
        new(campaigns, breaches ?? new InMemorySanctumBreachRepository(), NullLogger<SanctumGuard>.Instance, identities ?? UnregisteredCampaignRoots.Instance, new FakeDnsResolver());

    private Campaign CreateCampaign(SanctumConfig config)
    {
        _ = Directory.CreateDirectory(_root);

        return new Campaign
        {
            Id = Guid.NewGuid(),
            Name = "workspace-campaign",
            NameLower = "workspace-campaign",
            Path = _root,
            Type = WorkspaceType.Campaign,
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(config),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private Campaign CreateNestedCampaign(SanctumConfig config)
    {
        string nested = Path.Combine(_root, "nested");

        _ = Directory.CreateDirectory(nested);

        return new Campaign
        {
            Id = Guid.NewGuid(),
            Name = "nested-campaign",
            NameLower = "nested-campaign",
            Path = nested,
            Type = WorkspaceType.Campaign,
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(config),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private sealed class FixedIdentityReader(Result<RegisteredCampaignIdentity?> answer) : ICampaignPathIdentityReader
    {
        public int Calls { get; private set; }

        public ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(string? workingDirectory, CancellationToken cancellationToken)
        {
            Calls++;

            return ValueTask.FromResult(answer);
        }

        public ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(Guid campaignId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(null));
    }

    /// <summary>A store that finds its one Campaign by id and by the root that contains a directory, as the real one does.</summary>
    private class ContainingRepository(Campaign campaign) : ICampaignRepository
    {
        public virtual Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(id == campaign.Id ? campaign : null);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(string.Equals(Path.GetFullPath(path), Path.GetFullPath(campaign.Path), StringComparison.Ordinal) ? campaign : null);

        public virtual Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(campaign.Path));

            string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            return Task.FromResult<Campaign?>(
                candidate == root || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? campaign : null);
        }

        public Task<Campaign?> GetBySpelledPathAsync(string path, CancellationToken cancellationToken = default) =>
            GetByContainedPathAsync(path, cancellationToken);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(Campaign added, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<Campaign>> UpdateHeaderAsync(Campaign updated, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<Campaign>> UpdateSettingsAsync(Campaign updated, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<Campaign>> UpdateSanctumAsync(Campaign updated, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<Campaign>> UpdateWorkspaceAsync(Campaign updated, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(1);
    }

    private sealed class IdentityOnlyRepository(Campaign campaign) : ContainingRepository(campaign)
    {
        public override Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);
    }

    /// <summary>Two Campaigns, one nested in the other, found by id and by the deepest registered root that contains a directory.</summary>
    private sealed class NestedRepository : ContainingRepository
    {
        private readonly Campaign _outer;

        private readonly Campaign _inner;

        public NestedRepository(Campaign outer, Campaign inner)
            : base(outer)
        {
            _outer = outer;

            _inner = inner;
        }

        public override Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default)
        {
            string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            string innerRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_inner.Path));

            return candidate == innerRoot || candidate.StartsWith(innerRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? Task.FromResult<Campaign?>(_inner)
                : base.GetByContainedPathAsync(path, cancellationToken);
        }

        public override Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(id == _inner.Id ? _inner : id == _outer.Id ? _outer : null);
    }
}
