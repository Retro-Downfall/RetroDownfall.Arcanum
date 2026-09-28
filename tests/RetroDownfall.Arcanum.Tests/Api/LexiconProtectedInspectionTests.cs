using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Lexicon;
using RetroDownfall.Arcanum.Tests.Support;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LexiconProtectedInspectionTests
{
    public static TheoryData<string> Routes => new()
    {
        "GetLexiconEntry", "GetLexiconEntry:campaign", "ListLexiconEntries", "SearchMemory", "SearchMemory:all",
        "GetMemorySources", "GetSessionMemorySources", "ExplainMemory", "ExplainSessionMemory",
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Protected_routes_admit_the_complete_scope_before_read_and_retain_it_through_final_byte(string route)
    {
        await using InspectionHost host = await InspectionHost.CreateAsync();

        using BlockingBody body = new();

        DefaultHttpContext context = host.Context(route, body);

        context.Response.Headers.ETag = "old-validator";

        context.Response.Headers.LastModified = "Sun, 27 Sep 2026 00:00:00 GMT";

        Task executing = host.ExecuteAsync(route, context);

        try
        {
            await body.WaitForWriteAsync(executing);

            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

            Assert.Equal(1, host.Policy.Acquisitions);

            Assert.Equal(route == "GetLexiconEntry" ? CovenantOperationScope.Global : null, host.Policy.Scope);

            Assert.True(host.Policy.Lease!.Revalidations >= 3);

            Assert.Equal(0, host.Policy.Lease.Disposals);

            Assert.Equal(0, body.Length);

            Assert.True(host.ReadsAdmitted);

            Assert.Equal(0, host.LegacyLexiconCounts);

            AssertProtectedHeaders(context);
        }
        finally
        {
            body.Release.TrySetResult();

            await executing.WaitAsync(TimeSpan.FromSeconds(20));
        }

        Assert.Equal(1, host.Policy.Lease!.Disposals);

        Assert.True(body.Length > 0);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Admission_failure_prevents_every_Lexicon_read_including_status_counts(string route)
    {
        await using InspectionHost host = await InspectionHost.CreateAsync();

        host.Policy.Deny = true;

        using MemoryStream body = new();

        DefaultHttpContext context = host.Context(route, body);

        await host.ExecuteAsync(route, context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);

        Assert.Equal(0, host.LexiconReads);

        Assert.Contains(ErrorCodes.Covenant.Unavailable, Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Unlabeled_routes_release_before_ordinary_serialization_and_do_not_inherit_protected_headers(string route)
    {
        await using InspectionHost host = await InspectionHost.CreateAsync(protectedEntry: false);

        using BlockingBody body = new();

        DefaultHttpContext context = host.Context(route, body);

        Task executing = host.ExecuteAsync(route, context);

        try
        {
            await body.WaitForWriteAsync(executing);

            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

            Assert.NotNull(host.Policy.Lease);

            Assert.Equal(1, host.Policy.Lease.Disposals);

            Assert.Empty(context.Response.Headers.CacheControl.ToString());

            Assert.Empty(context.Response.Headers.Pragma.ToString());

            Assert.Empty(context.Response.Headers.Expires.ToString());
        }
        finally
        {
            body.Release.TrySetResult();

            await executing.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Feature_absent_routes_preserve_ordinary_success(string route)
    {
        await using InspectionHost host = await InspectionHost.CreateAsync(protectedEntry: false);

        host.Policy.Absent = true;

        using MemoryStream body = new();

        DefaultHttpContext context = host.Context(route, body);

        await host.ExecuteAsync(route, context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        Assert.Null(host.Policy.Lease);

        Assert.Empty(context.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Final_revalidation_refuses_revoked_content_before_serialization(string route)
    {
        await using InspectionHost host = await InspectionHost.CreateAsync();

        host.Policy.StaleAfter = 2;

        using MemoryStream body = new();

        DefaultHttpContext context = host.Context(route, body);

        await host.ExecuteAsync(route, context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);

        string json = Encoding.UTF8.GetString(body.ToArray());

        Assert.Contains(ErrorCodes.Covenant.StaleSnapshot, json, StringComparison.Ordinal);

        Assert.DoesNotContain("alpha", json, StringComparison.Ordinal);

        Assert.DoesNotContain("\"stores\"", json, StringComparison.Ordinal);

        Assert.NotNull(host.Policy.Lease);

        Assert.Equal(1, host.Policy.Lease.Disposals);

        AssertProtectedHeaders(context);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Serialization_failure_releases_the_route_lease_once(string route)
    {
        await using InspectionHost host = await InspectionHost.CreateAsync();

        using BlockingBody body = new() { Fail = true };

        body.Release.TrySetResult();

        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(route, host.Context(route, body)));

        Assert.NotNull(host.Policy.Lease);

        Assert.Equal(1, host.Policy.Lease.Disposals);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Cancellation_during_serialization_releases_the_route_lease_once(string route)
    {
        await using InspectionHost host = await InspectionHost.CreateAsync();

        using BlockingBody body = new();

        using CancellationTokenSource cancellation = new();

        DefaultHttpContext context = host.Context(route, body);

        context.RequestAborted = cancellation.Token;

        Task executing = host.ExecuteAsync(route, context);

        await body.WaitForWriteAsync(executing);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing);

        Assert.NotNull(host.Policy.Lease);

        Assert.Equal(1, host.Policy.Lease.Disposals);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Corrupt_content_is_a_mapped_error_and_releases_before_ordinary_serialization(string route)
    {
        await using InspectionHost host = await InspectionHost.CreateAsync();

        await host.Owner.ExecuteAsync("UPDATE lexicon_entries SET FactsText = 'corrupt'");

        using BlockingBody body = new();

        DefaultHttpContext context = host.Context(route, body);

        Task executing = host.ExecuteAsync(route, context);

        try
        {
            await body.WaitForWriteAsync(executing);

            Assert.NotEqual(StatusCodes.Status200OK, context.Response.StatusCode);

            Assert.NotNull(host.Policy.Lease);

            Assert.Equal(1, host.Policy.Lease.Disposals);

            Assert.Empty(context.Response.Headers.CacheControl.ToString());
        }
        finally
        {
            body.Release.TrySetResult();

            await executing.WaitAsync(TimeSpan.FromSeconds(20));
        }

        Assert.Contains(ErrorCodes.Lexicon.CurationIntegrityFailed, Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_existing_content_route_declares_conditional_read_authority()
    {
        await using InspectionHost host = await InspectionHost.CreateAsync();

        string[] expected = ["ApplyLexiconMemoryReview", "ExplainMemory", "ExplainSessionMemory", "GetLexiconEntry", "GetMemorySources",
            "GetSessionMemorySources", "ListLexiconEntries", "ListLexiconMemoryReviewQueue", "PrepareLexiconMemoryReview", "SearchMemory"];

        string[] actual = host.Endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/memory", StringComparison.Ordinal)
            && endpoint.Metadata.GetMetadata<CovenantConditionalReadRequirementMetadata>() is not null)
            .Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    private static void AssertProtectedHeaders(HttpContext context)
    {
        Assert.Equal("no-store, private", context.Response.Headers.CacheControl.ToString());

        Assert.Equal("no-cache", context.Response.Headers.Pragma.ToString());

        Assert.Equal("0", context.Response.Headers.Expires.ToString());

        Assert.Empty(context.Response.Headers.ETag.ToString());

        Assert.Empty(context.Response.Headers.LastModified.ToString());
    }

    private sealed class InspectionHost : IAsyncDisposable
    {
        private readonly GrimoireFixture _grimoire = new();

        private WebApplication _app = null!;

        internal CorrectionFixture Owner { get; private set; } = null!;

        internal Policy Policy { get; } = new();

        internal Guid Session { get; private set; }

        internal int LexiconReads { get; private set; }

        internal int LegacyLexiconCounts { get; private set; }

        internal bool ReadsAdmitted { get; private set; } = true;

        internal IEnumerable<RouteEndpoint> Endpoints => _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        internal static async Task<InspectionHost> CreateAsync(bool protectedEntry = true)
        {
            InspectionHost host = new();

            host.Owner = new(host._grimoire);

            LexiconEntryDetail detail = await host.Owner.SeedAsync();

            host.Session = detail.Entry.FactProvenance![0].Source.SessionId;

            if (protectedEntry)
            {
                await host.Owner.ProtectAsync();
            }
            else
            {
                await host.Owner.ExecuteAsync($"INSERT INTO Sessions(Id, Title, CreatedAt, UpdatedAt) VALUES ('{host.Session.ToString("D").ToUpperInvariant()}', 'ordinary', '2026-09-01T00:00:00.0000000Z', '2026-09-01T00:00:00.0000000Z')");
            }

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

            builder.WebHost.UseTestServer();

            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

            IServiceCollection services = builder.Services;

            services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(host.Owner.Settings));

            services.AddScoped<ISagaMemoryStore, SagaMemoryStore>();

            services.AddSingleton<WeaveIndexAvailability>();

            services.AddSingleton<ICovenantSensitiveArtifactPurger>(_ => throw new NotSupportedException());

            services.AddSingleton<ILexiconService>(host.Owner.Concrete);

            services.AddSingleton<ILexiconCurationService>(host.Owner.Concrete);

            services.AddSingleton<ICovenantExportPolicy>(host.Policy);

            services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(new FixtureOrdinaryConnectionFactory(host.Owner.Connection.ConnectionString));

            services.AddScoped(_ =>
            {
                ArcanumDbContext db = host._grimoire.CreateContext(host.Owner.Path);

                db.Database.OpenConnection();

                raw.sqlite3_trace(((Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection()).Handle,
                    (object _, string sql) => host.Observe(sql), null);

                return db;
            });

            host._app = builder.Build();

            _ = host._app.MapGroup("/api").MapMemoryEndpoints();

            await host._app.StartAsync();

            raw.sqlite3_trace(host.Owner.Connection.Handle, (object _, string sql) => host.Observe(sql), null);

            return host;
        }

        private void Observe(string sql)
        {
            if (sql.Contains("lexicon_entries", StringComparison.Ordinal))
            {
                LexiconReads++;

                ReadsAdmitted &= Policy.Acquisitions > 0;

                if (sql.Contains("COUNT(*) FROM lexicon_entries", StringComparison.Ordinal))
                {
                    LegacyLexiconCounts++;
                }
            }
        }

        internal DefaultHttpContext Context(string route, Stream body)
        {
            DefaultHttpContext context = new();

            context.Response.Body = body;

            context.Request.Headers[ArcanumApiHeaders.ApiKey] = ArcanumWebApplicationFactory.TestApiKey;

            context.Request.Method = route.StartsWith("SearchMemory", StringComparison.Ordinal) ? "POST" : "GET";

            context.Request.RouteValues["name"] = "Entity";

            context.Request.RouteValues["sessionId"] = Session.ToString("D");

            if (route.EndsWith(":campaign", StringComparison.Ordinal))
            {
                context.Request.QueryString = new("?campaignId=aaaaaaaa-1111-4111-8111-111111111111");
            }

            if (route.StartsWith("SearchMemory", StringComparison.Ordinal))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(route.EndsWith(":all", StringComparison.Ordinal)
                    ? "{\"query\":\"alpha\",\"scope\":\"All\"}" : "{\"query\":\"alpha\",\"scope\":\"Lexicon\"}");

                context.Request.Body = new MemoryStream(bytes);

                context.Request.ContentLength = bytes.Length;

                context.Request.ContentType = "application/json";

                context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyDetection());
            }

            return context;
        }

        internal async Task ExecuteAsync(string route, DefaultHttpContext context)
        {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();

            context.RequestServices = scope.ServiceProvider;

            RouteEndpoint endpoint = Assert.Single(Endpoints, endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == route.Split(':')[0]);

            context.SetEndpoint(endpoint);

            await endpoint.RequestDelegate!(context);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();

            await Owner.DisposeAsync();

            _grimoire.Dispose();
        }
    }

    private sealed class BodyDetection : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class Policy : ICovenantExportPolicy
    {
        internal bool Deny { get; set; }

        internal bool Absent { get; set; }

        internal int StaleAfter { get; set; } = int.MaxValue;

        internal int Acquisitions { get; private set; }

        internal CovenantOperationScope? Scope { get; private set; }

        internal ReadLease? Lease { get; private set; }

        public ValueTask<Result<CovenantExportAdmission>> AcquireConditionalReadAsync(CovenantOperationScope? scope, CancellationToken cancellationToken)
        {
            Acquisitions++;

            Scope = scope;

            return ValueTask.FromResult(Deny
                ? Result<CovenantExportAdmission>.Failure(new Error(ErrorCodes.Covenant.Unavailable, "Unavailable."))
                : Result<CovenantExportAdmission>.Success(Absent ? CovenantExportAdmission.Absent : new(Lease = new(scope, StaleAfter))));
        }

        public Task<Result<CovenantSessionExportSensitivity>> InspectSessionAsync(Guid sessionId, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<CovenantCampaignExportExclusions>> InventoryCampaignExclusionsAsync(Guid campaignId, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ReadLease(CovenantOperationScope? scope, int staleAfter) : ICovenantSnapshotReadLease
    {
        internal int Revalidations { get; private set; }

        internal int Disposals { get; private set; }

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(Guid.NewGuid(), 1,
            scope is null ? CovenantLeaseKind.InstallationRead : CovenantLeaseKind.Read,
            scope is null ? CovenantLeaseCoverage.Installation : CovenantLeaseCoverage.Scoped,
            scope, Guid.NewGuid(), 1, 1, 0, null, null, null, null, null, false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(
            Disposals > 0 || Revalidations++ >= staleAfter
                ? Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "Revoked.")) : Result.Success());

        public ValueTask DisposeAsync()
        {
            Disposals++;

            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingBody : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Fail { get; init; }

        internal async Task WaitForWriteAsync(Task executing)
        {
            Task first = await Task.WhenAny(Entered.Task, executing).WaitAsync(TimeSpan.FromSeconds(20));

            await first;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();

            await Release.Task.WaitAsync(cancellationToken);

            if (Fail)
            {
                throw new IOException("Injected serialization failure.");
            }

            base.Write(buffer.Span);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
