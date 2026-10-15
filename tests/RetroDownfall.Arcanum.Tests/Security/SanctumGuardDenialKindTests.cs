using System.Net;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// What a refusal of the real guard means to <see cref="SanctumEnforcement.AllowsContinuation"/>. A policy breach is overridden by an
/// audit-only Sanctum because the breach row it keeps is the control; a guard that could not read the Sanctum, or could not keep the row,
/// has no control left and refuses in both modes.
/// </summary>
public sealed class SanctumGuardDenialKindTests : IDisposable
{
    private const string OffListUrl = "https://off-list.test/page";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcanum-guard-denial-kind-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("not-a-valid-guid")]
    [InlineData("00000000-0000-0000-0000-00000000dead")]
    public async Task A_campaign_the_guard_cannot_read_is_refused_by_every_gate_in_both_modes(string campaignId)
    {
        SanctumGuard guard = CreateGuard(CreateCampaign(SanctumMode.AuditOnly), new ThrowingBreachRepository());

        SanctumResult path = await guard.ValidatePathAsync(campaignId, Path.Combine(_root, "x.txt"), "read", "read_file_chunk");

        SanctumResult network = await guard.ValidateNetworkAsync(campaignId, OffListUrl, "read_url");

        SanctumResult tool = await guard.ValidateToolAsync(campaignId, "write_file");

        foreach (SanctumResult result in new[] { path, network, tool })
        {
            Assert.False(result.Allowed);

            Assert.Equal(SanctumDenialKind.PolicyUnavailable, result.DenialKind);

            Assert.False(SanctumEnforcement.AllowsContinuation(result, SanctumMode.Strict));

            Assert.False(SanctumEnforcement.AllowsContinuation(result, SanctumMode.AuditOnly));
        }
    }

    [Theory]
    [InlineData(SanctumMode.Strict, false)]
    [InlineData(SanctumMode.AuditOnly, true)]
    public async Task A_recorded_policy_breach_is_a_policy_refusal_that_only_an_audit_only_sanctum_overrides(SanctumMode mode, bool continues)
    {
        Campaign campaign = CreateCampaign(mode);

        InMemorySanctumBreachRepository breaches = new();

        SanctumGuard guard = CreateGuard(campaign, breaches);

        SanctumResult network = await guard.ValidateNetworkAsync(campaign.Id.ToString("D"), OffListUrl, "read_url");

        SanctumResult path = await guard.ValidatePathAsync(campaign.Id.ToString("D"), Path.Combine(Path.GetTempPath(), "outside.txt"), "read", "read_file_chunk");

        SanctumResult tool = await guard.ValidateToolAsync(campaign.Id.ToString("D"), "write_file");

        foreach (SanctumResult result in new[] { network, path, tool })
        {
            Assert.False(result.Allowed);

            Assert.Equal(SanctumDenialKind.Policy, result.DenialKind);

            Assert.Equal(continues, SanctumEnforcement.AllowsContinuation(result, mode));
        }

        Assert.Equal(3, breaches.Records.Count);
    }

    [Fact]
    public async Task A_breach_that_cannot_be_recorded_is_refused_even_by_an_audit_only_sanctum_and_is_logged()
    {
        Campaign campaign = CreateCampaign(SanctumMode.AuditOnly);

        CapturedLogProvider logs = new();

        SanctumGuard guard = CreateGuard(campaign, new ThrowingBreachRepository(), logs);

        SanctumResult network = await guard.ValidateNetworkAsync(campaign.Id.ToString("D"), OffListUrl, "read_url");

        SanctumResult path = await guard.ValidatePathAsync(campaign.Id.ToString("D"), Path.Combine(Path.GetTempPath(), "outside.txt"), "read", "read_file_chunk");

        SanctumResult tool = await guard.ValidateToolAsync(campaign.Id.ToString("D"), "write_file");

        foreach (SanctumResult result in new[] { network, path, tool })
        {
            Assert.False(result.Allowed);

            Assert.Equal(SanctumDenialKind.AuditRecordFailed, result.DenialKind);

            Assert.False(SanctumEnforcement.AllowsContinuation(result, SanctumMode.AuditOnly));

            Assert.False(SanctumEnforcement.AllowsContinuation(result, SanctumMode.Strict));

            Assert.NotNull(result.Breach);
        }

        Assert.Equal(3, logs.Entries.Count(entry => entry.Level == LogLevel.Error && entry.Message.Contains("Failed to persist Sanctum breach", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_breach_that_cannot_be_recorded_is_still_refused_by_a_strict_sanctum()
    {
        Campaign campaign = CreateCampaign(SanctumMode.Strict);

        SanctumResult network = await CreateGuard(campaign, new ThrowingBreachRepository())
            .ValidateNetworkAsync(campaign.Id.ToString("D"), OffListUrl, "read_url");

        Assert.False(network.Allowed);

        Assert.False(SanctumEnforcement.AllowsContinuation(network, SanctumMode.Strict));
    }

    [Fact]
    public async Task A_cancelled_breach_write_still_propagates_instead_of_becoming_a_verdict()
    {
        Campaign campaign = CreateCampaign(SanctumMode.AuditOnly);

        SanctumGuard guard = CreateGuard(campaign, new ThrowingBreachRepository(new OperationCanceledException()));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => guard.ValidateNetworkAsync(campaign.Id.ToString("D"), OffListUrl, "read_url"));
    }

    [Fact]
    public async Task An_allowed_target_never_touches_the_breach_store()
    {
        Campaign campaign = CreateCampaign(SanctumMode.AuditOnly);

        ThrowingBreachRepository breaches = new();

        SanctumResult result = await CreateGuard(campaign, breaches)
            .ValidateNetworkAsync(campaign.Id.ToString("D"), "https://allowed.test/page", "read_url");

        Assert.True(result.Allowed);

        Assert.Equal(0, breaches.Attempts);
    }

    public void Dispose() =>
        _ = TestDirectoryCleanup.TryDelete(_root, nameof(SanctumGuardDenialKindTests));

    private static SanctumGuard CreateGuard(Campaign campaign, ISanctumBreachRepository breaches, CapturedLogProvider? logs = null)
    {
        FakeDnsResolver dns = new();

        dns.Add("allowed.test", IPAddress.Parse("93.184.216.34"));

        ILoggerFactory factory = logs is null
            ? LoggerFactory.Create(static _ => { })
            : LoggerFactory.Create(builder => builder.AddProvider(logs));

        return new SanctumGuard(new SingleCampaignRepository(campaign), breaches, factory.CreateLogger<SanctumGuard>(), UnregisteredCampaignRoots.Instance, dns);
    }

    private Campaign CreateCampaign(SanctumMode mode)
    {
        _ = Directory.CreateDirectory(_root);

        return new Campaign
        {
            Id = Guid.NewGuid(),
            Name = "denial-kind",
            NameLower = "denial-kind",
            Path = _root,
            Type = WorkspaceType.Campaign,
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(new SanctumConfig
            {
                Enabled = true,
                Mode = mode,
                EnforcePathBoundary = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["allowed.test"],
                DisabledTools = ["write_file"],
            }),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private sealed class ThrowingBreachRepository(Exception? failure = null) : ISanctumBreachRepository
    {
        public int Attempts { get; private set; }

        public Task RecordAsync(SanctumBreachRecord breach, int maxBreachCount, CancellationToken ct = default)
        {
            Attempts++;

            return Task.FromException(failure ?? new IOException("simulated breach store failure"));
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
