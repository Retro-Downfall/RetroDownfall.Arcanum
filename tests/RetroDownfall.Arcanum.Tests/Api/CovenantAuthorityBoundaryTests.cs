using System.Net;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Issue #89 — every Covenant decision that must happen before the request body is read.
/// </summary>
/// <remarks>
/// The ordering is the whole contract: API key, then context policy, then authority, and only then
/// body-size enforcement and model binding. Minimal-API endpoint filters run <em>after</em> binding,
/// so a check that lived in a filter alone would already have had the body read, buffered, and
/// deserialized — and <c>POST /v1/files</c> raises the multipart ceiling to 513 MiB, which makes
/// "after binding" mean half a gigabyte spooled to disk for a caller who was never going to be
/// allowed to send it.
///
/// <para>Authentication stays strictly first. A wrong key plus a malformed policy header is a 401,
/// not a 400: a 400 would confirm to an unauthenticated caller both that they reached a real route
/// and that their header spelling was the only thing wrong with the request.</para>
/// </remarks>
public sealed class CovenantAuthorityBoundaryTests
{

    private const string ApiKey = "covenant-boundary-test-key";

    [Fact]
    public async Task A_wrong_api_key_is_401_before_the_context_policy_is_even_parsed()
    {

        await using BoundaryHost host = await BoundaryHost.CreateAsync();

        HttpRequestMessage request = new(HttpMethod.Post, "/api/probe")
        {
            Content = new StringContent("""{"prompt":"hi"}""", Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(ArcanumApiHeaders.ApiKey, "wrong-key");

        request.Headers.Add(ArcanumApiHeaders.ContextPolicy, "NOPE");

        HttpResponseMessage response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal(0, host.BodyBytesRead);

    }

    [Theory]
    [InlineData("NONE")]
    [InlineData("None")]
    [InlineData("default")]
    [InlineData("none,none")]
    public async Task An_invalid_context_policy_is_400_before_binding(string value)
    {

        await using BoundaryHost host = await BoundaryHost.CreateAsync();

        HttpResponseMessage response = await host.SendAsync("/api/probe", value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Nothing read the body. A binding failure would have produced a 400 too, which is exactly
        // why the byte count is asserted rather than the status alone.
        Assert.Equal(0, host.BodyBytesRead);

        Assert.Equal(0, host.HandlerInvocations);

    }

    [Fact]
    public async Task A_valid_none_reaches_the_handler_and_is_echoed_back()
    {

        await using BoundaryHost host = await BoundaryHost.CreateAsync();

        HttpResponseMessage response = await host.SendAsync("/api/probe", "none");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, host.HandlerInvocations);

        Assert.Equal("none", Assert.Single(response.Headers.GetValues(ArcanumApiHeaders.ContextPolicy)));

        Assert.Equal(CovenantContextPolicy.None, host.ObservedPolicy);

    }

    [Fact]
    public async Task An_absent_header_is_the_default_policy_and_is_not_echoed()
    {

        await using BoundaryHost host = await BoundaryHost.CreateAsync();

        HttpResponseMessage response = await host.SendAsync("/api/probe", value: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.False(response.Headers.Contains(ArcanumApiHeaders.ContextPolicy));

        Assert.Equal(CovenantContextPolicy.Default, host.ObservedPolicy);

    }

    [Fact]
    public async Task A_route_that_never_injects_context_refuses_the_header_rather_than_ignoring_it()
    {

        await using BoundaryHost host = await BoundaryHost.CreateAsync();

        HttpResponseMessage response = await host.SendAsync("/api/plain", "none");

        // Silently ignoring it is the dangerous outcome: the caller believes it suppressed durable
        // context on a route that never consulted the header at all.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(0, host.HandlerInvocations);

    }

    [Fact]
    public async Task An_operator_route_without_issued_authority_is_refused_by_the_filter()
    {

        await using BoundaryHost host = await BoundaryHost.CreateAsync(issuer: null);

        HttpResponseMessage response = await host.SendAsync("/api/manage", value: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        Assert.Equal(0, host.HandlerInvocations);

    }

    [Fact]
    public async Task An_operator_route_issues_a_context_bound_to_its_own_requirement()
    {

        StubIssuer issuer = new();

        await using BoundaryHost host = await BoundaryHost.CreateAsync(issuer);

        HttpResponseMessage response = await host.SendAsync("/api/manage", value: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(CovenantAuthorityRequirement.CovenantManage, Assert.Single(issuer.Issued));

    }

    [Fact]
    public async Task A_stale_authority_epoch_is_refused_after_binding_and_before_the_handler()
    {

        StubIssuer issuer = new() { RevalidationFails = true };

        await using BoundaryHost host = await BoundaryHost.CreateAsync(issuer);

        HttpResponseMessage response = await host.SendAsync("/api/manage", value: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.Equal(0, host.HandlerInvocations);

    }

    [Fact]
    public async Task Every_protected_refusal_carries_the_exact_private_header_tuple()
    {

        StubIssuer issuer = new() { RevalidationFails = true };

        await using BoundaryHost host = await BoundaryHost.CreateAsync(issuer);

        HttpResponseMessage response = await host.SendAsync("/api/manage", value: null);

        // A refusal without the tuple is a cacheable "no", and an intermediary replaying a stale 503
        // to a caller who now has authority is a fault an operator cannot see from their side.
        Assert.Equal("no-store, private", string.Join(", ", response.Headers.CacheControl!.ToString()));

        Assert.Equal("no-cache", Assert.Single(response.Headers.GetValues("Pragma")));

        Assert.Equal("0", Assert.Single(response.Content.Headers.GetValues("Expires")));

    }

    [Fact]
    public async Task A_protected_success_carries_the_exact_private_header_tuple()
    {

        StubIssuer issuer = new();

        await using BoundaryHost host = await BoundaryHost.CreateAsync(issuer);

        HttpResponseMessage response = await host.SendAsync("/api/manage", value: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());

    }

    [Fact]
    public void An_operator_requirement_cannot_be_declared_as_a_protected_read()
    {

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        WebApplication app = builder.Build();

        RouteHandlerBuilder route = app.MapGet("/x", () => Results.Ok());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            route.RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.ProtectedRead));

    }

    /// <summary>
    /// The real issuer over a stub snapshot, so issuance is production code and only revalidation is
    /// steered.
    /// </summary>
    /// <remarks>
    /// A hand-written issuer would be free to mint a context the production one refuses, and this
    /// suite's whole subject is whether the boundary demands a real one.
    /// </remarks>
    private sealed class StubIssuer : IOperatorAuthorityContextIssuer
    {

        private readonly OperatorAuthorityContextIssuer _inner =
            new(new StubSnapshotProvider());

        public List<CovenantAuthorityRequirement> Issued { get; } = [];

        public bool RevalidationFails { get; init; }

        public Result<OperatorAuthorityContext> Issue(CovenantAuthorityRequirement requirement)
        {

            Issued.Add(requirement);

            return _inner.Issue(requirement);

        }

        public Result<CovenantReadAuthorityEpoch> IssueReadEpoch() => _inner.IssueReadEpoch();

        public Result Revalidate(OperatorAuthorityContext context) =>
            RevalidationFails
                ? Result.Failure(
                    new Error(ErrorCodes.Covenant.OperatorAuthorityUnavailable, "authority moved on"))
                : _inner.Revalidate(context);

        private sealed class StubSnapshotProvider : ICovenantAuthoritySnapshotProvider
        {

            public CovenantAuthoritySnapshot? Current { get; } = new(
                InstallationIdentity: "boundary-test",
                AuthorityEpoch: 4,
                MasterKeyVersion: 1,
                RecoveryEnvelopeEpoch: 1,
                HostToolsState: CovenantHostToolsState.Clean,
                TransitionId: null);

        }

    }

    private sealed class StubSecretStore(string apiKey) : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class BoundaryHost : IAsyncDisposable
    {

        private WebApplication _app = null!;

        public HttpClient Client { get; private set; } = null!;

        public int BodyBytesRead { get; private set; }

        public int HandlerInvocations { get; private set; }

        public CovenantContextPolicy ObservedPolicy { get; private set; }

        public static async Task<BoundaryHost> CreateAsync(IOperatorAuthorityContextIssuer? issuer = null)
        {

            BoundaryHost host = new();

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

            builder.WebHost.UseTestServer();

            builder.Services.AddSingleton<ISecretStore>(new StubSecretStore(ApiKey));

            builder.Services.AddSingleton<IApiKeyDigestCache>(new ApiKeyDigestCache(TimeProvider.System));

            builder.Services.AddSingleton<ApiKeyAuthenticator>();

            if (issuer is not null)
            {

                builder.Services.AddSingleton(issuer);

            }

            builder.Services.ConfigureHttpJsonOptions(static options =>
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

            WebApplication app = builder.Build();

            app.Use(async (HttpContext context, Func<Task> next) =>
            {

                context.Request.Body = new CountingStream(context.Request.Body, host);

                await next().ConfigureAwait(false);

            });

            app.UseArcanumApiKeyAuthentication();

            RouteGroupBuilder api = app.MapGroup("/api").RequireArcanumApiKey();

            api.MapPost(
                    "/probe",
                    (PingRequest body, HttpContext ctx) =>
                    {

                        _ = body;

                        host.HandlerInvocations++;

                        host.ObservedPolicy = CovenantRequestFeatures.ContextPolicy(ctx);

                        return Results.Ok();

                    })
                .AllowCovenantContext();

            api.MapPost(
                "/plain",
                (PingRequest body) =>
                {

                    _ = body;

                    host.HandlerInvocations++;

                    return Results.Ok();

                });

            api.MapPost(
                    "/manage",
                    () =>
                    {

                        host.HandlerInvocations++;

                        return Results.Ok();

                    })
                .RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.CovenantManage);

            await app.StartAsync();

            host._app = app;

            host.Client = app.GetTestClient();

            return host;

        }

        public Task<HttpResponseMessage> SendAsync(string path, string? value)
        {

            HttpRequestMessage request = new(HttpMethod.Post, path)
            {
                Content = new StringContent("""{"prompt":"hi"}""", Encoding.UTF8, "application/json"),
            };

            request.Headers.Add(ArcanumApiHeaders.ApiKey, ApiKey);

            if (value is not null)
            {

                request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ContextPolicy, value);

            }

            return Client.SendAsync(request);

        }

        public async ValueTask DisposeAsync()
        {

            Client?.Dispose();

            await _app.DisposeAsync();

        }

        private sealed class CountingStream(Stream inner, BoundaryHost host) : Stream
        {

            public override bool CanRead => inner.CanRead;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => throw new NotSupportedException();
            }

            public override void Flush() => inner.Flush();

            public override int Read(byte[] buffer, int offset, int count)
            {

                int read = inner.Read(buffer, offset, count);

                host.BodyBytesRead += read;

                return read;

            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {

                int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                host.BodyBytesRead += read;

                return read;

            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        }

    }

}
