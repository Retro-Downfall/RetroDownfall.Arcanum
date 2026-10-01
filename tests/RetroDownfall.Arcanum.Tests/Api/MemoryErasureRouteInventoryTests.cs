using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// The closed inventory of selective-erasure routes: every one is a static, named, authenticated route
/// that declares exactly its one operator authority and answers with the protected header tuple.
/// </summary>
/// <remarks>
/// <see cref="Routes"/> is closed. Each later erasure route joins it, and the set test fails in both
/// directions, so an erase, release, status, scrub or key-reset route that is mapped without being
/// listed here, or listed without being mapped, is caught by the same assertion.
/// </remarks>
[Collection("ApiHost")]
public sealed class MemoryErasureRouteInventoryTests(ArcanumWebApplicationFactory factory)
{
    internal static readonly (string Name, string Method, string Path, CovenantAuthorityRequirement? Authority)[] Routes =
    [
        ("PrepareSagaMemoryErasure", "POST", "/api/memory/saga/erase/prepare", CovenantAuthorityRequirement.SensitivityRetentionPurge),
        ("EraseSagaMemory", "POST", "/api/memory/saga/erase", CovenantAuthorityRequirement.SensitivityRetentionPurge),
    ];

    private static readonly Regex ErasurePath = new(
        "^/api/memory/(saga|lexicon|covenant)/(erase|release)",
        RegexOptions.CultureInvariant);

    public static TheoryData<string> PostRoutes()
    {
        TheoryData<string> rows = [];

        foreach ((string name, string method, _, _) in Routes)
        {
            if (method == "POST")
            {
                rows.Add(name);
            }
        }

        return rows;
    }

    [Fact]
    public void Every_erasure_post_is_a_static_named_route_with_exactly_its_operator_authority()
    {
        IReadOnlyList<RouteEndpoint> endpoints = Endpoints();

        foreach ((string name, string method, string path, CovenantAuthorityRequirement? authority) in Routes)
        {
            if (method != "POST")
            {
                // The status GET's authentication-only shape is pinned by the task that maps it.
                continue;
            }

            Assert.NotNull(authority);

            RouteEndpoint endpoint = Assert.Single(
                endpoints,
                candidate => string.Equals(
                    candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                    name,
                    StringComparison.Ordinal));

            Assert.Equal(path, endpoint.RoutePattern.RawText);

            Assert.Equal(["POST"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);

            Assert.Empty(endpoint.RoutePattern.Parameters);

            Assert.Equal(
                authority,
                Assert.Single(endpoint.Metadata.GetOrderedMetadata<CovenantAuthorityRequirementMetadata>()).Requirement);

            Assert.Null(endpoint.Metadata.GetMetadata<CovenantConditionalSensitivityPurgeMetadata>());

            Assert.Null(endpoint.Metadata.GetMetadata<CovenantConditionalReadRequirementMetadata>());

            Assert.Null(endpoint.Metadata.GetMetadata<CovenantConditionalExactWriteRequirementMetadata>());
        }
    }

    [Fact]
    public void The_erasure_route_set_is_exactly_the_declared_routes()
    {
        string[] mapped =
        [
            .. Endpoints()
                .Where(static endpoint => endpoint.RoutePattern.RawText is { } raw
                    && (ErasurePath.IsMatch(raw) || raw.StartsWith("/api/memory/erasure", StringComparison.Ordinal)))
                .SelectMany(static endpoint =>
                    (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                        .Select(method => Row(
                            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? string.Empty,
                            method,
                            endpoint.RoutePattern.RawText!)))
                .Order(StringComparer.Ordinal),
        ];

        string[] declared = [.. Routes.Select(static route => Row(route.Name, route.Method, route.Path)).Order(StringComparer.Ordinal)];

        Assert.Equal(declared, mapped);
    }

    [SkippableTheory]
    [MemberData(nameof(PostRoutes))]
    public async Task Every_erasure_post_answers_with_the_protected_header_tuple(string name)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync(PathOf(name), EmptyBody());

        // Any 400 code: a store may answer its own validation code for an empty body.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.NotEmpty(await MemoryErasureRouteDriver.ReadErrorCodeAsync(response));

        AssertProtectedTuple(response);
    }

    [SkippableTheory]
    [MemberData(nameof(PostRoutes))]
    public async Task Erasure_posts_refuse_a_host_tools_tainted_installation(string name)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        TaintSwitch taint = new();

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(credentials);

        host.ServiceOverrides += services =>
        {
            services.RemoveAll<ICovenantAuthoritySnapshotProvider>();

            services.AddSingleton<ICovenantAuthoritySnapshotProvider>(provider =>
            {
                taint.Inner = provider.GetRequiredService<CovenantAuthoritySnapshotProvider>();

                return taint;
            });
        };

        HttpClient client = host.CreateAuthenticatedClient();

        MemoryErasureRouteDriver driver = new(client);

        string memoryId = await MemoryErasureRouteDriver.InsertSagaAsync(host, "The ward-stone lies under the mill.");

        // A genuine body, built while the installation is clean: a refusal of {} could never have
        // changed anything, so it would prove nothing about this one.
        (string body, Guid mutationId) = await GenuineBodyAsync(name, client, driver, memoryId);

        taint.Tainted = true;

        using (HttpResponseMessage refused = await client.PostAsync(PathOf(name), Json(body)))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

            Assert.Equal(
                ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));

            AssertProtectedTuple(refused);
        }

        taint.Tainted = false;

        using (HttpResponseMessage shown = await client.GetAsync($"/api/memory/saga/{memoryId}"))
        {
            Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        }

        Assert.Equal(0, await ReceiptsAsync(host, mutationId));

        foreach (MemoryReviewStore store in Enum.GetValues<MemoryReviewStore>())
        {
            Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(host, store));
        }

        // The same body on the clean installation is accepted, so the refusal above is what stopped it.
        using (HttpResponseMessage accepted = await client.PostAsync(PathOf(name), Json(body)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using IServiceScope check = host.Services.CreateScope();

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(await OpenAsync(check));
    }

    /// <summary>
    /// The driver authenticates its own requests and leaves the caller's client as it found it, so a
    /// test that keeps that client for an unauthenticated probe still gets 401 from it.
    /// </summary>
    [SkippableFact]
    public async Task The_route_driver_authenticates_its_own_requests_and_leaves_the_callers_client_alone()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient bare = factory.CreateClient();

        MemoryErasureRouteDriver driver = new(bare);

        Assert.False(bare.DefaultRequestHeaders.Contains(ArcanumApiHeaders.ApiKey));

        string path = Routes[0].Path;

        using (HttpResponseMessage viaDriver = await driver.PostAsync(
            path,
            new SagaErasePrepareRequest("not-a-guid", new string('0', 64), null, Guid.NewGuid()),
            ArcanumJsonContext.Default.SagaErasePrepareRequest))
        {
            Assert.Equal(HttpStatusCode.BadRequest, viaDriver.StatusCode);
        }

        using HttpResponseMessage direct = await bare.PostAsync(path, EmptyBody());

        Assert.Equal(HttpStatusCode.Unauthorized, direct.StatusCode);
    }

    [SkippableFact]
    public async Task Erasure_routes_refuse_an_unauthenticated_caller()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateClient();

        foreach ((_, string method, string path, _) in Routes)
        {
            using HttpRequestMessage request = new(new HttpMethod(method), path);

            if (method == "POST")
            {
                request.Content = EmptyBody();
            }

            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private IReadOnlyList<RouteEndpoint> Endpoints()
    {
        _ = factory.CreateAuthenticatedClient();

        return [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];
    }

    /// <summary>
    /// A body each erasure POST would act on: the exact target its store's show reports, and for an
    /// apply the token its prepare issued. A route added to <see cref="Routes"/> adds its body here.
    /// </summary>
    private static async Task<(string Body, Guid MutationId)> GenuineBodyAsync(
        string name,
        HttpClient client,
        MemoryErasureRouteDriver driver,
        string sagaMemoryId)
    {
        using HttpResponseMessage shown = await client.GetAsync($"/api/memory/saga/{sagaMemoryId}");

        SagaMemoryDetail detail = await MemoryErasureRouteDriver.ReadDataAsync(
            shown,
            ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail);

        SagaErasePrepareRequest prepare = new(sagaMemoryId, detail.ContentHash, detail.Claim?.CurrentVersionId, Guid.NewGuid());

        switch (name)
        {
            case "PrepareSagaMemoryErasure":
                return (JsonSerializer.Serialize(prepare, ArcanumJsonContext.Default.SagaErasePrepareRequest), prepare.MutationId);

            case "EraseSagaMemory":
                using (HttpResponseMessage prepared = await driver.PostAsync(
                    PathOf("PrepareSagaMemoryErasure"),
                    prepare,
                    ArcanumJsonContext.Default.SagaErasePrepareRequest))
                {
                    MemoryErasurePreflightDto preflight = await MemoryErasureRouteDriver.ReadDataAsync(
                        prepared,
                        ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto);

                    SagaEraseRequest apply = new(
                        prepare.MemoryId,
                        prepare.ExpectedContentHash,
                        prepare.ExpectedClaimVersionId,
                        prepare.MutationId,
                        preflight.PreflightToken);

                    return (JsonSerializer.Serialize(apply, ArcanumJsonContext.Default.SagaEraseRequest), prepare.MutationId);
                }

            default:
                throw new InvalidOperationException($"Give the erasure route {name} a genuine body here.");
        }
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<SqliteConnection> OpenAsync(IServiceScope scope)
    {
        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync();
        }

        return connection;
    }

    private static async Task<long> ReceiptsAsync(ArcanumWebApplicationFactory host, Guid mutationId)
    {
        using IServiceScope scope = host.Services.CreateScope();

        await using SqliteCommand command = (await OpenAsync(scope)).CreateCommand();

        command.CommandText = "SELECT count(*) FROM memory_erasure_receipts WHERE MutationId = $mutation;";

        _ = command.Parameters.AddWithValue("$mutation", mutationId.ToString("D").ToUpperInvariant());

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static string Row(string name, string method, string path) => $"{name} {method} {path}";

    private static string PathOf(string name) =>
        Routes.Single(route => string.Equals(route.Name, name, StringComparison.Ordinal)).Path;

    private static StringContent EmptyBody() => new("{}", Encoding.UTF8, "application/json");

    /// <summary>The tuple every erasure response carries whatever its status (API §8.29, §8.35).</summary>
    private static void AssertProtectedTuple(HttpResponseMessage response)
    {
        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());

        Assert.Equal("no-cache", Assert.Single(response.Headers.GetValues("Pragma")));

        Assert.Equal("0", Assert.Single(response.Content.Headers.GetValues("Expires")));

        Assert.Null(response.Headers.ETag);

        Assert.False(response.Content.Headers.Contains("Last-Modified"));
    }

    /// <summary>
    /// The host's published authority, reported as host-tools tainted while <see cref="Tainted"/> is
    /// set, exactly as a tainted installation publishes it.
    /// </summary>
    private sealed class TaintSwitch : ICovenantAuthoritySnapshotProvider
    {
        private int _tainted;

        internal ICovenantAuthoritySnapshotProvider? Inner { get; set; }

        internal bool Tainted
        {
            get => Volatile.Read(ref _tainted) != 0;
            set => Volatile.Write(ref _tainted, value ? 1 : 0);
        }

        public CovenantAuthoritySnapshot? Current =>
            Inner?.Current is { } current && Tainted
                ? current with { HostToolsState = CovenantHostToolsState.HostToolsTainted }
                : Inner?.Current;
    }
}
