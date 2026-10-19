using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Packaging;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Covenant;
using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class MemoryEvaluationEndpointTests
{
    [Theory]
    [InlineData("run")]
    [InlineData("get")]
    [InlineData("delete")]
    public async Task Master_key_precedes_binding_for_every_evaluation_route(string verb)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/proving-grounds/memory-evaluations/" + verb,
            new StringContent("{broken", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Unknown_get_is_a_protected_typed_404()
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/api/proving-grounds/memory-evaluations/get",
            new StringContent("{\"evaluationId\":\"11111111-1111-1111-1111-111111111111\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("MemoryEvaluation.NotFound", body.RootElement.GetProperty("error").GetProperty("code").GetString());

        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Malformed_run_is_a_constant_protected_400()
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/api/proving-grounds/memory-evaluations/run",
            new StringContent("{private-sentinel", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("private-sentinel", body);

        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("{\"evaluationId\":\"11111111-1111-1111-1111-111111111111\",\"authority\":{}}")]
    [InlineData("{\"evaluationId\":\"11111111-1111-1111-1111-111111111111\",\"surface\":\"Background\"}")]
    public async Task Status_body_cannot_supply_authority_or_surface(string json)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/api/proving-grounds/memory-evaluations/get", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("scope", "authority", "{}")]
    [InlineData("options", "tools", "[]")]
    [InlineData("workload", "history", "[]")]
    [InlineData("memory", "cache", "true")]
    [InlineData("checks", "label", "\"private-check\"")]
    public async Task Unknown_nested_members_are_rejected_by_the_wire_contract(string container, string member, string value)
    {
        string json = JsonSerializer.Serialize(MemoryEvaluationTestData.ValidRequest(), ArcanumJsonContext.Default.MemoryEvaluationRequest);

        using var document = JsonDocument.Parse(json);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;

        var target = container == "checks" ? node[container]![0]! : node[container]!;

        target[member] = System.Text.Json.Nodes.JsonNode.Parse(value);

        var http = new DefaultHttpContext();

        http.Request.ContentType = "application/json";

        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(node.ToJsonString()));

        var read = await MemoryEvaluationRequestJson.ReadAsync(http, ArcanumJsonContext.Default.MemoryEvaluationRequest);

        Assert.NotNull(read.Error);

        http.Response.Body = new MemoryStream();

        await read.Error!.ExecuteAsync(http);

        Assert.Equal(400, http.Response.StatusCode);
    }

    [Theory]
    [InlineData("utf-16", false)]
    [InlineData("utf-8", true)]
    public async Task Raw_reader_refuses_transcoding_and_invalid_utf8(string charset, bool invalid)
    {
        var http = new DefaultHttpContext();

        http.Request.ContentType = "application/json; charset=" + charset;

        http.Request.Body = new MemoryStream(invalid ? [0x22, 0xC3, 0x28, 0x22] : Encoding.Unicode.GetBytes("{}"));

        var read = await MemoryEvaluationRequestJson.ReadAsync(http, ArcanumJsonContext.Default.MemoryEvaluationGetRequest);

        Assert.NotNull(read.Error);
    }

    [Fact]
    public async Task Unknown_length_body_stops_at_raw_limit_plus_one_and_known_oversize_is_not_read()
    {
        foreach (bool known in new[] { false, true })
        {
            var http = new DefaultHttpContext();

            http.Request.ContentType = "application/json";

            var stream = new CountedStream(new byte[MemoryEvaluationLimits.RequestBytes * 2]);

            http.Request.Body = stream;

            if (known) http.Request.ContentLength = stream.Length;

            var read = await MemoryEvaluationRequestJson.ReadAsync(http, ArcanumJsonContext.Default.MemoryEvaluationRequest);

            Assert.NotNull(read.Error);

            Assert.Equal(known ? 0 : MemoryEvaluationLimits.RequestBytes + 1, stream.ReadBytes);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Source_generated_result_delivers_202_and_refuses_late_original_authority_rotation(bool rotate, bool absent)
    {
        var authority = new RequestAuthorityProvider();

        var gate = CovenantOperationGateFixture.CreateGate();

        var issuer = new OperatorAuthorityContextIssuer(authority);

        var http = new DefaultHttpContext();

        var original = issuer.Issue(CovenantAuthorityRequirement.ProtectedRead).Value;

        CovenantRequestFeatures.RecordAuthority(http, new(original, original.Requirement, original.AuthorityEpoch));

        var opened = await new MemoryEvaluationRequestScopeFactory(new GlobalResolver(), gate, issuer)
            .OpenAsync(http, new(MemoryEvaluationScopeKind.Global), default);

        Assert.True(opened.IsSuccess);

        await using var scope = opened.Value;

        var response = new MemoryEvaluationResponse(Guid.NewGuid(), Guid.NewGuid(), MemoryEvaluationState.Running, MemoryEvaluationReason.None, null);

        Result<MemoryEvaluationResponse> payload = absent ? new Error("MemoryEvaluation.NotFound", "Absent before creation.") : response;

        var result = new MemoryEvaluationJsonResult<MemoryEvaluationResponse>(scope, payload, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse, 202);

        if (rotate) authority.RotateMasterKey();

        Assert.True((await gate.ValidateTurnLeaseAsync(scope.Lease, default)).IsSuccess);

        http.Response.Body = new MemoryStream();

        await result.ExecuteAsync(http);

        Assert.Equal(rotate ? 409 : absent ? 404 : 202, http.Response.StatusCode);

        http.Response.Body.Position = 0;

        var body = await JsonSerializer.DeserializeAsync(http.Response.Body, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse);

        Assert.NotNull(body);

        Assert.Equal(!rotate && !absent, body.IsSuccess);

        if (rotate) Assert.Equal("MemoryEvaluation.StaleAuthority", body.Error!.Value.Code);
        else if (absent) Assert.Equal("MemoryEvaluation.NotFound", body.Error!.Value.Code);
        else
        {
            Assert.Equal(response, body.Data);

            Assert.True((await gate.ValidateTurnLeaseAsync(scope.Lease, default)).IsSuccess);
        }
    }

    [Theory]
    [InlineData("MemoryEvaluation.InvalidRequest", 400)]
    [InlineData("MemoryEvaluation.InvalidScope", 400)]
    [InlineData("MemoryEvaluation.InvalidEvaluation", 400)]
    [InlineData("MemoryEvaluation.InvalidMetadata", 400)]
    [InlineData("MemoryEvaluation.NotFound", 404)]
    [InlineData("MemoryEvaluation.Gone", 410)]
    [InlineData("MemoryEvaluation.StaleSource", 409)]
    [InlineData("MemoryEvaluation.StaleConfiguration", 409)]
    [InlineData("MemoryEvaluation.StaleAuthority", 409)]
    [InlineData("MemoryEvaluation.ScopeMismatch", 409)]
    [InlineData("MemoryEvaluation.Busy", 409)]
    [InlineData("MemoryEvaluation.QuotaExceeded", 409)]
    [InlineData("Security.IdempotencyConflict", 409)]
    [InlineData("MemoryEvaluation.Unavailable", 503)]
    [InlineData("MemoryEvaluation.ProviderUnavailable", 503)]
    [InlineData("MemoryEvaluation.InvalidProviderPlan", 503)]
    public void Closed_error_statuses_are_actionable(string code, int status) => Assert.Equal(status, ArcanumErrorMapper.ResolveStatusCode(code));

    [Fact]
    public async Task Every_evaluation_route_is_named_body_only_and_prebinding_protected()
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateAuthenticatedClient();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.Contains("memory-evaluations", StringComparison.Ordinal)).ToArray();

        Assert.Equal(new[] { "DeleteMemoryEvaluation", "GetMemoryEvaluation", "RunMemoryEvaluation" }, routes.Select(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName).Order().ToArray());

        foreach (var route in routes)
        {
            Assert.Empty(route.RoutePattern.Parameters);

            Assert.Equal(["POST"], route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);

            Assert.Equal(CovenantAuthorityRequirement.ProtectedRead, route.Metadata.GetMetadata<CovenantAuthorityRequirementMetadata>()!.Requirement);

            Assert.Null(route.Metadata.GetMetadata<CovenantContextPolicyRequirementMetadata>());
        }
    }

    [Fact]
    public async Task Real_http_run_replay_get_and_exact_cleanup_use_content_free_evidence_and_preserve_source()
    {
        await using var provider = await PublishedArcanumSessionSmokeTests.FakeOpenAiProvider.StartAsync();

        const string credential = "ARCANUM_EVALUATION_HTTP_TEST_KEY";

        string? previous = global::System.Environment.GetEnvironmentVariable(credential);

        global::System.Environment.SetEnvironmentVariable(credential, PublishedArcanumSessionSmokeTests.ProviderKey);

        try
        {
            await using var factory = EvaluationHost(provider, credential);

            int hydrations = 0;

            var overrides = factory.ServiceOverrides;

            factory.ServiceOverrides = services =>
            {
                overrides!(services);

                services.RemoveAll<IMemoryEvaluationSourceReader>();

                services.AddScoped<IMemoryEvaluationSourceReader>(sp => new ObservedSource(ActivatorUtilities.CreateInstance<MemoryEvaluationSourceReader>(sp), () => hydrations++));
            };

            using var client = factory.CreateAuthenticatedClient();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await PublishedArcanumSessionSmokeTests.ExerciseMemoryEvaluationAsync(client, provider, 0, deadline.Token);

            Assert.Equal(1, hydrations);

            using var scope = factory.Services.CreateScope();
            var connection = await scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenCoreConnectionAsync(default);

            await using var command = connection.CreateCommand();

            command.CommandText = "SELECT SensitivityCode, length(ExactGenerationIds) FROM external_disclosure_receipts WHERE SubjectKind = $kind AND EffectCategoryCode = $category";

            command.Parameters.AddWithValue("$kind", (int)CovenantDisclosureSubjectKind.Operation);

            command.Parameters.AddWithValue("$category", (int)CovenantDisclosureEffectCategory.ProviderDispatch);

            await using var rows = await command.ExecuteReaderAsync();

            int clean = 0, derived = 0;

            while (await rows.ReadAsync())
            {
                if ((ContentSensitivity)rows.GetInt32(0) == ContentSensitivity.None) { clean++; Assert.Equal(0, rows.GetInt32(1)); }
                else { Assert.Equal(ContentSensitivity.CovenantDerived, (ContentSensitivity)rows.GetInt32(0)); derived++; Assert.True(rows.GetInt32(1) >= 16); }
            }
            Assert.Equal(3, clean);

            Assert.Equal(3, derived);

            await rows.DisposeAsync();

            command.CommandText = "SELECT count(*) FROM memory_evaluations";

            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        finally { global::System.Environment.SetEnvironmentVariable(credential, previous); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Actual_http_inflight_get_replay_busy_and_terminal_failure_have_exact_statuses(int publicationPhase)
    {
        bool holdInitialPublication = publicationPhase != 0;

        await using var provider = await PublishedArcanumSessionSmokeTests.FakeOpenAiProvider.StartAsync();

        provider.ResumeArms = new(TaskCreationOptions.RunContinuationsAsynchronously);

        provider.InvalidOutput = true;

        const string credential = "ARCANUM_EVALUATION_INFLIGHT_TEST_KEY";

        string? previous = global::System.Environment.GetEnvironmentVariable(credential);

        global::System.Environment.SetEnvironmentVariable(credential, PublishedArcanumSessionSmokeTests.ProviderKey);

        try
        {
            await using var factory = EvaluationHost(provider, credential);

            var publication = new PublicationBarrier { CaptureMissingOwnership = publicationPhase == 2 };
            int hydrations = 0;
            Dictionary<string, TaskCompletionSource> waiters = new()
            {
                ["get"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
                ["delete"] = new(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            if (holdInitialPublication) factory.BeforeEndpoint = async (http, next) =>
            {
                if (http.Request.Headers.ContainsKey("X-Test-Direct-Global"))
                {
                    // Exercise direct runner callers with a genuine request-issued Global Turn.
                    // Only the endpoint's coherent ownership path may choose Campaign scope.
                    var opened = await http.RequestServices.GetRequiredService<MemoryEvaluationRequestScopeFactory>().OpenAsync(http, new(MemoryEvaluationScopeKind.Global), http.RequestAborted);
                    Assert.True(opened.IsSuccess, opened.Error.ToString());
                    var runner = http.RequestServices.GetRequiredService<MemoryEvaluationRunner>();
                    var wrongGet = await runner.GetAsync(new(publication.EvaluationId), opened.Value, http.RequestAborted);
                    var wrongDelete = await runner.DeleteAsync(new(publication.EvaluationId, new CovenantDigest(new byte[32]), true), opened.Value, http.RequestAborted);
                    Assert.Equal("MemoryEvaluation.ScopeMismatch", wrongGet.Error.Code);
                    Assert.Equal("MemoryEvaluation.ScopeMismatch", wrongDelete.Error.Code);
                    await new MemoryEvaluationJsonResult<MemoryEvaluationResponse>(opened.Value, wrongGet, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse).ExecuteAsync(http);
                    return;
                }

                string verb = http.Request.Path.Value!.Split('/')[^1];
                if (waiters.TryGetValue(verb, out var entered) && !publication.Release.Task.IsCompleted)
                {
                    // Make request parsing synchronous, then observe the real endpoint at its
                    // first asynchronous boundary. It must wait before any ownership read.
                    await using var body = new MemoryStream();
                    await http.Request.Body.CopyToAsync(body, http.RequestAborted);
                    body.Position = 0;
                    http.Request.Body = body;
                    Task response = next(http);
                    entered.TrySetResult();
                    await response;
                }
                else await next(http);
            };
            var overrides = factory.ServiceOverrides;
            factory.ServiceOverrides = services =>
            {
                overrides!(services);
                services.RemoveAll<IMemoryEvaluationSourceReader>();
                services.AddScoped<IMemoryEvaluationSourceReader>(sp => new ObservedSource(ActivatorUtilities.CreateInstance<MemoryEvaluationSourceReader>(sp), () => Interlocked.Increment(ref hydrations)));
                if (publicationPhase == 3)
                {
                    services.RemoveAll<ICanonicalCampaignContextResolver>();
                    services.AddScoped<ICanonicalCampaignContextResolver>(sp => new MissingScopeBarrier(ActivatorUtilities.CreateInstance<RetroDownfall.Arcanum.Api.Tower.CanonicalCampaignContextResolver>(sp), publication));
                }

                if (holdInitialPublication)
                {
                    services.RemoveAll<IMemoryEvaluationEvidenceStore>();
                    services.AddScoped<IMemoryEvaluationEvidenceStore>(sp => new InitialPublicationEvidence(ActivatorUtilities.CreateInstance<MemoryEvaluationEvidenceStore>(sp), publication));
                }
            };
            using var client = factory.CreateAuthenticatedClient();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Directory.CreateDirectory(Path.Combine(factory.TempHome, "evaluation"));

            using var campaignResponse = await client.PostAsync("api/campaigns", System.Net.Http.Json.JsonContent.Create(
                new RegisterCampaignRequest("Evaluation", Path.Combine(factory.TempHome, "evaluation"), RetroDownfall.Arcanum.Core.Workspaces.WorkspaceType.Custom, null),
                ArcanumJsonContext.Default.RegisterCampaignRequest), deadline.Token);

            Assert.Equal(HttpStatusCode.Created, campaignResponse.StatusCode);

            var campaign = JsonSerializer.Deserialize(await campaignResponse.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseCampaignDto)!.Data!;

            var request = (await PublishedArcanumSessionSmokeTests.SeedEvaluationAsync(client, deadline.Token)) with { Scope = new(MemoryEvaluationScopeKind.Campaign, campaign.Id) };

            Task<HttpResponseMessage>? pendingGet = null;
            Task<HttpResponseMessage>? pendingReplay = null;
            Task<HttpResponseMessage>? pendingDelete = null;
            if (publicationPhase >= 2)
            {
                publication.CaptureMissingScope = publicationPhase == 3;
                pendingGet = client.PostAsync("api/proving-grounds/memory-evaluations/get", System.Net.Http.Json.JsonContent.Create(new MemoryEvaluationGetRequest(request.EvaluationId), ArcanumJsonContext.Default.MemoryEvaluationGetRequest), deadline.Token);
                pendingDelete = client.PostAsync("api/proving-grounds/memory-evaluations/delete", System.Net.Http.Json.JsonContent.Create(new MemoryEvaluationDeleteRequest(request.EvaluationId, new CovenantDigest(new byte[32]), true), ArcanumJsonContext.Default.MemoryEvaluationDeleteRequest), deadline.Token);
                await publication.AbsenceCaptured.Task.WaitAsync(deadline.Token);
                Assert.False(pendingGet.IsCompleted);
                Assert.False(pendingDelete.IsCompleted);
                Assert.Equal(0, hydrations);
                Assert.Equal(0, provider.RequestCount);
            }

            var running = client.PostAsync("api/proving-grounds/memory-evaluations/run", System.Net.Http.Json.JsonContent.Create(request, ArcanumJsonContext.Default.MemoryEvaluationRequest), deadline.Token);

            try
            {
                if (holdInitialPublication)
                {
                    await publication.Entered.Task.WaitAsync(deadline.Token);
                    Assert.Equal(0, provider.RequestCount);
                    pendingGet ??= client.PostAsync("api/proving-grounds/memory-evaluations/get", System.Net.Http.Json.JsonContent.Create(new MemoryEvaluationGetRequest(request.EvaluationId), ArcanumJsonContext.Default.MemoryEvaluationGetRequest), deadline.Token);
                    pendingReplay = client.PostAsync("api/proving-grounds/memory-evaluations/run", System.Net.Http.Json.JsonContent.Create(request, ArcanumJsonContext.Default.MemoryEvaluationRequest), deadline.Token);
                    pendingDelete ??= client.PostAsync("api/proving-grounds/memory-evaluations/delete", System.Net.Http.Json.JsonContent.Create(new MemoryEvaluationDeleteRequest(request.EvaluationId, new CovenantDigest(new byte[32]), true), ArcanumJsonContext.Default.MemoryEvaluationDeleteRequest), deadline.Token);
                    await Task.WhenAll(waiters.Values.Select(waiter => waiter.Task)).WaitAsync(deadline.Token);
                    Assert.Equal(publicationPhase >= 2 ? 2 : 0, publication.EarlyOwnershipReads);
                    Assert.False(pendingGet.IsCompleted);
                    Assert.False(pendingDelete.IsCompleted);
                    publication.ResumeAbsence.TrySetResult();
                    publication.Release.SetResult();
                }

                await provider.FirstArm.Task.WaitAsync(deadline.Token);
                if (pendingGet is not null)
                {
                    using var waitedGet = await pendingGet;
                    using var waitedReplay = await pendingReplay!;
                    using var waitedDelete = await pendingDelete!;
                    Assert.Equal(publicationPhase == 3 ? HttpStatusCode.NotFound : HttpStatusCode.Accepted, waitedGet.StatusCode);
                    Assert.Equal(HttpStatusCode.Accepted, waitedReplay.StatusCode);
                    if (publicationPhase != 3)
                        Assert.Equal(publication.OperationId, JsonSerializer.Deserialize(await waitedGet.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse)!.Data!.OperationId);
                    else Assert.Equal("MemoryEvaluation.NotFound", await MemoryErasureRouteDriver.ReadErrorCodeAsync(waitedGet));
                    Assert.Equal(publication.OperationId, JsonSerializer.Deserialize(await waitedReplay.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse)!.Data!.OperationId);
                    Assert.Equal(publicationPhase == 3 ? HttpStatusCode.NotFound : HttpStatusCode.Conflict, waitedDelete.StatusCode);
                    Assert.Equal(publicationPhase == 3 ? "MemoryEvaluation.NotFound" : "Security.IdempotencyConflict", await MemoryErasureRouteDriver.ReadErrorCodeAsync(waitedDelete));
                }

                if (publicationPhase == 2)
                {
                    using var direct = new HttpRequestMessage(HttpMethod.Post, "api/proving-grounds/memory-evaluations/get")
                    {
                        Content = System.Net.Http.Json.JsonContent.Create(new MemoryEvaluationGetRequest(request.EvaluationId), ArcanumJsonContext.Default.MemoryEvaluationGetRequest)
                    };
                    direct.Headers.Add("X-Test-Direct-Global", "true");
                    using var refused = await client.SendAsync(direct, deadline.Token);
                    Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                    Assert.Equal("MemoryEvaluation.ScopeMismatch", await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));
                }

                using var get = await client.PostAsync("api/proving-grounds/memory-evaluations/get", System.Net.Http.Json.JsonContent.Create(new MemoryEvaluationGetRequest(request.EvaluationId), ArcanumJsonContext.Default.MemoryEvaluationGetRequest), deadline.Token);
                Assert.Equal(HttpStatusCode.Accepted, get.StatusCode);

                var status = JsonSerializer.Deserialize(await get.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse)!;

                Assert.True(status.IsSuccess);

                Assert.Null(status.Data!.Report);

                Assert.Null(status.Data.ReportDigest);

                Assert.Equal("no-store, private", get.Headers.CacheControl!.ToString());

                using var replay = await client.PostAsync("api/proving-grounds/memory-evaluations/run", System.Net.Http.Json.JsonContent.Create(request, ArcanumJsonContext.Default.MemoryEvaluationRequest), deadline.Token);
                Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);

                foreach (var changed in new[] { request with { PairCount = 4 }, request with { EvaluationId = Guid.NewGuid() } })
                {
                    using var conflict = await client.PostAsync("api/proving-grounds/memory-evaluations/run", System.Net.Http.Json.JsonContent.Create(changed, ArcanumJsonContext.Default.MemoryEvaluationRequest), deadline.Token);
                    Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                }
                Assert.Equal(1, provider.RequestCount);
            }
            finally { publication.ResumeAbsence.TrySetResult(); publication.Release.TrySetResult(); provider.ResumeArms.TrySetResult(); }
            using var completed = await running;
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

            var terminal = JsonSerializer.Deserialize(await completed.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse)!.Data!;

            Assert.Equal(MemoryEvaluationState.Completed, terminal.State);

            Assert.Equal(MemoryEvaluationOutcome.InsufficientEvidence, terminal.Report!.Decision.Outcome);

            Assert.Equal(MemoryEvaluationReason.InvalidOutput, terminal.Reason);

            Assert.Equal(campaign.Id, terminal.Report.Registration.Ownership.CampaignId);

            Assert.NotNull(terminal.ReportDigest);

            Assert.Equal(1, provider.RequestCount);
            Assert.Equal(1, hydrations);
        }
        finally { global::System.Environment.SetEnvironmentVariable(credential, previous); }
    }

    [Theory]
    [InlineData("run")]
    [InlineData("get")]
    [InlineData("delete")]
    public async Task Unavailable_original_authority_refuses_before_endpoint_binding(string verb)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        var original = factory.ServiceOverrides;

        var authority = new FakeCovenantAuthorityProvider();

        authority.Clear();

        factory.ServiceOverrides = services =>
        {
            original!(services);

            services.RemoveAll<IOperatorAuthorityContextIssuer>();

            services.AddSingleton<IOperatorAuthorityContextIssuer>(new OperatorAuthorityContextIssuer(authority));
        };

        int entered = 0;

        factory.BeforeEndpoint = async (http, next) => { entered++; await next(http); };

        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsync("api/proving-grounds/memory-evaluations/" + verb, new StringContent("{private-sentinel", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.Equal(0, entered);

        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());

        Assert.DoesNotContain("private-sentinel", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Wire_metadata_is_explicit_and_transient_execution_state_is_absent()
    {
        Type[] wire = [typeof(MemoryEvaluationRequest), typeof(MemoryEvaluationScope), typeof(MemoryEvaluationOptions), typeof(MemoryEvaluationWorkload),
            typeof(MemoryEvaluationMemory), typeof(MemoryEvaluationCovenantTarget), typeof(MemoryEvaluationSagaTarget), typeof(MemoryEvaluationLexiconTarget),
            typeof(MemoryEvaluationCheck), typeof(MemoryEvaluationRegexCheck), typeof(MemoryEvaluationJsonSchemaCheck), typeof(MemoryEvaluationGetRequest),
            typeof(MemoryEvaluationDeleteRequest), typeof(MemoryEvaluationDeleteResult), typeof(MemoryEvaluationResponse), typeof(MemoryEvaluationTerminalReport),
            typeof(MemoryEvaluationRegistration), typeof(MemoryEvaluationSourceCoordinate), typeof(MemoryEvaluationDependencyEdge), typeof(MemoryEvaluationCheckAggregate),
            typeof(MemoryEvaluationAggregate), typeof(MemoryEvaluationDecision), typeof(MemoryEvaluationOwnership), typeof(MemoryEvaluationState), typeof(MemoryEvaluationOutcome),
            typeof(MemoryEvaluationReason), typeof(MemoryEvaluationScopeKind), typeof(MemoryEvaluationCheckKind), typeof(MemoryEvaluationSource), typeof(MemoryEvaluationCoordinateRole),
            typeof(MemoryEvaluationArm), typeof(MemoryEvaluationVerdict), typeof(MemoryEvaluationPairRelation), typeof(MemoryEvaluationSourceLifecycle),
            typeof(ApiResponse<MemoryEvaluationResponse>), typeof(ApiResponse<MemoryEvaluationDeleteResult>)];

        var registered = typeof(ArcanumJsonContext).CustomAttributes.Where(a => a.AttributeType == typeof(System.Text.Json.Serialization.JsonSerializableAttribute))
            .Select(a => (Type)a.ConstructorArguments[0].Value!).ToArray();

        foreach (var type in wire)
        {
            Assert.Contains(type, registered);

            Assert.NotNull(ArcanumJsonContext.Default.GetTypeInfo(type));
        }

        foreach (var type in new[] { typeof(MemoryEvaluationValidatedRequest), typeof(MemoryEvaluationExecutionPlan), typeof(MemoryEvaluationSourceSnapshot), typeof(MemoryEvaluationSendEvidence), typeof(MemoryEvaluationRequestScope), typeof(ArcanumInvocationContext), typeof(CovenantTurnLease) })
            Assert.Null(ArcanumJsonContext.Default.GetTypeInfo(type));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Policy_refusal_and_unknown_cleanup_do_not_execute_a_provider(bool cleanup)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateAuthenticatedClient();
        using var response = cleanup
            ? await client.PostAsync("api/proving-grounds/memory-evaluations/delete", System.Net.Http.Json.JsonContent.Create(new MemoryEvaluationDeleteRequest(Guid.NewGuid(), new CovenantDigest(new byte[32]), true), ArcanumJsonContext.Default.MemoryEvaluationDeleteRequest))
            : await client.PostAsync("api/proving-grounds/memory-evaluations/run", System.Net.Http.Json.JsonContent.Create(MemoryEvaluationTestData.ValidRequest() with { PairCount = 2 }, ArcanumJsonContext.Default.MemoryEvaluationRequest));

        Assert.Equal(cleanup ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Composition_retains_process_epoch_and_execution_slot_but_owns_provider_clients_per_request()
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateAuthenticatedClient();
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        Assert.Same(first.ServiceProvider.GetRequiredService<MemoryEvaluationExecutionSlot>(), second.ServiceProvider.GetRequiredService<MemoryEvaluationExecutionSlot>());

        Assert.Same(first.ServiceProvider.GetRequiredService<MemoryEvaluationProviderEpoch>(), second.ServiceProvider.GetRequiredService<MemoryEvaluationProviderEpoch>());

        Assert.NotSame(first.ServiceProvider.GetRequiredService<MemoryEvaluationProviderClientFactory>(), second.ServiceProvider.GetRequiredService<MemoryEvaluationProviderClientFactory>());

        Assert.NotSame(first.ServiceProvider.GetRequiredService<IMemoryEvaluationProvider>(), second.ServiceProvider.GetRequiredService<IMemoryEvaluationProvider>());

        Assert.NotSame(first.ServiceProvider.GetRequiredService<MemoryEvaluationRunner>(), second.ServiceProvider.GetRequiredService<MemoryEvaluationRunner>());
    }

    [Theory]
    [InlineData("delete", "{}")]
    [InlineData("delete", "{\"bytes\":null}")]
    [InlineData("delete", "{\"bytes\":\"AA==\"}")]
    [InlineData("run", "{}")]
    [InlineData("run", "{\"bytes\":null}")]
    [InlineData("run", "{\"bytes\":\"AA==\"}")]
    public async Task Literal_malformed_digest_objects_are_constant_protected_400_before_work(string verb, string digest)
    {
        var work = new RejectingEvaluationWork();
        await using var factory = LiteralHost(work);
        using var client = factory.CreateAuthenticatedClient();
        string json = verb == "delete" ? LiteralDelete(digest) : LiteralRun("covenant", digest);
        using var response = await client.PostAsync("api/proving-grounds/memory-evaluations/" + verb, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MemoryEvaluation.InvalidRequest", await MemoryErasureRouteDriver.ReadErrorCodeAsync(response));
        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-cache", Assert.Single(response.Headers.Pragma).Name);
        Assert.Equal("0", Assert.Single(response.Content.Headers.GetValues("Expires")));
        Assert.Equal(0, work.Hydrations);
        Assert.Equal(0, work.ProviderCalls);
    }

    [Theory]
    [InlineData("covenant")]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("delete")]
    public async Task Literal_digest_objects_reject_unknown_members_before_work(string store)
    {
        const string digest = "{\"bytes\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\",\"unexpected\":\"private-digest-sentinel\"}";
        var work = new RejectingEvaluationWork();
        await using var factory = LiteralHost(work);
        using var client = factory.CreateAuthenticatedClient();
        string verb = store == "delete" ? "delete" : "run";
        string json = store == "delete" ? LiteralDelete(digest) : LiteralRun(store, digest);
        using var response = await client.PostAsync("api/proving-grounds/memory-evaluations/" + verb, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MemoryEvaluation.InvalidRequest", await MemoryErasureRouteDriver.ReadErrorCodeAsync(response));
        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());
        Assert.DoesNotContain("private-digest-sentinel", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, work.Hydrations);
        Assert.Equal(0, work.ProviderCalls);
    }

    [Theory]
    [InlineData("covenant")]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("delete")]
    public async Task Literal_exact_32_byte_base64_digest_objects_reach_domain_processing(string store)
    {
        const string digest = "{\"bytes\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"}";
        var work = new RejectingEvaluationWork();
        await using var factory = LiteralHost(work);
        using var client = factory.CreateAuthenticatedClient();
        bool cleanup = store == "delete";
        using var response = await client.PostAsync("api/proving-grounds/memory-evaluations/" + (cleanup ? "delete" : "run"),
            new StringContent(cleanup ? LiteralDelete(digest) : LiteralRun(store, digest), Encoding.UTF8, "application/json"));
        Assert.Equal(cleanup ? HttpStatusCode.NotFound : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(cleanup ? "MemoryEvaluation.NotFound" : "MemoryEvaluation.StaleSource", await MemoryErasureRouteDriver.ReadErrorCodeAsync(response));
        Assert.Equal(cleanup ? 0 : 1, work.Hydrations);
        Assert.Equal(0, work.ProviderCalls);
    }

    [Fact]
    public async Task Literal_returned_digest_object_is_copied_unchanged_into_successful_cleanup()
    {
        await using var provider = await PublishedArcanumSessionSmokeTests.FakeOpenAiProvider.StartAsync();
        provider.InvalidOutput = true;
        const string credential = "ARCANUM_EVALUATION_LITERAL_CLEANUP_KEY";
        string? previous = global::System.Environment.GetEnvironmentVariable(credential);
        global::System.Environment.SetEnvironmentVariable(credential, PublishedArcanumSessionSmokeTests.ProviderKey);
        try
        {
            await using var factory = EvaluationHost(provider, credential);
            using var client = factory.CreateAuthenticatedClient();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var request = await PublishedArcanumSessionSmokeTests.SeedEvaluationAsync(client, deadline.Token);
            using var run = await client.PostAsync("api/proving-grounds/memory-evaluations/run", System.Net.Http.Json.JsonContent.Create(request, ArcanumJsonContext.Default.MemoryEvaluationRequest), deadline.Token);
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
            using var report = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
            var returnedDigest = report.RootElement.GetProperty("data").GetProperty("reportDigest");
            Assert.Equal(JsonValueKind.Object, returnedDigest.ValueKind);
            Assert.Equal("bytes", Assert.Single(returnedDigest.EnumerateObject()).Name);
            Assert.Equal(32, returnedDigest.GetProperty("bytes").GetBytesFromBase64().Length);
            string literal = LiteralDelete(returnedDigest.GetRawText()).Replace("11111111-1111-1111-1111-111111111111", request.EvaluationId.ToString("D"), StringComparison.Ordinal);
            using var deleted = await client.PostAsync("api/proving-grounds/memory-evaluations/delete", new StringContent(literal, Encoding.UTF8, "application/json"), deadline.Token);
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            using var body = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("data").GetProperty("deleted").GetBoolean());
            Assert.Equal(1, provider.RequestCount);
        }
        finally { global::System.Environment.SetEnvironmentVariable(credential, previous); }
    }

    [Fact]
    public void Strict_request_metadata_does_not_change_shared_digest_options()
    {
        const string digest = "{\"bytes\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\",\"unexpected\":\"ordinary-api-compatibility\"}";
        var ordinary = JsonSerializer.Deserialize(LiteralDelete(digest), ArcanumJsonContext.Default.MemoryEvaluationDeleteRequest);
        Assert.NotNull(ordinary);
        Assert.True(ordinary.ExpectedReportDigest.IsValid);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(LiteralDelete(digest), MemoryEvaluationRequestJson.DeleteTypeInfo));
    }

    private static string LiteralDelete(string digest) =>
        "{\"evaluationId\":\"11111111-1111-1111-1111-111111111111\",\"expectedReportDigest\":" + digest + ",\"confirm\":true}";

    private static string LiteralRun(string store, string digest)
    {
        string target = store switch
        {
            "covenant" => "{\"entryId\":\"22222222-2222-2222-2222-222222222222\",\"versionId\":\"33333333-3333-3333-3333-333333333333\",\"fragmentDigest\":" + digest + "}",
            "saga" => "{\"memoryId\":\"selected-memory\",\"versionId\":\"exact-version\",\"hashFormat\":1,\"contentDigest\":" + digest + "}",
            "lexicon" => "{\"entryId\":\"22222222-2222-2222-2222-222222222222\",\"versionId\":\"exact-version\",\"hashFormat\":1,\"contentDigest\":" + digest + "}",
            _ => throw new ArgumentOutOfRangeException(nameof(store))
        };
        return "{\"evaluationId\":\"11111111-1111-1111-1111-111111111111\",\"scope\":{\"kind\":\"Global\"},\"providerName\":\"provider\",\"model\":\"model\","
            + "\"options\":{},\"workload\":{\"systemPrompt\":\"system\",\"userPrompt\":\"user\"},\"memory\":{"
            + "\"covenant\":[" + (store == "covenant" ? target : "") + "],\"saga\":[" + (store == "saga" ? target : "") + "],\"lexicon\":[" + (store == "lexicon" ? target : "") + "]},"
            + "\"checks\":[{\"kind\":\"regex\",\"pattern\":\"expected\"}],\"pairCount\":3}";
    }

    private static ArcanumWebApplicationFactory LiteralHost(RejectingEvaluationWork work)
    {
        var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());
        var overrides = factory.ServiceOverrides;
        factory.ServiceOverrides = services =>
        {
            overrides!(services);
            services.RemoveAll<IMemoryEvaluationSourceReader>();
            services.RemoveAll<IMemoryEvaluationProvider>();
            services.AddSingleton<IMemoryEvaluationSourceReader>(work);
            services.AddSingleton<IMemoryEvaluationProvider>(work);
        };
        return factory;
    }

    private sealed class RejectingEvaluationWork : IMemoryEvaluationSourceReader, IMemoryEvaluationProvider
    {
        internal int Hydrations;

        internal int ProviderCalls;
        public Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryEvaluationValidatedRequest request, ArcanumInvocationContext invocation, CovenantTurnLease lease, CancellationToken ct)
        {
            Hydrations++;
            return Task.FromResult(Result<MemoryEvaluationSourceSnapshot>.Failure(new Error("MemoryEvaluation.StaleSource", "No literal fixture source exists.")));
        }

        public Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct) => throw new InvalidOperationException("No literal fixture plan exists.");
        public Task<Result<MemoryEvaluationExecutionPlan>> PrepareAsync(MemoryEvaluationValidatedRequest request, MemoryEvaluationSourceSnapshot snapshot, MemoryEvaluationPromptPair prompts, CancellationToken ct)
        { ProviderCalls++; throw new InvalidOperationException("Parsing refusal must precede provider preparation."); }
        public Task<Result> ValidateAsync(MemoryEvaluationExecutionPlan plan, CancellationToken ct)
        { ProviderCalls++; throw new InvalidOperationException("No literal fixture plan exists."); }
        public Task<Result<MemoryEvaluationArmResult>> SendAsync(MemoryEvaluationExecutionPlan plan, int pairIndex, MemoryEvaluationArm arm, ITurnBudget budget, Func<MemoryEvaluationSendEvidence, CancellationToken, Task> beforeSend, CancellationToken ct)
        { ProviderCalls++; throw new InvalidOperationException("Parsing refusal must precede sends."); }
    }

    private static ArcanumWebApplicationFactory EvaluationHost(PublishedArcanumSessionSmokeTests.FakeOpenAiProvider provider, string credential) =>
        MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true, configure: settings =>
        {
            settings.Providers = [..settings.Providers, new ProviderSettings
            {
                Name = "published-smoke", Type = AiProviderKind.OpenAICompatible, Endpoint = provider.Endpoint.AbsoluteUri,
                CredentialEnvironmentVariable = credential, Models = [new ModelEntry(PublishedArcanumSessionSmokeTests.ExpectedModel, SupportsTools: false)], ContextWindowLimit = 8192
            }];

            PublishedArcanumSessionSmokeTests.ConfigureEvaluationPricing(settings);
        });

    private sealed class PublicationBarrier
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Guid OperationId;

        internal Guid EvaluationId;

        internal int EarlyOwnershipReads;

        internal bool CaptureMissingOwnership;

        internal bool CaptureMissingScope;

        internal int MissingObservations;
        internal readonly TaskCompletionSource AbsenceCaptured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ResumeAbsence = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task HoldAbsenceAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref MissingObservations) == 2) AbsenceCaptured.TrySetResult();
            await ResumeAbsence.Task.WaitAsync(ct);
        }
    }

    private sealed class InitialPublicationEvidence(IMemoryEvaluationEvidenceStore inner, PublicationBarrier barrier) : IMemoryEvaluationEvidenceStore
    {
        public async Task<Result<MemoryEvaluationStoredEvidence>> RegisterAsync(MemoryEvaluationRegistration registration, ImmutableArray<MemoryEvaluationSourceCoordinate> sources, CancellationToken ct)
        {
            barrier.OperationId = registration.OperationId;
            barrier.EvaluationId = registration.EvaluationId;
            barrier.Entered.SetResult();
            await barrier.Release.Task.WaitAsync(ct);
            return await inner.RegisterAsync(registration, sources, ct);
        }

        public async Task<Result<MemoryEvaluationOwnership>> ReadOwnershipAsync(Guid id, CancellationToken ct)
        {
            if (!barrier.Release.Task.IsCompleted) Interlocked.Increment(ref barrier.EarlyOwnershipReads);
            var result = await inner.ReadOwnershipAsync(id, ct);
            if (barrier.CaptureMissingOwnership && result.IsFailure && result.Error.Code == "MemoryEvaluation.NotFound")
                await barrier.HoldAbsenceAsync(ct);
            return result;
        }

        public Task<Result<MemoryEvaluationStoredEvidence>> GetAsync(Guid id, CancellationToken ct) => inner.GetAsync(id, ct);
        public Task<Result<MemoryEvaluationStoredEvidence>> CompleteAsync(Guid id, MemoryEvaluationTerminalReport report, CancellationToken ct) => inner.CompleteAsync(id, report, ct);
        public Task<Result<MemoryEvaluationDeleteResult>> DeleteAsync(MemoryEvaluationDeleteRequest request, CancellationToken ct) => inner.DeleteAsync(request, ct);
    }

    private sealed class MissingScopeBarrier(ICanonicalCampaignContextResolver inner, PublicationBarrier barrier) : ICanonicalCampaignContextResolver
    {
        public async ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken ct)
        {
            var result = await inner.ResolveAsync(request, ct);
            if (barrier.CaptureMissingScope && result.IsSuccess && result.Value.CampaignId is null)
                await barrier.HoldAbsenceAsync(ct);
            return result;
        }
    }

    private sealed class RequestAuthorityProvider : ICovenantAuthoritySnapshotProvider
    {
        public CovenantAuthoritySnapshot? Current { get; private set; } = new FakeCovenantAuthorityProvider().Current;

        internal void RotateMasterKey() => Current = Current! with { MasterKeyVersion = Current.MasterKeyVersion + 1 };
    }

    private sealed class GlobalResolver : ICanonicalCampaignContextResolver
    {
        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken ct) => ValueTask.FromResult(Result<CanonicalCampaignContext>.Success(CanonicalCampaignContext.GlobalOnly));
    }

    private sealed class ObservedSource(IMemoryEvaluationSourceReader inner, Action hydrate) : IMemoryEvaluationSourceReader
    {
        public Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryEvaluationValidatedRequest request, ArcanumInvocationContext invocation, CovenantTurnLease lease, CancellationToken ct)
        { hydrate(); return inner.ReadAsync(request, invocation, lease, ct); }
        public Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct) => inner.ValidateAsync(snapshot, lease, ct);
    }

    private sealed class CountedStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int ReadBytes;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);

            ReadBytes += read;

            return read;
        }
    }
}
