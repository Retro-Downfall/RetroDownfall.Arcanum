using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// How the Campaign whose Sanctum governs a turn is found. Every source the request names (the canonical binding, the Session, the
/// explicit identifier, the registered root that contains the working directory) is a claim about which Campaign the turn belongs to, and
/// the stricter Sanctum among the claims governs: a resolution the canonical seam refused, or one that disagrees with the registered root,
/// never governs less strictly than the sources it set aside.
/// </summary>
public sealed class SanctumCampaignResolutionTests
{
    private static readonly Guid Bound = Guid.Parse("7E1D9C42-05B8-4F63-9A0E-3C8B27D641F5");

    private static readonly Guid Contained = Guid.Parse("1A2B3C4D-5E6F-4071-8293-A4B5C6D7E8F9");

    private static readonly Guid Named = Guid.Parse("3F2E1D0C-9B8A-4756-8493-A2B1C0D9E8F7");

    private static readonly Guid FromSession = Guid.Parse("5C4B3A29-1807-4F6E-9D5C-4B3A29180716");

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_known_canonical_campaign_with_no_working_directory_is_the_answer_and_nothing_else_is_asked()
    {
        Table campaigns = new(Open(Bound), Strict(Contained));

        Resolver resolver = new(Result<CanonicalCampaignContext>.Success(CanonicalFor(Named)));

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), Named, null, CanonicalFor(Bound), resolver, campaigns, Token);

        Assert.Equal(Bound, resolved.CampaignId);

        Assert.False(resolved.PolicyUnreadable);

        Assert.Equal(0, resolver.Calls);

        Assert.Equal(0, campaigns.ContainedLookups);
    }

    // The registered root that contains the directory is a second claim, and a Campaign nested in the canonical one is the case where
    // the two differ. The canonical binding that rests on a registered identity is not better evidence of where the directory belongs
    // than a root registered by path, so the stricter Sanctum governs.
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public async Task A_known_canonical_campaign_and_the_root_containing_the_directory_are_governed_by_the_stricter_of_the_two(
        bool canonicalIsStrict,
        bool containedIsStrict,
        bool expectContained)
    {
        Table campaigns = new(
            canonicalIsStrict ? Strict(Bound) : Open(Bound),
            containedIsStrict ? Strict(Contained) : Open(Contained));

        campaigns.ContainingRoot = Contained;

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            null, null, "/work/app/nested", CanonicalFor(Bound), RefusingCampaignResolver.Instance, campaigns, Token);

        Assert.Equal(expectContained ? Contained : Bound, resolved.CampaignId);

        Assert.Equal(1, campaigns.ContainedLookups);
    }

    [Fact]
    public async Task A_known_canonical_campaign_that_is_as_strict_as_the_containing_root_stays_the_answer()
    {
        Table campaigns = new(Strict(Bound), Strict(Contained));

        campaigns.ContainingRoot = Contained;

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            null, null, "/work/app/nested", CanonicalFor(Bound), RefusingCampaignResolver.Instance, campaigns, Token);

        Assert.Equal(Bound, resolved.CampaignId);
    }

    [Fact]
    public async Task A_known_global_only_context_falls_to_the_registered_root_that_contains_the_directory_and_never_to_the_explicit_identifier()
    {
        Table campaigns = new(Strict(Contained), Strict(Named));

        campaigns.ContainingRoot = Contained;

        SanctumCampaignResolution.Resolved containing = await SanctumCampaignResolution.ResolveAsync(
            null, Named, "/work/app/src", CanonicalCampaignContext.GlobalOnly, RefusingCampaignResolver.Instance, campaigns, Token);

        Assert.Equal(Contained, containing.CampaignId);

        Assert.NotNull(containing.Loaded);

        SanctumCampaignResolution.Resolved nothing = await SanctumCampaignResolution.ResolveAsync(
            null, Named, null, CanonicalCampaignContext.GlobalOnly, RefusingCampaignResolver.Instance, new Table(Strict(Named)), Token);

        // The canonical answer was "no Campaign", so the named identifier is not substituted for it.
        Assert.Null(nothing.CampaignId);

        Assert.False(nothing.PolicyUnreadable);
    }

    [Fact]
    public async Task An_uninitialised_context_counts_as_no_answer()
    {
        Resolver resolver = new(Result<CanonicalCampaignContext>.Success(CanonicalFor(Bound)));

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            null, null, "/work/app", default(CanonicalCampaignContext), resolver, new Table(Open(Bound)), Token);

        Assert.Equal(Bound, resolved.CampaignId);

        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task A_resolver_answer_is_asked_with_exactly_the_sources_the_request_carries()
    {
        Resolver resolver = new(Result<CanonicalCampaignContext>.Success(CanonicalCampaignContext.GlobalOnly));

        Guid session = Guid.NewGuid();

        _ = await SanctumCampaignResolution.ResolveAsync(session, Named, "  ", null, resolver, new Table(), Token);

        CanonicalCampaignResolutionRequest asked = Assert.IsType<CanonicalCampaignResolutionRequest>(resolver.LastRequest);

        Assert.Equal(session, asked.SessionId);

        Assert.Equal(Named, asked.ExplicitCampaignId);

        Assert.Null(asked.WorkingDirectory);
    }

    [Fact]
    public async Task A_request_with_no_sources_consults_nothing()
    {
        Resolver resolver = new(Result<CanonicalCampaignContext>.Success(CanonicalCampaignContext.GlobalOnly));

        Table campaigns = new(Strict(Contained));

        campaigns.ContainingRoot = Contained;

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            null, null, " ", null, resolver, campaigns, Token);

        Assert.Null(resolved.CampaignId);

        Assert.False(resolved.PolicyUnreadable);

        Assert.Equal(0, resolver.Calls);

        Assert.Equal(0, campaigns.ContainedLookups);
    }

    // A refusal of the combined resolution is the canonical seam saying the sources disagree. It says nothing about which of them is
    // right, so every source is asked on its own and the stricter Sanctum among the Campaigns they name governs.
    [Fact]
    public async Task A_refused_resolution_is_governed_by_the_session_s_strict_campaign_when_the_directory_is_in_no_registered_root()
    {
        Table campaigns = new(Strict(FromSession));

        ShapeResolver resolver = ShapeResolver.RefusingTheCombination(session: FromSession);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), null, "/somewhere/else", null, resolver, campaigns, Token);

        Assert.Equal(FromSession, resolved.CampaignId);

        Assert.False(resolved.PolicyUnreadable);
    }

    [Fact]
    public async Task A_refused_resolution_is_governed_by_the_session_s_strict_campaign_when_the_directory_is_in_a_campaign_with_the_sanctum_off()
    {
        Table campaigns = new(Strict(FromSession), Open(Contained));

        campaigns.ContainingRoot = Contained;

        ShapeResolver resolver = ShapeResolver.RefusingTheCombination(session: FromSession);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), null, "/work/open/src", null, resolver, campaigns, Token);

        Assert.Equal(FromSession, resolved.CampaignId);
    }

    [Fact]
    public async Task A_refused_resolution_is_governed_by_the_explicit_strict_campaign_when_the_directory_is_in_a_campaign_with_the_sanctum_off()
    {
        Table campaigns = new(Strict(Named), Open(Contained));

        campaigns.ContainingRoot = Contained;

        ShapeResolver resolver = ShapeResolver.RefusingTheCombination(explicitCampaign: Named);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            null, Named, "/work/open/src", null, resolver, campaigns, Token);

        Assert.Equal(Named, resolved.CampaignId);
    }

    [Fact]
    public async Task A_refused_resolution_is_governed_by_the_strict_root_the_directory_resolves_to_when_the_session_s_campaign_has_the_sanctum_off()
    {
        Table campaigns = new(Open(FromSession), Strict(Contained));

        campaigns.ContainingRoot = Contained;

        ShapeResolver resolver = ShapeResolver.RefusingTheCombination(session: FromSession);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), null, "/work/strict/src", null, resolver, campaigns, Token);

        Assert.Equal(Contained, resolved.CampaignId);
    }

    [Fact]
    public async Task A_refused_resolution_is_governed_by_the_strict_campaign_of_the_directory_s_registered_identity()
    {
        Table campaigns = new(Open(FromSession), Strict(Bound));

        ShapeResolver resolver = ShapeResolver.RefusingTheCombination(session: FromSession, directory: Bound);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), null, "/work/strict/src", null, resolver, campaigns, Token);

        Assert.Equal(Bound, resolved.CampaignId);
    }

    [Fact]
    public async Task A_refused_resolution_whose_campaigns_all_have_the_sanctum_off_is_governed_by_no_sanctum_at_all()
    {
        Table campaigns = new(Open(FromSession), Open(Contained));

        campaigns.ContainingRoot = Contained;

        ShapeResolver resolver = ShapeResolver.RefusingTheCombination(session: FromSession);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), null, "/work/open/src", null, resolver, campaigns, Token);

        Assert.NotNull(resolved.CampaignId);

        Assert.False(resolved.PolicyUnreadable);

        Assert.Equal(SanctumStrictness.Disabled, CampaignRepository.GetSanctumStrictness(resolved.Loaded!));
    }

    [Fact]
    public async Task An_audit_only_campaign_governs_over_one_with_the_sanctum_off_and_yields_to_a_strict_one()
    {
        Table campaigns = new(Open(FromSession), AuditOnly(Contained), Strict(Named));

        campaigns.ContainingRoot = Contained;

        ShapeResolver resolver = ShapeResolver.RefusingTheCombination(session: FromSession);

        SanctumCampaignResolution.Resolved audit = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), null, "/work/audit/src", null, resolver, campaigns, Token);

        Assert.Equal(Contained, audit.CampaignId);

        SanctumCampaignResolution.Resolved strict = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), Named, "/work/audit/src", null, ShapeResolver.RefusingTheCombination(session: FromSession, explicitCampaign: Named), campaigns, Token);

        Assert.Equal(Named, strict.CampaignId);
    }

    // Whether a Campaign's Sanctum was switched on is unknowable when its configuration cannot be read, and that is never read as off.
    [Fact]
    public async Task A_campaign_whose_sanctum_cannot_be_read_governs_and_is_reported_unreadable()
    {
        Campaign unreadable = Strict(FromSession);

        unreadable.SanctumConfigJson = "{ this is not json";

        Table campaigns = new(unreadable, Strict(Contained));

        campaigns.ContainingRoot = Contained;

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), null, "/work/strict/src", null, ShapeResolver.RefusingTheCombination(session: FromSession), campaigns, Token);

        Assert.Equal(FromSession, resolved.CampaignId);

        Assert.True(resolved.PolicyUnreadable);
    }

    [Fact]
    public async Task A_campaign_that_cannot_be_loaded_is_reported_unreadable()
    {
        Table campaigns = new(Open(Contained));

        campaigns.ContainingRoot = Contained;

        campaigns.Throwing.Add(FromSession);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), null, "/work/open/src", null, ShapeResolver.RefusingTheCombination(session: FromSession), campaigns, Token);

        Assert.Equal(FromSession, resolved.CampaignId);

        Assert.True(resolved.PolicyUnreadable);
    }

    [Fact]
    public async Task An_explicit_identifier_no_campaign_answers_to_is_still_named_and_reported_unreadable()
    {
        Table campaigns = new();

        ShapeResolver resolver = ShapeResolver.RefusingTheCombination(missingExplicit: Named);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            null, Named, null, null, resolver, campaigns, Token);

        Assert.Equal(Named, resolved.CampaignId);

        Assert.True(resolved.PolicyUnreadable);
    }

    [Theory]
    [InlineData(ErrorCodes.Session.NotFound)]
    [InlineData(ErrorCodes.Session.CampaignBindingRequired)]
    [InlineData(ErrorCodes.Covenant.CampaignBindingConflict)]
    [InlineData(ErrorCodes.Campaign.NotFound)]
    public async Task A_source_the_resolver_cannot_bind_names_no_campaign(string code)
    {
        Table campaigns = new(Strict(Contained));

        campaigns.ContainingRoot = Contained;

        Resolver resolver = new(Result<CanonicalCampaignContext>.Failure(new Error(code, "unbound")));

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), null, "/work/strict/src", null, resolver, campaigns, Token);

        Assert.Equal(Contained, resolved.CampaignId);

        Assert.False(resolved.PolicyUnreadable);
    }

    [Fact]
    public async Task A_resolver_that_fails_for_a_reason_other_than_an_unbound_source_leaves_the_sanctum_unreadable()
    {
        Table campaigns = new(Open(Contained));

        campaigns.ContainingRoot = Contained;

        Resolver resolver = new(Result<CanonicalCampaignContext>.Failure(new Error(ErrorCodes.Covenant.IntegrityFailure, "malformed")));

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), null, "/work/open/src", null, resolver, campaigns, Token);

        Assert.True(resolved.PolicyUnreadable);
    }

    // A Session's immutable binding is written when its first turn begins, so a Session created and not yet used has no binding row and the
    // reader reports it as an integrity failure. It is bound to nothing yet: it names no Campaign, and the other sources still claim.
    [Fact]
    public async Task A_session_with_no_binding_row_names_no_campaign_and_the_other_sources_still_govern()
    {
        Table campaigns = new(Strict(Named));

        ShapeResolver resolver = ShapeResolver.RefusingTheCombination(
            explicitCampaign: Named,
            sessionFailure: ErrorCodes.Covenant.IntegrityFailure);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), Named, "/somewhere/else", null, resolver, campaigns, Token);

        Assert.Equal(Named, resolved.CampaignId);

        Assert.False(resolved.PolicyUnreadable);
    }

    [Fact]
    public async Task A_session_with_no_binding_row_and_nothing_else_to_claim_binds_the_turn_to_no_campaign()
    {
        ShapeResolver resolver = ShapeResolver.RefusingTheCombination(sessionFailure: ErrorCodes.Covenant.IntegrityFailure);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), null, null, null, resolver, new Table(), Token);

        Assert.Null(resolved.CampaignId);

        Assert.False(resolved.PolicyUnreadable);
    }

    [Fact]
    public async Task A_malformed_registry_of_root_identities_leaves_the_sanctum_unreadable()
    {
        Table campaigns = new(Open(FromSession));

        ShapeResolver resolver = ShapeResolver.RefusingTheCombination(
            session: FromSession,
            directoryFailure: ErrorCodes.Covenant.IntegrityFailure);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), null, "/work/open/src", null, resolver, campaigns, Token);

        Assert.True(resolved.PolicyUnreadable);
    }

    private static Campaign Strict(Guid id) => Build(id, new SanctumConfig { Enabled = true, Mode = SanctumMode.Strict });

    private static Campaign AuditOnly(Guid id) => Build(id, new SanctumConfig { Enabled = true, Mode = SanctumMode.AuditOnly });

    private static Campaign Open(Guid id) => Build(id, new SanctumConfig { Enabled = false });

    private static Campaign Build(Guid id, SanctumConfig config) =>
        new()
        {
            Id = id,
            Name = "campaign-" + id.ToString("N")[..8],
            NameLower = "campaign-" + id.ToString("N")[..8],
            Path = "/work/" + id.ToString("N")[..8],
            Type = WorkspaceType.Campaign,
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(config),
        };

    private static CanonicalCampaignContext CanonicalFor(Guid campaignId) =>
        CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest: null);

    private sealed class Resolver(Result<CanonicalCampaignContext> answer) : ICanonicalCampaignContextResolver
    {
        public int Calls { get; private set; }

        public CanonicalCampaignResolutionRequest? LastRequest { get; private set; }

        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
            CanonicalCampaignResolutionRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;

            LastRequest = request;

            return ValueTask.FromResult(answer);
        }
    }

    /// <summary>
    /// A resolver that behaves as the real one does for the question being asked: it refuses a request that carries more than one source
    /// (they disagree), and answers each single source with the Campaign that source names.
    /// </summary>
    private sealed class ShapeResolver : ICanonicalCampaignContextResolver
    {
        private Guid? _session;

        private Guid? _explicitCampaign;

        private Guid? _directory;

        private Guid? _missingExplicit;

        private string? _sessionFailure;

        private string? _directoryFailure;

        public static ShapeResolver RefusingTheCombination(
            Guid? session = null,
            Guid? explicitCampaign = null,
            Guid? directory = null,
            Guid? missingExplicit = null,
            string? sessionFailure = null,
            string? directoryFailure = null) =>
            new()
            {
                _session = session,
                _explicitCampaign = explicitCampaign,
                _directory = directory,
                _missingExplicit = missingExplicit,
                _sessionFailure = sessionFailure,
                _directoryFailure = directoryFailure,
            };

        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
            CanonicalCampaignResolutionRequest request,
            CancellationToken cancellationToken)
        {
            int sources = (request.SessionId is null ? 0 : 1)
                + (request.ExplicitCampaignId is null ? 0 : 1)
                + (string.IsNullOrWhiteSpace(request.WorkingDirectory) ? 0 : 1);

            if (sources > 1)
            {
                return Refused(ErrorCodes.Covenant.CampaignBindingConflict);
            }

            if (request.SessionId is not null)
            {
                return _sessionFailure is { } sessionFailure ? Refused(sessionFailure) : Answer(_session);
            }

            if (request.ExplicitCampaignId is { } explicitId)
            {
                return _missingExplicit == explicitId ? Refused(ErrorCodes.Campaign.NotFound) : Answer(_explicitCampaign);
            }

            return _directoryFailure is { } directoryFailure ? Refused(directoryFailure) : Answer(_directory);
        }

        private static ValueTask<Result<CanonicalCampaignContext>> Answer(Guid? campaignId) =>
            ValueTask.FromResult(
                Result<CanonicalCampaignContext>.Success(
                    campaignId is { } id
                        ? CanonicalFor(id)
                        : CanonicalCampaignContext.GlobalOnly));

        private static ValueTask<Result<CanonicalCampaignContext>> Refused(string code) =>
            ValueTask.FromResult(Result<CanonicalCampaignContext>.Failure(new Error(code, "refused")));
    }

    /// <summary>A Campaign table that finds its rows by identifier and answers one registered root as the one containing a directory.</summary>
    private sealed class Table : ICampaignRepository
    {
        private readonly Dictionary<Guid, Campaign> _rows;

        public Table(params Campaign[] rows)
        {
            _rows = rows.ToDictionary(static row => row.Id);
        }

        public Guid? ContainingRoot { get; set; }

        public HashSet<Guid> Throwing { get; } = [];

        public int ContainedLookups { get; private set; }

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (Throwing.Contains(id))
            {
                throw new IOException("simulated Campaign table failure");
            }

            return Task.FromResult(_rows.GetValueOrDefault(id));
        }

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default)
        {
            ContainedLookups++;

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
}
