using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The decision the reference and refresh routes make when a Sanctum is asked whether a live attachment source may be read: which Campaign
/// governs (the Session's claims and the registered root that contains the path the source is spelled with, the stricter winning), what that
/// Campaign's mode lets a refusal cost, and the refusals no mode overrides.
/// </summary>
public sealed class AttachmentSourceSanctumTests
{
    private const string Surface = "attachment_reference";

    private static readonly Guid SessionId = Guid.Parse("0F1E2D3C-4B5A-4968-8776-655443322110");

    private static readonly Guid Bound = Guid.Parse("7E1D9C42-05B8-4F63-9A0E-3C8B27D641F5");

    private static readonly Guid Contained = Guid.Parse("1A2B3C4D-5E6F-4071-8293-A4B5C6D7E8F9");

    private static readonly AttachmentSourceRead Read = new("/work/app/link.txt", "/work/elsewhere/target.txt");

    [Fact]
    public async Task A_read_no_claim_names_is_unrestricted_and_the_guard_is_never_asked()
    {
        ScriptedGuard guard = new(SanctumDenial.Policy);

        bool allowed = await AuthorizeAsync(guard, new CampaignTable(), sessionCampaignId: null);

        Assert.True(allowed);

        Assert.Empty(guard.Calls);
    }

    [Fact]
    public async Task The_guard_judges_the_resolved_path_of_the_governing_campaign_and_the_resolver_is_asked_about_the_spelled_path()
    {
        ScriptedGuard guard = new(SanctumDenial.None);

        FixedCampaignResolver resolver = new(Bound);

        bool allowed = await AuthorizeAsync(guard, new CampaignTable(Build(Bound, SanctumMode.Strict)), sessionCampaignId: Bound, resolver);

        Assert.True(allowed);

        GuardCall call = Assert.Single(guard.Calls);

        Assert.Equal(Bound.ToString("D"), call.CampaignId);

        Assert.Equal(Read.CanonicalPath, call.Path);

        Assert.Equal("attachment source read", call.OperationType);

        Assert.Equal(Surface, call.ToolName);

        CanonicalCampaignResolutionRequest asked = Assert.IsType<CanonicalCampaignResolutionRequest>(resolver.LastRequest);

        Assert.Equal(SessionId, asked.SessionId);

        Assert.Equal(Bound, asked.ExplicitCampaignId);

        Assert.Equal(Read.SourcePath, asked.WorkingDirectory);
    }

    [Theory]
    [InlineData(SanctumMode.Strict, SanctumDenial.None, true)]
    [InlineData(SanctumMode.AuditOnly, SanctumDenial.None, true)]
    [InlineData(SanctumMode.Strict, SanctumDenial.Policy, false)]
    [InlineData(SanctumMode.AuditOnly, SanctumDenial.Policy, true)]
    [InlineData(SanctumMode.Strict, SanctumDenial.PolicyUnavailable, false)]
    [InlineData(SanctumMode.AuditOnly, SanctumDenial.PolicyUnavailable, false)]
    [InlineData(SanctumMode.Strict, SanctumDenial.AuditRecordFailed, false)]
    [InlineData(SanctumMode.AuditOnly, SanctumDenial.AuditRecordFailed, false)]
    public async Task The_governing_campaigns_mode_decides_what_a_refusal_costs_and_no_mode_overrides_an_integrity_denial(
        SanctumMode mode,
        SanctumDenial denial,
        bool expectedAllowed)
    {
        ScriptedGuard guard = new(denial);

        bool allowed = await AuthorizeAsync(guard, new CampaignTable(Build(Bound, mode)), sessionCampaignId: Bound, new FixedCampaignResolver(Bound));

        Assert.Equal(expectedAllowed, allowed);
    }

    [Theory]
    [InlineData(SanctumMode.Strict, false)]
    [InlineData(SanctumMode.AuditOnly, true)]
    public async Task A_campaign_reached_only_by_the_path_governs_with_its_own_mode(SanctumMode mode, bool expectedAllowed)
    {
        ScriptedGuard guard = new(SanctumDenial.Policy);

        CampaignTable campaigns = new(Build(Contained, mode)) { ContainingRoot = Contained };

        bool allowed = await AuthorizeAsync(guard, campaigns, sessionCampaignId: null);

        Assert.Equal(expectedAllowed, allowed);

        Assert.Equal(Contained.ToString("D"), Assert.Single(guard.Calls).CampaignId);

        Assert.Equal(Read.SourcePath, campaigns.ContainedPaths.Single());
    }

    [Fact]
    public async Task The_stricter_of_the_sessions_campaign_and_the_one_reached_by_path_governs()
    {
        ScriptedGuard guard = new(SanctumDenial.Policy);

        CampaignTable campaigns = new(Build(Bound, SanctumMode.AuditOnly), Build(Contained, SanctumMode.Strict)) { ContainingRoot = Contained };

        bool allowed = await AuthorizeAsync(guard, campaigns, sessionCampaignId: Bound, new FixedCampaignResolver(Bound));

        Assert.False(allowed);

        Assert.Equal(Contained.ToString("D"), Assert.Single(guard.Calls).CampaignId);
    }

    [Fact]
    public async Task A_campaign_whose_sanctum_cannot_be_read_refuses_the_read_in_every_mode_without_asking_the_guard()
    {
        ScriptedGuard guard = new(SanctumDenial.None);

        Campaign unreadable = Build(Bound, SanctumMode.AuditOnly);

        unreadable.SanctumConfigJson = "{ this is not a sanctum policy";

        bool allowed = await AuthorizeAsync(guard, new CampaignTable(unreadable), sessionCampaignId: Bound, new FixedCampaignResolver(Bound));

        Assert.False(allowed);

        Assert.Empty(guard.Calls);
    }

    [Fact]
    public async Task A_campaign_with_its_sanctum_switched_off_lets_the_read_through_on_the_guards_word()
    {
        ScriptedGuard guard = new(SanctumDenial.None);

        bool allowed = await AuthorizeAsync(guard, new CampaignTable(Build(Bound, SanctumMode.Strict, enabled: false)), sessionCampaignId: Bound, new FixedCampaignResolver(Bound));

        Assert.True(allowed);
    }

    private static async Task<bool> AuthorizeAsync(
        ScriptedGuard guard,
        CampaignTable campaigns,
        Guid? sessionCampaignId,
        FixedCampaignResolver? resolver = null)
    {
        AttachmentSourcePathAuthorizer authorizer = AttachmentSourceSanctum.Authorizer(
            SessionId,
            sessionCampaignId,
            Surface,
            resolver ?? new FixedCampaignResolver(null),
            campaigns,
            guard,
            NullLogger.Instance);

        return await authorizer(Read, CancellationToken.None);
    }

    private static Campaign Build(Guid id, SanctumMode mode, bool enabled = true) =>
        new()
        {
            Id = id,
            Name = "campaign-" + id.ToString("N")[..8],
            NameLower = "campaign-" + id.ToString("N")[..8],
            Path = "/work/" + id.ToString("N")[..8],
            Type = WorkspaceType.Campaign,
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(new SanctumConfig { Enabled = enabled, Mode = mode }),
        };

    public enum SanctumDenial
    {
        None,

        Policy,

        PolicyUnavailable,

        AuditRecordFailed,
    }

    private sealed record GuardCall(string CampaignId, string Path, string OperationType, string ToolName);

    private sealed class ScriptedGuard(SanctumDenial denial) : ISanctumGuard
    {
        public List<GuardCall> Calls { get; } = [];

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default)
        {
            Calls.Add(new GuardCall(campaignId, requestedPath, operationType, toolName));

            return Task.FromResult(
                denial switch
                {
                    SanctumDenial.None => new SanctumResult { Allowed = true },
                    SanctumDenial.Policy => new SanctumResult { Allowed = false, DenialKind = SanctumDenialKind.Policy },
                    SanctumDenial.PolicyUnavailable => new SanctumResult { Allowed = false, DenialKind = SanctumDenialKind.PolicyUnavailable },
                    _ => new SanctumResult { Allowed = false, DenialKind = SanctumDenialKind.AuditRecordFailed },
                });
        }

        public Task<SanctumResult> ValidateNetworkAsync(string campaignId, string url, string toolName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(string? workspaceRoot, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(string? workspaceRoot, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
