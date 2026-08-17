using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Issue #114 — the export half of #90's plaintext-transfer criterion.
/// </summary>
/// <remarks>
/// A backup is an encrypted archive an operator chose; a plaintext export is a file anybody can read,
/// and once it exists no local erasure unmakes it. These assertions are therefore about ordering as
/// much as outcome: the refusal has to land before the export graph is read, and the lease has to
/// outlive the last byte rather than the handler (§10.19.11).
/// </remarks>
public sealed class CovenantPlaintextExportTests
{

    private static readonly Guid SessionId = Guid.Parse("4E5F6071-8293-4A4B-8C5D-6E7F80918293");

    private static readonly Guid CampaignId = Guid.Parse("5F607182-93A4-4B5C-8D6E-7F8091A2B3C4");

    /// <summary>
    /// Atomic means the export graph is never read at all. A refusal that ran after serialization
    /// would already have pulled Covenant-derived content into process memory to describe it.
    /// </summary>
    [Fact]
    public async Task Plaintext_session_export_rejects_any_tainted_artifact_atomically()
    {

        await using ExportHost host = await ExportHost.CreateAsync(policy =>
            policy.SessionSensitivity = new CovenantSessionExportSensitivity(
                SessionId,
                TaintedArtifactCount: 3,
                ContentSensitivity.CovenantDerived));

        HttpResponseMessage response = await host.Client.GetAsync(
            $"/api/sessions/{SessionId:D}/export?format=json");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        Assert.False(host.Sessions.ExportCalled);

        ApiResponse<SessionExportResult>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSessionExportResult);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Covenant.PlaintextExportRefused, body.Error!.Value.Code);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());

    }

    /// <summary>
    /// A clean Session still exports. The refusal is about evidence, not about the feature being on.
    /// </summary>
    [Fact]
    public async Task A_clean_session_still_exports_under_the_conditional_arm()
    {

        await using ExportHost host = await ExportHost.CreateAsync();

        HttpResponseMessage response = await host.Client.GetAsync(
            $"/api/sessions/{SessionId:D}/export?format=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(host.Sessions.ExportCalled);

    }

    /// <summary>
    /// The <c>format</c> vocabulary is the one the contract publishes and the CLI sends.
    /// </summary>
    /// <remarks>
    /// Found while wiring the refusal above: the route was typed as the CLR enum, minimal-API enum
    /// binding is case-sensitive, and so the only accepted values were <c>Json</c> and <c>Markdown</c>
    /// — while §8 of the API reference, the string-only wire enum, and <c>arcanum session export</c>
    /// all say <c>json</c> and <c>markdown</c>. Every export the shipped CLI attempted was refused
    /// with an untyped framework 400 before the handler ran.
    /// </remarks>
    [Theory]
    [InlineData("json")]
    [InlineData("markdown")]
    [InlineData("Json")]
    [InlineData("MARKDOWN")]
    public async Task Session_export_accepts_the_documented_format_vocabulary(string format)
    {

        await using ExportHost host = await ExportHost.CreateAsync();

        HttpResponseMessage response = await host.Client.GetAsync(
            $"/api/sessions/{SessionId:D}/export?format={format}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    }

    /// <summary>
    /// An unknown or missing format is still refused, but with a typed code a client can switch on
    /// rather than the framework's untyped envelope.
    /// </summary>
    [Theory]
    [InlineData("?format=yaml")]
    [InlineData("")]
    public async Task Session_export_refuses_an_unknown_format_with_a_typed_code(string query)
    {

        await using ExportHost host = await ExportHost.CreateAsync();

        HttpResponseMessage response = await host.Client.GetAsync(
            $"/api/sessions/{SessionId:D}/export{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.False(host.Sessions.ExportCalled);

        ApiResponse<SessionExportResult>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSessionExportResult);

        Assert.NotNull(body);

        Assert.Equal(ErrorCodes.Session.InvalidFormat, body.Error!.Value.Code);

    }

    /// <summary>
    /// The Campaign export payload is spells, prompts, and the Campaign's own settings. Nothing in it
    /// may name Covenant memory, a version, a receipt, a hash, provenance, or a tainted artifact.
    /// </summary>
    [Fact]
    public async Task Campaign_export_contains_no_covenant_or_tainted_artifact_fields()
    {

        await using ExportHost host = await ExportHost.CreateAsync(policy =>
            policy.CampaignExclusions = new CovenantCampaignExportExclusions(
                CovenantEntryCount: 4,
                TaintedArtifactCount: 2));

        HttpResponseMessage response = await host.Client.PostAsync(
            $"/api/campaigns/{CampaignId:D}/export",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string payload = await response.Content.ReadAsStringAsync();

        foreach (string forbidden in new[]
                 {
                     "covenantEntries",
                     "covenantVersion",
                     "receipt",
                     "provenance",
                     "sensitivity",
                     "labelDigest",
                     "contentDigest",
                     "authoredKey",
                     "normalizedKey",
                 })
        {

            Assert.DoesNotContain(forbidden, payload, StringComparison.OrdinalIgnoreCase);

        }

    }

    /// <summary>
    /// Silence is the failure mode this closes. A Campaign whose Covenant memory was simply left out
    /// looked, on the wire, exactly like a Campaign that never had any.
    /// </summary>
    [Fact]
    public async Task Campaign_export_reports_typed_covenant_and_tainted_exclusion_counts()
    {

        await using ExportHost host = await ExportHost.CreateAsync(policy =>
            policy.CampaignExclusions = new CovenantCampaignExportExclusions(
                CovenantEntryCount: 4,
                TaintedArtifactCount: 2));

        HttpResponseMessage response = await host.Client.PostAsync(
            $"/api/campaigns/{CampaignId:D}/export",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<CampaignExportDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignExportDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data!.Exclusions);

        Assert.Equal(4, body.Data.Exclusions!.CovenantEntryCount);

        Assert.Equal(2, body.Data.Exclusions.TaintedArtifactCount);

    }

    /// <summary>
    /// The lease covers the whole response, not the handler. A reset that drained while the last
    /// kilobyte was still on the socket would otherwise report completion over content it had
    /// promised was gone.
    /// </summary>
    [Fact]
    public async Task Session_and_campaign_export_hold_conditional_read_lease_through_archive_serialization()
    {

        await using ExportHost sessionHost = await ExportHost.CreateAsync();

        _ = await sessionHost.Client.GetAsync($"/api/sessions/{SessionId:D}/export?format=json");

        AssertLeaseOutlivedTheBody(sessionHost);

        await using ExportHost campaignHost = await ExportHost.CreateAsync();

        _ = await campaignHost.Client.PostAsync($"/api/campaigns/{CampaignId:D}/export", content: null);

        AssertLeaseOutlivedTheBody(campaignHost);

    }

    /// <summary>
    /// The inventory test proves each content-bearing route made a decision, not that every route
    /// made the same one. Both exports are conditional: whether they are protected is a fact about
    /// the data, not about the URL.
    /// </summary>
    [Fact]
    public async Task Both_export_routes_declare_the_conditional_covenant_read_policy()
    {

        await using ExportHost host = await ExportHost.CreateAsync();

        foreach (string endpointName in new[] { "ExportSession", "ExportCampaign" })
        {

            RouteEndpoint endpoint = host.Endpoint(endpointName);

            Assert.NotNull(endpoint.Metadata.GetMetadata<CovenantConditionalReadRequirementMetadata>());

        }

    }

    /// <summary>
    /// With <c>Arcanum:Features:Covenant</c> off there is no arm, no lease, and no exclusion report,
    /// and both routes are byte-for-byte what they were before this slice.
    /// </summary>
    [Fact]
    public async Task With_the_covenant_arm_absent_both_exports_behave_as_they_did_before()
    {

        await using ExportHost host = await ExportHost.CreateAsync(policy => policy.ArmPresent = false);

        HttpResponseMessage session = await host.Client.GetAsync(
            $"/api/sessions/{SessionId:D}/export?format=json");

        Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        Assert.True(host.Sessions.ExportCalled);

        Assert.Null(session.Headers.CacheControl);

        HttpResponseMessage campaign = await host.Client.PostAsync(
            $"/api/campaigns/{CampaignId:D}/export",
            content: null);

        Assert.Equal(HttpStatusCode.OK, campaign.StatusCode);

        Assert.DoesNotContain(
            "exclusions",
            await campaign.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// An arm that is on but cannot take its lease refuses before content on both routes. An export
    /// that proceeded because the ledger was unreadable would be exactly the disclosure the lease
    /// exists to prevent.
    /// </summary>
    [Fact]
    public async Task An_unavailable_conditional_arm_refuses_both_exports_before_content()
    {

        await using ExportHost host = await ExportHost.CreateAsync(policy =>
            policy.AdmissionError = new Error(
                ErrorCodes.Covenant.Unavailable,
                "The Covenant canonical tier is not healthy."));

        HttpResponseMessage session = await host.Client.GetAsync(
            $"/api/sessions/{SessionId:D}/export?format=json");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, session.StatusCode);

        Assert.False(host.Sessions.ExportCalled);

        HttpResponseMessage campaign = await host.Client.PostAsync(
            $"/api/campaigns/{CampaignId:D}/export",
            content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, campaign.StatusCode);

        Assert.False(host.Campaigns.LookupCalled);

    }

    private static void AssertLeaseOutlivedTheBody(ExportHost host)
    {

        Assert.NotNull(host.Policy.Lease);

        int lastWrite = host.Events.LastIndexOf(ExportHost.ResponseWrite);

        int disposed = host.Events.IndexOf(ExportHost.LeaseDisposed);

        Assert.True(lastWrite >= 0, "The response wrote no bytes at all.");

        Assert.True(disposed >= 0, "The conditional read lease was never released.");

        Assert.True(
            lastWrite < disposed,
            $"The lease was released before the last response byte: {string.Join(" -> ", host.Events)}");

    }

    /// <summary>
    /// A slim host that maps the real Session and Campaign route groups over stub ports, so the
    /// assertions are about the shipped route rather than a copy of it.
    /// </summary>
    private sealed class ExportHost : IAsyncDisposable
    {

        internal const string ResponseWrite = "response-write";

        internal const string LeaseDisposed = "lease-disposed";

        private WebApplication _app = null!;

        internal List<string> Events { get; } = [];

        internal HttpClient Client { get; private set; } = null!;

        internal StubExportPolicy Policy { get; private set; } = null!;

        internal StubSessionRepository Sessions { get; } = new();

        internal StubCampaignRepository Campaigns { get; } = new();

        internal static async Task<ExportHost> CreateAsync(Action<StubExportPolicy>? configure = null)
        {

            ExportHost host = new();

            StubExportPolicy policy = new(host.Events);

            configure?.Invoke(policy);

            host.Policy = policy;

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

            builder.WebHost.UseTestServer();

            builder.Services.AddSingleton<ISessionRepository>(host.Sessions);

            builder.Services.AddSingleton<ICampaignRepository>(host.Campaigns);

            builder.Services.AddSingleton<IPromptRepository>(new StubPromptRepository());

            builder.Services.AddSingleton<ISpellRepository>(new StubSpellRepository());

            builder.Services.AddSingleton<ICovenantExportPolicy>(policy);

            builder.Services.ConfigureHttpJsonOptions(static options =>
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

            WebApplication app = builder.Build();

            app.Use(async (HttpContext context, Func<Task> next) =>
            {

                Stream original = context.Response.Body;

                context.Response.Body = new RecordingStream(original, host.Events);

                try
                {

                    await next().ConfigureAwait(false);

                }
                finally
                {

                    context.Response.Body = original;

                }

            });

            RouteGroupBuilder api = app.MapGroup("/api");

            _ = api.MapSessionEndpoints();

            _ = api.MapCampaignEndpoints();

            await app.StartAsync();

            host._app = app;

            host.Client = app.GetTestClient();

            return host;

        }

        internal RouteEndpoint Endpoint(string name) =>
            _app.Services
                .GetRequiredService<EndpointDataSource>()
                .Endpoints
                .OfType<RouteEndpoint>()
                .Single(endpoint => string.Equals(
                    endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                    name,
                    StringComparison.Ordinal));

        public async ValueTask DisposeAsync()
        {

            Client?.Dispose();

            await _app.DisposeAsync();

        }

    }

    /// <summary>Records every write so a test can prove the lease outlived the last one.</summary>
    private sealed class RecordingStream(Stream inner, List<string> events) : Stream
    {

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();

            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {

            events.Add(ExportHost.ResponseWrite);

            inner.Write(buffer, offset, count);

        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {

            events.Add(ExportHost.ResponseWrite);

            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {

            events.Add(ExportHost.ResponseWrite);

            return inner.WriteAsync(buffer, offset, count, cancellationToken);

        }

    }

    private sealed class StubExportPolicy(List<string> events) : ICovenantExportPolicy
    {

        internal bool ArmPresent { get; set; } = true;

        internal Error? AdmissionError { get; set; }

        internal CovenantSessionExportSensitivity? SessionSensitivity { get; set; }

        internal CovenantCampaignExportExclusions? CampaignExclusions { get; set; }

        internal RecordingLease? Lease { get; private set; }

        public ValueTask<Result<CovenantExportAdmission>> AcquireConditionalReadAsync(
            CovenantOperationScope? scope,
            CancellationToken cancellationToken)
        {

            if (AdmissionError is { } error)
            {

                return ValueTask.FromResult(Result<CovenantExportAdmission>.Failure(error));

            }

            if (!ArmPresent)
            {

                return ValueTask.FromResult(
                    Result<CovenantExportAdmission>.Success(CovenantExportAdmission.Absent));

            }

            Lease = new RecordingLease(events, scope);

            return ValueTask.FromResult(
                Result<CovenantExportAdmission>.Success(new CovenantExportAdmission(Lease)));

        }

        public Task<Result<CovenantSessionExportSensitivity>> InspectSessionAsync(
            Guid sessionId,
            ICovenantSnapshotReadLease readLease,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<CovenantSessionExportSensitivity>.Success(
                SessionSensitivity ?? CovenantSessionExportSensitivity.Clean(sessionId)));

        public Task<Result<CovenantCampaignExportExclusions>> InventoryCampaignExclusionsAsync(
            Guid campaignId,
            ICovenantSnapshotReadLease readLease,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<CovenantCampaignExportExclusions>.Success(
                CampaignExclusions ?? new CovenantCampaignExportExclusions(0, 0)));

    }

    private sealed class RecordingLease(List<string> events, CovenantOperationScope? scope)
        : ICovenantSnapshotReadLease
    {

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            RegistrationId: Guid.Parse("60718293-A4B5-4C6D-8E7F-8091A2B3C4D5"),
            Kind: scope is null ? CovenantLeaseKind.InstallationRead : CovenantLeaseKind.Read,
            Coverage: scope is null ? CovenantLeaseCoverage.Installation : CovenantLeaseCoverage.Scoped,
            Scope: scope,
            DatasetGeneration: Guid.Parse("718293A4-B5C6-4D7E-8F90-91A2B3C4D5E6"),
            CapabilityGeneration: 1,
            AuthorityEpoch: 1,
            CanonicalSequence: 0,
            CampaignAvailabilityGeneration: null,
            CampaignPathRevision: null,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            RecoveryOwner: null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        internal bool Disposed { get; private set; }

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Disposed
                ? Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "Released."))
                : Result.Success());

        public ValueTask DisposeAsync()
        {

            if (!Disposed)
            {

                Disposed = true;

                events.Add(ExportHost.LeaseDisposed);

            }

            return ValueTask.CompletedTask;

        }

    }

    private sealed class StubSessionRepository : ISessionRepository
    {

        internal bool ExportCalled { get; private set; }

        public Task<Result<SessionExportResult>> ExportAsync(
            Guid id,
            SessionExportFormat format,
            CancellationToken ct)
        {

            ExportCalled = true;

            return Task.FromResult(Result<SessionExportResult>.Success(
                new SessionExportResult(id, "json", "{\"session\":{},\"entries\":[]}", "application/json")));

        }

        public Task<Session> CreateAsync(Guid? campaignId, string? title, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SessionQueryResult> QueryAsync(SessionQueryRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SessionAnalytics> GetAnalyticsAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<Entry>> AddEntryAsync(Guid sessionId, Entry entry, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<Session>> ForkAsync(Guid sourceId, ForkSessionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<List<Entry>> GetEntriesAscendingAsync(Guid sessionId, int takeLast, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<Entry>> GetEntriesAfterAsync(
            Guid sessionId,
            long afterSequence,
            int limit,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Entry?> GetEntryAsync(Guid sessionId, Guid entryId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<Entry>> GetEntriesAsync(
            Guid sessionId,
            int offset = 0,
            int limit = 100,
            DateTimeOffset? beforeCreatedAt = null,
            Guid? beforeId = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpdateSessionAsync(Session session, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ArchiveAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();

    }

    private sealed class StubCampaignRepository : ICampaignRepository
    {

        internal bool LookupCalled { get; private set; }

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {

            LookupCalled = true;

            return Task.FromResult<Campaign?>(new Campaign
            {
                Id = id,

                Name = "Arcanum",

                NameLower = "arcanum",

                Path = "/tmp/arcanum",

                Type = WorkspaceType.Campaign,

                Settings = "{}",

                CreatedAt = DateTimeOffset.UnixEpoch,

                UpdatedAt = DateTimeOffset.UnixEpoch,
            });

        }

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class StubPromptRepository : IPromptRepository
    {

        public Task<ListPageResult<Prompt>> ListAsync(
            Guid? campaignId,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Prompt>([], false));

        public Task<Prompt?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Prompt?> GetByNameAndVersionAsync(
            string name,
            string version,
            Guid? campaignId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<Prompt>> ListVersionsAsync(
            string name,
            Guid? campaignId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Prompt> AddAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Prompt> UpdateAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class StubSpellRepository : ISpellRepository
    {

        public Task<SpellSummary[]> ListAsync(string? workingDirectory, CancellationToken ct) =>
            Task.FromResult(Array.Empty<SpellSummary>());

        public Task<SpellDetail?> GetAsync(string name, string? workingDirectory, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result> CreateAsync(string? workingDirectory, CreateSpellRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result> UpdateAsync(
            string name,
            string? workingDirectory,
            UpdateSpellRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<Result> DeleteAsync(string name, string? workingDirectory, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SpellSummary[]> SearchAsync(SpellSearchQuery query, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SpellValidationResultDto> ValidateAsync(
            string name,
            string? workingDirectory,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<SpellExportDto?> ExportAsync(string name, string? workingDirectory, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<SpellSummary>> ImportAsync(SpellImportRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<SpellSummary>> CloneAsync(
            string name,
            string? workingDirectory,
            CloneSpellRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<SpellVersionDto>> CreateVersionAsync(
            string name,
            string? workingDirectory,
            CreateSpellVersionRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<SpellVersionDto>> UpdateVersionAsync(
            string name,
            string version,
            string? workingDirectory,
            UpdateSpellVersionRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<SpellVersionDto>> ActivateVersionAsync(
            string name,
            string version,
            string? workingDirectory,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<SpellVersionDetailDto>> GetVersionDetailAsync(
            string name,
            string version,
            string? workingDirectory,
            CancellationToken ct) => throw new NotSupportedException();

    }

}
