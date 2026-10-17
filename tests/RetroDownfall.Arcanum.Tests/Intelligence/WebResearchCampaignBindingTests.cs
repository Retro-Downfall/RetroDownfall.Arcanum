using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Which Campaign's Sanctum gates a research run's citations, and what the run reports for a citation it refuses. The Campaign is the one the
/// run is bound to (its Session, its explicit identifier, or the registered root that contains its working directory), exactly as it is for a
/// chat turn, and a citation the Sanctum or the outbound address guard refuses is reported as denied rather than kept as an empty source.
/// </summary>
public sealed class WebResearchCampaignBindingTests : IDisposable
{
    private const string AllowedCitation = "https://allowed.test/start";

    private const string OffListHop = "https://off-list.test/page";

    private const string OffListCitation = "https://off-list.test/direct";

    private const string PrivateCitation = "https://private.test/page";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcanum-research-binding-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _extraRoots = [];

    [Theory]
    [InlineData("")]
    [InlineData("src")]
    [InlineData("trailing")]
    public async Task A_working_directory_inside_a_strict_campaign_gates_every_citation_with_no_campaign_identifier(string variant)
    {
        Run run = Start(SanctumMode.Strict);

        run.Citations = [OffListCitation];

        string workingDirectory = variant switch
        {
            "src" => Directory.CreateDirectory(Path.Combine(_root, "src")).FullName,
            "trailing" => _root + Path.DirectorySeparatorChar,
            _ => _root,
        };

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: workingDirectory);

        Assert.Contains(frames, static frame => frame.Stage == "source_denied");

        Assert.Empty(run.Http.Requests);

        Assert.Single(run.Breaches.Records);

        Assert.DoesNotContain(OffListCitation, run.Synthesis.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_campaign_a_resolver_binds_the_run_to_governs_its_citations()
    {
        Run run = Start(SanctumMode.Strict);

        run.Citations = [OffListCitation];

        run.Resolver = new FixedResolver(Result<CanonicalCampaignContext>.Success(CanonicalFor(run.Campaign.Id)));

        // The working directory is nowhere near the Campaign: the Session or identifier the resolver bound is what counts.
        List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: Path.GetTempPath());

        Assert.Contains(frames, static frame => frame.Stage == "source_denied");

        Assert.Empty(run.Http.Requests);

        Assert.Equal(1, run.Resolver.Calls);
    }

    [Fact]
    public async Task A_run_whose_directory_no_registered_root_contains_is_not_gated()
    {
        Run run = Start(SanctumMode.Strict);

        run.Citations = [OffListCitation];

        string unregistered = Path.Combine(Path.GetTempPath(), "arcanum-research-unregistered-" + Guid.NewGuid().ToString("N"));

        _ = Directory.CreateDirectory(unregistered);

        try
        {
            List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: unregistered);

            Assert.DoesNotContain(frames, static frame => frame.Stage == "source_denied");

            Assert.Equal([OffListCitation], run.Http.Requests);

            Assert.Empty(run.Breaches.Records);
        }
        finally
        {
            Directory.Delete(unregistered, recursive: true);
        }
    }

    [Fact]
    public async Task A_campaign_whose_sanctum_is_off_does_not_gate_its_citations()
    {
        Run run = Start(SanctumMode.Strict, enabled: false);

        run.Citations = [OffListCitation];

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: _root);

        Assert.DoesNotContain(frames, static frame => frame.Stage == "source_denied");

        Assert.Equal([OffListCitation], run.Http.Requests);

        Assert.Empty(run.Breaches.Records);
    }

    [Fact]
    public async Task An_explicit_campaign_identifier_still_gates_when_no_resolver_answered()
    {
        Run run = Start(SanctumMode.Strict);

        run.Citations = [OffListCitation];

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: string.Empty, campaignId: run.Campaign.Id);

        Assert.Contains(frames, static frame => frame.Stage == "source_denied");

        Assert.Empty(run.Http.Requests);
    }

    // A redirect hop the Sanctum refuses is the same refusal as a citation it refuses: the source is reported as denied and is not kept
    // in the synthesis as an empty body.
    [Fact]
    public async Task A_citation_whose_redirect_hop_a_strict_sanctum_refuses_is_reported_as_denied_and_not_kept()
    {
        Run run = Start(SanctumMode.Strict);

        run.Citations = [AllowedCitation];

        run.Http.RedirectsTo(AllowedCitation, OffListHop);

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: _root);

        WebResearchStreamFrame denied = Assert.Single(frames, static frame => frame.Stage == "source_denied");

        Assert.Contains("Sanctum", denied.Message, StringComparison.Ordinal);

        Assert.Equal([AllowedCitation], run.Http.Requests);

        Assert.DoesNotContain(AllowedCitation, run.Synthesis.Prompt, StringComparison.Ordinal);

        Assert.Empty(run.Result!.Citations);

        Assert.Single(run.Breaches.Records);
    }

    // A research citation is refused at an off-list redirect hop in either Sanctum mode: the hop is recorded as a breach and not followed.
    [Fact]
    public async Task A_citation_whose_redirect_hop_an_audit_only_sanctum_records_is_reported_as_denied_and_not_kept()
    {
        Run run = Start(SanctumMode.AuditOnly);

        run.Citations = [AllowedCitation];

        run.Http.RedirectsTo(AllowedCitation, OffListHop);

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: _root);

        WebResearchStreamFrame denied = Assert.Single(frames, static frame => frame.Stage == "source_denied");

        Assert.Contains("Sanctum", denied.Message, StringComparison.Ordinal);

        Assert.Equal([AllowedCitation], run.Http.Requests);

        Assert.DoesNotContain("controlled page", run.Synthesis.Prompt, StringComparison.Ordinal);

        Assert.Single(run.Breaches.Records);
    }

    [Fact]
    public async Task An_audit_only_sanctum_that_cannot_record_the_breach_refuses_the_citation()
    {
        Run run = Start(SanctumMode.AuditOnly);

        run.Citations = [OffListCitation];

        run.Breaches.RecordFailure = new IOException("simulated breach store failure");

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: _root);

        Assert.Contains(frames, static frame => frame.Stage == "source_denied");

        Assert.Empty(run.Http.Requests);
    }

    // The outbound address guard is independent of the Sanctum and of its mode. A citation that resolves to a private address is refused
    // before any request is made, in an AuditOnly Sanctum as in a Strict one and in a run with no Sanctum at all, and it is reported as
    // refused rather than kept as an empty source.
    [Theory]
    [InlineData(true, SanctumMode.AuditOnly)]
    [InlineData(true, SanctumMode.Strict)]
    [InlineData(false, SanctumMode.Strict)]
    public async Task A_citation_that_resolves_privately_is_refused_before_any_request_and_not_kept(bool inCampaign, SanctumMode mode)
    {
        Run run = Start(mode);

        run.Citations = [PrivateCitation];

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(
            workingDirectory: inCampaign ? _root : Path.GetTempPath());

        WebResearchStreamFrame denied = Assert.Single(frames, static frame => frame.Stage == "source_denied");

        Assert.Empty(run.Http.Requests);

        Assert.DoesNotContain(PrivateCitation, run.Synthesis.Prompt, StringComparison.Ordinal);

        if (inCampaign)
        {
            // The target is also outside the allowed domains, so the Sanctum has recorded it in either mode, and the message names
            // whichever control refused first.
            Assert.Single(run.Breaches.Records);
        }
        else
        {
            Assert.Contains("outbound network policy", denied.Message, StringComparison.Ordinal);

            Assert.Empty(run.Breaches.Records);
        }
    }

    [Theory]
    [InlineData(SanctumMode.AuditOnly)]
    [InlineData(SanctumMode.Strict)]
    public async Task A_citation_that_rebinds_to_loopback_at_connect_time_is_refused_and_nothing_is_dialed(SanctumMode mode)
    {
        using TcpListener loopback = new(IPAddress.Loopback, 0);

        loopback.Start();

        string citation = $"http://rebind.test:{((IPEndPoint)loopback.LocalEndpoint).Port}/page";

        Run run = Start(mode, allowedDomains: ["allowed.test", "rebind.test"]);

        run.Citations = [citation];

        run.Dns.Add("rebind.test", IPAddress.Parse("93.184.216.36"));

        FakeDnsResolver connect = new();

        connect.Add("rebind.test", IPAddress.Loopback);

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler(connect);

        run.UseHandler(handler);

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: _root);

        Assert.Contains(frames, static frame => frame.Stage == "source_denied");

        Assert.False(loopback.Pending());

        Assert.Empty(run.Breaches.Records);
    }

    // A Session is bound to one Campaign for good. In production no Campaign root has a registered identity, so a working directory the
    // Session's Campaign does not contain is a disagreement the canonical seam refuses; a refusal is not permission to fetch ungated, and the
    // Session's Strict Sanctum still gates the run whatever directory it stands in.
    [Theory]
    [InlineData("none")]
    [InlineData("outside")]
    [InlineData("inside-a-campaign-with-the-sanctum-off")]
    public async Task A_run_continuing_a_session_bound_to_a_strict_campaign_gates_every_citation_whatever_directory_it_stands_in(string where)
    {
        Run run = Start(SanctumMode.Strict);

        Campaign open = run.AddCampaign(OpenCampaign("elsewhere-open"));

        run.Citations = [OffListCitation];

        Guid session = Guid.NewGuid();

        run.BindSession(session, run.Campaign.Id);

        string workingDirectory = where switch
        {
            "none" => string.Empty,
            "outside" => Path.GetTempPath(),
            _ => Directory.CreateDirectory(Path.Combine(open.Path, "src")).FullName,
        };

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: workingDirectory, continueSessionId: session);

        Assert.Contains(frames, static frame => frame.Stage == "source_denied");

        Assert.Empty(run.Http.Requests);

        Assert.Single(run.Breaches.Records);

        Assert.DoesNotContain(OffListCitation, run.Synthesis.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_naming_a_strict_campaign_gates_every_citation_when_it_stands_in_a_campaign_with_the_sanctum_off()
    {
        Run run = Start(SanctumMode.Strict);

        Campaign open = run.AddCampaign(OpenCampaign("elsewhere-open"));

        run.Citations = [OffListCitation];

        run.UseProductionResolver();

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(
            workingDirectory: Directory.CreateDirectory(Path.Combine(open.Path, "src")).FullName,
            campaignId: run.Campaign.Id);

        Assert.Contains(frames, static frame => frame.Stage == "source_denied");

        Assert.Empty(run.Http.Requests);

        Assert.Single(run.Breaches.Records);
    }

    [Fact]
    public async Task A_run_standing_in_a_strict_campaign_is_gated_by_it_though_its_session_is_bound_to_a_campaign_with_the_sanctum_off()
    {
        Run run = Start(SanctumMode.Strict);

        Campaign open = run.AddCampaign(OpenCampaign("session-open"));

        run.Citations = [OffListCitation];

        Guid session = Guid.NewGuid();

        run.BindSession(session, open.Id);

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: _root, continueSessionId: session);

        Assert.Contains(frames, static frame => frame.Stage == "source_denied");

        Assert.Empty(run.Http.Requests);
    }

    [Fact]
    public async Task A_run_whose_session_and_directory_are_both_in_campaigns_with_the_sanctum_off_is_not_gated()
    {
        Run run = Start(SanctumMode.Strict, enabled: false);

        Campaign open = run.AddCampaign(OpenCampaign("session-open"));

        run.Citations = [OffListCitation];

        Guid session = Guid.NewGuid();

        run.BindSession(session, open.Id);

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: _root, continueSessionId: session);

        Assert.DoesNotContain(frames, static frame => frame.Stage == "source_denied");

        Assert.Equal([OffListCitation], run.Http.Requests);

        Assert.Empty(run.Breaches.Records);
    }

    // A Campaign whose Sanctum cannot be read is not one whose Sanctum is off.
    [Fact]
    public async Task A_run_bound_to_a_campaign_whose_sanctum_cannot_be_read_denies_every_citation()
    {
        Run run = Start(SanctumMode.Strict);

        run.Campaign.SanctumConfigJson = "{ not json";

        run.Citations = [AllowedCitation, OffListCitation];

        List<WebResearchStreamFrame> frames = await run.ResearchAsync(workingDirectory: _root);

        Assert.Equal(2, frames.Count(static frame => frame.Stage == "source_denied"));

        Assert.Empty(run.Http.Requests);

        Assert.Empty(run.Result!.Citations);
    }

    public void Dispose()
    {
        _ = TestDirectoryCleanup.TryDelete(_root, nameof(WebResearchCampaignBindingTests));

        foreach (string extra in _extraRoots)
        {
            _ = TestDirectoryCleanup.TryDelete(extra, nameof(WebResearchCampaignBindingTests));
        }
    }

    private Run Start(SanctumMode mode, bool enabled = true, string[]? allowedDomains = null)
    {
        _ = Directory.CreateDirectory(_root);

        Campaign campaign = new()
        {
            Id = Guid.NewGuid(),
            Name = "research-binding",
            NameLower = "research-binding",
            Path = _root,
            Type = WorkspaceType.Campaign,
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(new SanctumConfig
            {
                Enabled = enabled,
                Mode = mode,
                EnforcePathBoundary = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = allowedDomains ?? ["allowed.test"],
            }),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        return new Run(campaign);
    }

    private Campaign OpenCampaign(string name)
    {
        string root = Path.Combine(_root + "-" + name);

        _ = Directory.CreateDirectory(root);

        _extraRoots.Add(root);

        return new Campaign
        {
            Id = Guid.NewGuid(),
            Name = name,
            NameLower = name,
            Path = root,
            Type = WorkspaceType.Campaign,
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(new SanctumConfig { Enabled = false }),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static CanonicalCampaignContext CanonicalFor(Guid campaignId) =>
        CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest: null);

    /// <summary>One research run over scripted search results and a scripted HTTP origin, with the real guard and the real page reader.</summary>
    private sealed class Run
    {
        private readonly ContainingCampaigns _campaigns;

        private readonly SanctumGuard _guard;

        private HttpMessageHandler _handler;

        private readonly StubSessionBindings _sessionBindings = new();

        private readonly List<Guid> _availability;

        private ICanonicalCampaignContextResolver? _production;

        public Run(Campaign campaign)
        {
            Campaign = campaign;

            _campaigns = new ContainingCampaigns(campaign);

            _availability = new List<Guid> { campaign.Id };

            Breaches = new InMemorySanctumBreachRepository();

            Dns = new FakeDnsResolver();

            Dns.Add("allowed.test", IPAddress.Parse("93.184.216.34"));

            Dns.Add("off-list.test", IPAddress.Parse("93.184.216.35"));

            Dns.Add("private.test", IPAddress.Parse("10.1.2.3"));

            _guard = new SanctumGuard(_campaigns, Breaches, Microsoft.Extensions.Logging.Abstractions.NullLogger<SanctumGuard>.Instance, UnregisteredCampaignRoots.Instance, Dns);

            Http = new ScriptedHttp();

            _handler = Http;
        }

        public Campaign Campaign { get; }

        public InMemorySanctumBreachRepository Breaches { get; }

        public FakeDnsResolver Dns { get; }

        public ScriptedHttp Http { get; }

        public string[] Citations { get; set; } = [];

        public FixedResolver? Resolver { get; set; }

        public CapturingIntelligence Synthesis { get; } = new();

        public WebWorkflowResultCapture? Captured { get; private set; }

        public WebResearchWorkflowResult? Result => Captured?.Result;

        public void UseHandler(HttpMessageHandler handler) => _handler = handler;

        public Campaign AddCampaign(Campaign other)
        {
            _campaigns.Add(other);

            _availability.Add(other.Id);

            return other;
        }

        public void BindSession(Guid sessionId, Guid campaignId)
        {
            _ = _sessionBindings.Bind(sessionId, campaignId);

            UseProductionResolver();
        }

        /// <summary>The production canonical resolver over fake readers: no Campaign root has a registered identity, as in production.</summary>
        public void UseProductionResolver() =>
            _production = ProductionCampaignResolver.Over(_sessionBindings, new AvailableCampaigns([.. _availability]));

        public async Task<List<WebResearchStreamFrame>> ResearchAsync(string workingDirectory, Guid? campaignId = null, Guid? continueSessionId = null)
        {
            using SingleClientFactory clients = new(_handler);

            ArcanumSettings settings = new()
            {
                DefaultModel = "research-model",
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "test",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://example.test/v1",
                        Models = [new ModelEntry("research-model")],
                    },
                ],
                Features = new FeatureSettings { WebBrowsing = true },
            };

            LocalHttpWebProvider reader = new(clients, new WebPageContentExtractor(), Dns);

            WebResearchWorkflowService service = new(
                new Catalog(new SearchStub(Citations), reader),
                new TestOptionsSnapshot<ArcanumSettings>(settings),
                Synthesis,
                sessions: new ExistingSessions(),
                attachments: null!,
                _campaigns,
                _guard,
                _production ?? (ICanonicalCampaignContextResolver?)Resolver ?? RefusingCampaignResolver.Instance);

            List<WebResearchStreamFrame> frames = [];

            await foreach (WebResearchStreamFrame frame in service.ResearchAsync(
                new WebResearchWorkflowRequest
                {
                    Question = "What changed?",
                    SourceTarget = Math.Max(1, Citations.Length),
                    TokenBudget = 1_200,
                    WorkingDirectory = workingDirectory,
                    CampaignId = campaignId,
                    ContinueSessionId = continueSessionId,
                },
                CancellationToken.None))
            {
                frames.Add(frame);

                if (frame.Type == WebResearchStreamFrameType.Result)
                {
                    Captured = new WebWorkflowResultCapture(frame.Result);
                }
            }

            Assert.Contains(frames, static frame => frame.Type == WebResearchStreamFrameType.Result);

            return frames;
        }
    }

    private sealed record WebWorkflowResultCapture(WebResearchWorkflowResult? Result);

    private sealed class FixedResolver(Result<CanonicalCampaignContext> answer) : ICanonicalCampaignContextResolver
    {
        public int Calls { get; private set; }

        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
            CanonicalCampaignResolutionRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;

            return ValueTask.FromResult(answer);
        }
    }

    private sealed class Catalog(IWebResearchProvider search, IWebResearchProvider reader) : IWebResearchProviderCatalog
    {
        public bool TryGetProvider(string providerName, [NotNullWhen(true)] out IWebResearchProvider? provider)
        {
            provider = string.Equals(providerName, WebResearchProviderNames.LocalHttp, StringComparison.OrdinalIgnoreCase)
                ? reader
                : search;

            return true;
        }
    }

    private sealed class SearchStub(string[] urls) : IWebResearchProvider
    {
        public string ProviderName => WebResearchProviderNames.Perplexity;

        public WebResearchCapabilities Capabilities => WebResearchCapabilities.Search;

        public Task<Result<WebSearchResult>> SearchAsync(string query, WebSearchOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<WebSearchResult>.Success(
                    new WebSearchResult(
                        "Search summary.",
                        [.. urls.Select(static (url, index) => new WebCitation(index + 1, url, "Source " + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))],
                        new WebResearchUsage(TotalTokens: 10, SearchQueries: 1, CostUsd: 0.01m))));

        public Task<Result<WebReadResult>> ReadUrlAsync(string url, WebReadOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Search results are read by the local reader.");
    }

    private sealed class CapturingIntelligence : IArcanumIntelligenceProvider
    {
        public string Prompt { get; private set; } = string.Empty;

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            Prompt = request.Prompt;

            return Task.FromResult(
                Result<PromptTurnResult>.Success(new PromptTurnResult("Synthesized answer [1].", new ChatCompletionUsage(20, 10, 30))));
        }

        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException();
    }

    /// <summary>The origin: records every URL requested and answers a scripted redirect or a small text page.</summary>
    private sealed class ScriptedHttp : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _redirects = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public void RedirectsTo(string from, string to) => _redirects[from] = to;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;

            Requests.Add(url);

            if (_redirects.TryGetValue(url, out string? target))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri(target) } });
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("controlled page", Encoding.UTF8, "text/plain"),
                });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false);

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }

    /// <summary>The Campaign table: its Campaigns, found by identifier and by the deepest registered root that contains a directory.</summary>
    private sealed class ContainingCampaigns(Campaign first) : ICampaignRepository
    {
        private readonly List<Campaign> _campaigns = [first];

        public void Add(Campaign campaign) => _campaigns.Add(campaign);

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_campaigns.FirstOrDefault(campaign => campaign.Id == id));

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(_campaigns.FirstOrDefault(campaign => string.Equals(Path.GetFullPath(path), Path.GetFullPath(campaign.Path), StringComparison.Ordinal)));

        public Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default)
        {
            string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            return Task.FromResult(
                _campaigns
                    .Where(campaign =>
                    {
                        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(campaign.Path));

                        return candidate == root || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                    })
                    .OrderByDescending(static campaign => campaign.Path.Length)
                    .FirstOrDefault());
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

        public Task<Campaign> UpdateAsync(Campaign updated, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_campaigns.Count);
    }

    /// <summary>A Session store in which every Session exists; research only asks whether the Session it continues is there.</summary>
    private sealed class ExistingSessions : ISessionRepository
    {
        public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<Session?>(new Session { Id = id });

        public Task<Session> CreateAsync(Guid? campaignId, string? title, CancellationToken ct) => throw new NotSupportedException();

        public Task<SessionQueryResult> QueryAsync(SessionQueryRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<SessionAnalytics> GetAnalyticsAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<SessionExportResult>> ExportAsync(Guid id, SessionExportFormat format, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<Entry>> AddEntryAsync(Guid sessionId, Entry entry, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<Session>> ForkAsync(Guid sourceId, ForkSessionRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<Entry>> GetEntriesAscendingAsync(Guid sessionId, int takeLast, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<List<Entry>> GetEntriesAfterAsync(Guid sessionId, long afterSequence, int limit, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Entry?> GetEntryAsync(Guid sessionId, Guid entryId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<List<Entry>> GetEntriesAsync(
            Guid sessionId,
            int offset = 0,
            int limit = 100,
            DateTimeOffset? beforeCreatedAt = null,
            Guid? beforeId = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task<Session?> PatchSessionAsync(Guid id, SessionHeaderPatch patch, CancellationToken ct) => throw new NotSupportedException();

        public Task ArchiveAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
}
