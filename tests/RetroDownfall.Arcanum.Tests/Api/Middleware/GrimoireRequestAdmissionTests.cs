using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Middleware;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Api.Middleware;

/// <summary>
/// The pre-endpoint admission stage: which requests it decides for, which it must never touch, and
/// how long the lease it takes survives.
/// </summary>
/// <remarks>
/// The host here is a probe rather than the composed one, because every assertion is about the
/// pipeline's shape rather than any endpoint's behaviour, and only a probe can prove the negative
/// cases — that a refused request never reached its endpoint at all.
/// </remarks>
public sealed class GrimoireRequestAdmissionTests
{

    private const string ApiKey = "admission-key";

    private static readonly TimeSpan OpeningTimeout = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("/api/probe")]
    [InlineData("/API/probe")]
    [InlineData("/v1/probe")]
    [InlineData("/V1/probe")]
    public async Task A_protected_request_is_refused_before_its_endpoint_runs(string path)
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        host.CloseAdmission();

        using HttpResponseMessage response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.Equal(0, host.EndpointRuns);

    }

    /// <summary>
    /// The exclusions are a property of segment-safe matching, not an allow-list that can drift.
    /// </summary>
    /// <remarks>
    /// <c>/apiary</c> and <c>/v10</c> are the two the parent design names, and they are named because
    /// a plain <c>StartsWith</c> sweeps both in — the same defect the A2A path policy exists to
    /// correct. <c>/metrics</c> is the one route outside both prefixes that is still authenticated,
    /// so it also proves admission is selected by path rather than by API-key metadata.
    /// </remarks>
    [Theory]
    [InlineData("/metrics")]
    [InlineData("/apiary")]
    [InlineData("/apiary/a2a")]
    [InlineData("/v10/probe")]
    [InlineData("/v1x")]
    public async Task A_path_outside_the_two_prefixes_is_never_refused(string path)
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        host.CloseAdmission();

        using HttpResponseMessage response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, host.EndpointRuns);

    }

    /// <summary>
    /// Endpoint matching still precedes admission, so a path that names no route is still a 404.
    /// </summary>
    /// <remarks>
    /// A request that matched nothing takes the anonymous branch and is never authenticated, so
    /// refusing it by path alone would answer 503 to a caller who presented no key — and would tell
    /// that caller both that <c>/api</c> is a real prefix and that this installation is under
    /// maintenance. It also runs no endpoint, so there is nothing for admission to prevent.
    /// </remarks>
    [Fact]
    public async Task A_path_that_matched_no_endpoint_is_still_a_not_found()
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        host.CloseAdmission();

        using HttpResponseMessage response = await host.GetAsync("/api/nothing-is-mapped-here");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [Fact]
    public async Task A_wrong_method_is_still_a_method_not_allowed()
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        host.CloseAdmission();

        using HttpResponseMessage response = await host.PostAsync("/api/probe");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);

    }

    /// <summary>
    /// Authentication stays strictly first: a bad key is a 401 whether or not maintenance is running.
    /// </summary>
    /// <remarks>
    /// A 503 here would confirm to an unauthenticated caller that they reached a real route, which is
    /// the disclosure the existing 401-before-400 rule already exists to prevent.
    /// </remarks>
    [Fact]
    public async Task A_bad_key_is_still_unauthorized_and_never_a_maintenance_refusal()
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        host.CloseAdmission();

        using HttpResponseMessage response = await host.GetAsync("/api/probe", key: "wrong-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal(0, host.EndpointRuns);

    }

    /// <summary>
    /// An anonymous route under <c>/api</c> is protected by path authority, not by key metadata.
    /// </summary>
    [Fact]
    public async Task An_anonymous_route_under_api_is_still_refused()
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        host.CloseAdmission();

        using HttpResponseMessage response = await host.GetAsync("/api/anonymous", key: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.Equal(0, host.EndpointRuns);

    }

    [Fact]
    public async Task An_exempt_route_runs_while_admission_is_closed()
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        host.CloseAdmission();

        using HttpResponseMessage response = await host.GetAsync("/api/exempt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, host.EndpointRuns);

    }

    [Fact]
    public async Task An_ordinary_gate_admits_the_request_and_the_endpoint_runs()
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        using HttpResponseMessage response = await host.GetAsync("/api/probe");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, host.EndpointRuns);

    }

    [Fact]
    public async Task The_api_refusal_is_the_documented_envelope()
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        host.CloseAdmission();

        using HttpResponseMessage response = await host.GetAsync("/api/probe");

        ApiResponse<string>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseString);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Grimoire.MaintenanceUnavailable, body.Error?.Code);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());

    }

    [Fact]
    public async Task The_v1_refusal_is_the_documented_openai_envelope()
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        host.CloseAdmission();

        using HttpResponseMessage response = await host.GetAsync("/v1/probe");

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("service_unavailable", body.Error.Type);

    }

    /// <summary>
    /// The lease is still held while every other scoped disposable is being released.
    /// </summary>
    /// <remarks>
    /// The sentinel is resolved by the endpoint, i.e. after the middleware resolved the holder, so
    /// the container disposes the sentinel first and the holder last. A holder released in a
    /// middleware <c>finally</c> would invert that, and the writes that run on response completion —
    /// the idempotency claim persist among them — would find no live lifetime and be refused.
    /// </remarks>
    [Fact]
    public async Task The_lease_outlives_every_other_scoped_disposable()
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        using HttpResponseMessage response = await host.GetAsync("/api/sentinel");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(await host.LeaseHeldAtSentinelDisposalAsync());

    }

    /// <summary>
    /// A gate that closes under an in-flight request answers on the wire exactly as admission does.
    /// </summary>
    /// <remarks>
    /// Driven through the real exception middleware rather than by calling the handler, because the
    /// framework clears cache headers on its way to a handler and sets the status itself. Whether the
    /// protected tuple survives that is a property of the composed pipeline, and #128 requires it on
    /// every protected refusal — including one no handler wrote.
    /// </remarks>
    [Fact]
    public async Task An_in_flight_refusal_carries_the_same_envelope_and_headers_on_the_wire()
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        using HttpResponseMessage response = await host.GetAsync("/api/throws");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        ApiResponse<string>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseString);

        Assert.NotNull(body);

        Assert.Equal(ErrorCodes.Grimoire.MaintenanceUnavailable, body.Error?.Code);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());

        Assert.Equal("no-cache", response.Headers.Pragma.ToString());

        Assert.Null(response.Headers.ETag);

    }

    /// <summary>
    /// An admitted request can still reach the Grimoire once maintenance begins closing under it.
    /// </summary>
    /// <remarks>
    /// Admission records the request's ordinary lifetime in the gate's static <c>AsyncLocal</c>, and
    /// that lifetime is the only thing that distinguishes an already-admitted request from a new one
    /// while the gate is <c>Closing</c>: without it every open is refused, including the reset
    /// request's own, which is promoted out of its drain precisely so it can finish its transition.
    ///
    /// <para>The assertion is made from inside the endpoint because that is where the property has to
    /// hold, and because it is the one place a defect in how the lease is taken shows up. Taking the
    /// lease behind an <c>await</c> discards the lifetime on return, and a test that admitted and
    /// checked in the same frame would pass over exactly that defect.</para>
    /// </remarks>
    [Fact]
    public async Task An_admitted_request_can_still_open_the_grimoire_once_closing_begins()
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        using HttpResponseMessage response = await host.GetAsync("/api/lifetime");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, host.EndpointRuns);

        Assert.True(
            host.OrdinaryOpenSucceededWhileClosing,
            "the admitted request had no live ordinary lifetime once the gate began closing");

    }

    /// <summary>
    /// The lease kind comes from the route's own marker, not from the path or the handler.
    /// </summary>
    /// <remarks>
    /// Asserted through the lease the gate actually issued rather than through any downstream
    /// behaviour, because the kind is the only thing that decides whether a transition revokes this
    /// request at all. A test that inferred it from an observed refusal would pass equally well on a
    /// stage that had guessed right for the wrong reason.
    ///
    /// <para>An unmarked route stays <see cref="GrimoireRequestKind.Finite"/> deliberately: a finite
    /// lease is drained through completion, so a streaming route whose marker was forgotten makes a
    /// transition slow rather than cutting a response mid-frame. The inventory is what stops the
    /// marker staying forgotten.</para>
    /// </remarks>
    [Theory]
    [InlineData("/api/quiesceable", true)]
    [InlineData("/api/billable", false)]
    [InlineData("/api/finite-stream", false)]
    [InlineData("/api/probe", false)]
    public async Task The_route_marker_decides_which_kind_of_lease_the_request_takes(
        string path,
        bool expectedQuiesceable)
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        using HttpResponseMessage response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(expectedQuiesceable, host.AdmittedQuiesceable);

    }

    /// <summary>
    /// Beginning a transition revokes a quiesceable stream and leaves every finite request alone.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the kind exists. The gate collects revocation sources only from
    /// <see cref="GrimoireRequestKind.QuiesceableStream"/> leases, so a route that took the wrong kind
    /// is either never told to stop or is told to stop when it should have been drained — and neither
    /// failure is visible from the request's own response.
    /// </remarks>
    [Theory]
    [InlineData("/api/quiesceable", true)]
    [InlineData("/api/billable", false)]
    [InlineData("/api/finite-stream", false)]
    [InlineData("/api/probe", false)]
    public async Task Only_a_quiesceable_lease_is_revoked_when_a_transition_begins(
        string path,
        bool expectedRevoked)
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        host.Gate(entered, release);

        Task<HttpResponseMessage> pending = host.GetAsync(path);

        await entered.Task.WaitAsync(BoundedWait);

        await using IGrimoireClosingOwner closing = host.BeginClosing();

        Assert.Equal(expectedRevoked, host.RevocationSignalled);

        release.SetResult();

        using HttpResponseMessage response = await pending;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    }

    /// <summary>
    /// A request admitted before stage one holds the drain open until it is finished.
    /// </summary>
    [Fact]
    public async Task A_closing_gate_waits_for_an_already_admitted_request()
    {

        await using AdmissionProbeHost host = await AdmissionProbeHost.StartAsync();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        host.Gate(entered, release);

        Task<HttpResponseMessage> pending = host.GetAsync("/api/gated");

        await entered.Task.WaitAsync(BoundedWait);

        await using IGrimoireClosingOwner closing = host.BeginClosing();

        Task<Result> drain = host.Admission
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask();

        Assert.False(drain.IsCompleted);

        release.SetResult();

        using HttpResponseMessage response = await pending;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Result drained = await drain.WaitAsync(BoundedWait);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

    }

    /// <summary>
    /// A probe host composing exactly the pipeline stages admission has to sit between.
    /// </summary>
    private sealed class AdmissionProbeHost : IAsyncDisposable
    {

        private readonly WebApplication _app;

        private TaskCompletionSource? _entered;

        private TaskCompletionSource? _release;

        private int _endpointRuns;

        private int _leaseHeldAtSentinelDisposal = -1;

        private int _ordinaryOpenWhileClosing = -1;

        private IGrimoireRequestLease? _admittedLease;

        private AdmissionProbeHost(WebApplication app, GrimoireConnectionAdmissionGate admission)
        {

            _app = app;

            Admission = admission;

            Client = app.GetTestClient();

        }

        internal GrimoireConnectionAdmissionGate Admission { get; }

        internal HttpClient Client { get; }

        internal int EndpointRuns => Volatile.Read(ref _endpointRuns);

        internal bool OrdinaryOpenSucceededWhileClosing =>
            Volatile.Read(ref _ordinaryOpenWhileClosing) == 1;

        /// <summary>
        /// Whether the lease the admission stage took was the quiesceable kind, as the gate issued it.
        /// </summary>
        /// <remarks>
        /// A bool rather than the kind itself only because <c>GrimoireRequestKind</c> is internal to
        /// Infrastructure and a public xUnit theory cannot carry it as a parameter. The read is still
        /// of the real lease.
        /// </remarks>
        internal bool AdmittedQuiesceable { get; private set; }

        /// <summary>
        /// Whether the admitted lease's maintenance revocation has fired.
        /// </summary>
        /// <remarks>
        /// Read from the captured lease rather than from the endpoint, because the whole point is what
        /// the gate did to a request that is still running when a transition began.
        /// </remarks>
        internal bool RevocationSignalled =>
            _admittedLease?.MaintenanceRevocation.IsCancellationRequested ?? false;

        internal void RecordOrdinaryOpenWhileClosing(bool succeeded) =>
            Interlocked.Exchange(ref _ordinaryOpenWhileClosing, succeeded ? 1 : 0);

        internal static async Task<AdmissionProbeHost> StartAsync()
        {

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

            builder.WebHost.UseTestServer();

            GrimoireConnectionAdmissionGate admission = new(
                TimeProvider.System,
                new NoOpConnectionDrain(),
                OpeningTimeout);

            AdmissionProbeHost? host = null;

            builder.Services.AddSingleton<ISecretStore>(new StubSecretStore(ApiKey));

            builder.Services.AddSingleton<IApiKeyDigestCache>(
                new ApiKeyDigestCache(TimeProvider.System));

            builder.Services.AddSingleton<ApiKeyAuthenticator>();

            builder.Services.AddExceptionHandler<ArcanumExceptionHandler>();

            builder.Services.AddProblemDetails();

            builder.Services.AddSingleton(admission);

            builder.Services.AddSingleton<IGrimoireConnectionAdmissionGate>(admission);

            builder.Services.AddScoped(
                static sp => new GrimoireRequestAdmissionScope(
                    sp.GetRequiredService<IGrimoireConnectionAdmissionGate>()));

            builder.Services.AddScoped(sp => new DisposalSentinel(
                sp.GetRequiredService<GrimoireRequestAdmissionScope>(),
                held => host!.RecordSentinelDisposal(held)));

            builder.Services.ConfigureHttpJsonOptions(static options =>
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

            WebApplication app = builder.Build();

            app.UseArcanumExceptionHandler();

            app.UseArcanumApiKeyAuthentication();

            RouteGroupBuilder api = app.MapGroup("/api").RequireArcanumApiKey();

            RouteGroupBuilder v1 = app.MapGroup("/v1").RequireArcanumApiKey();

            _ = api.MapGet(
                "/probe",
                async (GrimoireRequestAdmissionScope admission) =>
                    await host!.CaptureAndParkAsync(admission));

            _ = api.MapGet("/exempt", () => host!.Ran())
                .WithMetadata(GrimoireAdmissionExemptRouteMetadata.Instance);

            _ = api.MapGet("/sentinel", (DisposalSentinel sentinel) => host!.Ran());

            // Admitted, then the gate closes under it and the endpoint reaches SQLite anyway.
            _ = api.MapGet("/throws", IResult () =>
            {

                _ = host!.Ran();

                throw new GrimoireMaintenanceUnavailableException();

            });

            _ = api.MapGet("/lifetime", async () =>
            {

                await using IGrimoireClosingOwner closing = host!.BeginClosing();

                using SqliteConnection connection = new();

                try
                {

                    using IGrimoireConnectionOpenTicket ticket = admission.AcquireOrdinaryOpen(connection);

                    ticket.MarkFailed();

                    host.RecordOrdinaryOpenWhileClosing(succeeded: true);

                }
                catch (GrimoireMaintenanceUnavailableException)
                {

                    host.RecordOrdinaryOpenWhileClosing(succeeded: false);

                }

                return host.Ran();

            });

            _ = api.MapGet("/gated", async () =>
            {

                host!._entered?.TrySetResult();

                if (host._release is not null)
                {

                    await host._release.Task.ConfigureAwait(false);

                }

                return host.Ran();

            });

            // The three marked shapes. Each captures the lease the admission stage took and then
            // parks on the same barrier the gated route uses, so a transition can begin while the
            // request is provably still live and the revocation it does or does not receive is
            // observable.
            _ = api.MapGet(
                    "/quiesceable",
                    async (GrimoireRequestAdmissionScope admission) =>
                        await host!.CaptureAndParkAsync(admission))
                .WithMetadata(GrimoireStreamRouteMetadata.Quiesceable);

            _ = api.MapGet(
                    "/billable",
                    async (GrimoireRequestAdmissionScope admission) =>
                        await host!.CaptureAndParkAsync(admission))
                .WithMetadata(GrimoireStreamRouteMetadata.BillableDrain);

            _ = api.MapGet(
                    "/finite-stream",
                    async (GrimoireRequestAdmissionScope admission) =>
                        await host!.CaptureAndParkAsync(admission))
                .WithMetadata(GrimoireStreamRouteMetadata.FiniteDrain);

            _ = v1.MapGet("/probe", () => host!.Ran());

            // Mapped on the root application rather than inside the group, exactly as the anonymous
            // A2A peer callback is: an /api-rooted path that carries no API-key metadata at all.
            _ = app.MapGet("/api/anonymous", () => host!.Ran());

            _ = app.MapGet("/metrics", () => host!.Ran()).RequireArcanumApiKey();

            _ = app.MapGet("/apiary", () => host!.Ran());

            _ = app.MapGet("/apiary/a2a", () => host!.Ran());

            _ = app.MapGet("/v10/probe", () => host!.Ran());

            _ = app.MapGet("/v1x", () => host!.Ran());

            await app.StartAsync();

            host = new AdmissionProbeHost(app, admission);

            return host;

        }

        internal IResult Ran()
        {

            _ = Interlocked.Increment(ref _endpointRuns);

            return Results.Ok();

        }

        /// <summary>
        /// Records the lease this request was admitted on, then waits if a test armed the barrier.
        /// </summary>
        /// <remarks>
        /// Capture and park are one method because the two have to happen in that order on the same
        /// request: a test that begins a transition needs the lease already recorded, and needs the
        /// request still running when it does. Parking is conditional so every existing test that
        /// calls these routes without arming a barrier still returns immediately.
        /// </remarks>
        internal async Task<IResult> CaptureAndParkAsync(GrimoireRequestAdmissionScope admission)
        {

            _admittedLease = admission.Lease;

            AdmittedQuiesceable = admission.Lease?.Kind == GrimoireRequestKind.QuiesceableStream;

            _entered?.TrySetResult();

            if (_release is not null)
            {

                await _release.Task.ConfigureAwait(false);

            }

            return Ran();

        }

        internal void CloseAdmission() => _ = BeginClosing();

        internal IGrimoireClosingOwner BeginClosing()
        {

            Result<IGrimoireClosingOwner> begun = Admission.BeginOrResumeExclusive(
                new CovenantExclusiveRecoveryOwner(
                    Guid.Parse("00000000-0000-0000-0000-000000000251"),
                    CovenantExclusiveOperation.CovenantReset,
                    new CovenantDigest([.. Enumerable.Repeat<byte>(251, 32)])));

            Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

            return begun.Value;

        }

        internal void Gate(TaskCompletionSource entered, TaskCompletionSource release)
        {

            _entered = entered;

            _release = release;

        }

        internal void RecordSentinelDisposal(bool leaseHeld) =>
            Interlocked.Exchange(ref _leaseHeldAtSentinelDisposal, leaseHeld ? 1 : 0);

        internal async Task<bool> LeaseHeldAtSentinelDisposalAsync()
        {

            // The request scope is disposed once the server is finished with the request, which is
            // not the moment the client's response task completed.
            for (int attempt = 0; attempt < 200; attempt++)
            {

                int observed = Volatile.Read(ref _leaseHeldAtSentinelDisposal);

                if (observed >= 0)
                {

                    return observed == 1;

                }

                await Task.Delay(5);

            }

            Assert.Fail("the request scope was never disposed");

            return false;

        }

        internal async Task<HttpResponseMessage> GetAsync(string path, string? key = ApiKey)
        {

            using HttpRequestMessage request = new(HttpMethod.Get, path);

            if (key is not null)
            {

                request.Headers.Add(ArcanumApiHeaders.ApiKey, key);

            }

            return await Client.SendAsync(request);

        }

        internal async Task<HttpResponseMessage> PostAsync(string path)
        {

            using HttpRequestMessage request = new(HttpMethod.Post, path);

            request.Headers.Add(ArcanumApiHeaders.ApiKey, ApiKey);

            return await Client.SendAsync(request);

        }

        public async ValueTask DisposeAsync()
        {

            Client.Dispose();

            await _app.StopAsync();

            await _app.DisposeAsync();

        }

    }

    /// <summary>
    /// A scoped disposable created after the holder, which therefore is released before it.
    /// </summary>
    private sealed class DisposalSentinel(
        GrimoireRequestAdmissionScope admission,
        Action<bool> onDisposed) : IAsyncDisposable
    {

        public ValueTask DisposeAsync()
        {

            onDisposed(admission.Lease is not null);

            return ValueTask.CompletedTask;

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

    private sealed class NoOpConnectionDrain : ICovenantConnectionDrain
    {

        public IDisposable Register(SqliteConnection connection) => new Registration();

        public Result ClearExactPoolAfterClose(SqliteConnection connection) => Result.Success();

        public Task<Result> DrainAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        private sealed class Registration : IDisposable
        {

            public void Dispose()
            {
            }

        }

    }

}
