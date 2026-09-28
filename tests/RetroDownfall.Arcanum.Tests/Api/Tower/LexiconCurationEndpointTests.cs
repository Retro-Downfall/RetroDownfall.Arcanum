using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class LexiconCurationEndpointTests
{
    private const string Campaign = "aaaaaaaa-1111-4111-8111-111111111111";

    public static TheoryData<string> Routes => new() { "show", "correct", "retire", "reinstate", "pin", "unpin" };

    public static TheoryData<string, int> Errors => new()
    {
        { ErrorCodes.Lexicon.InvalidName, 400 },
        { "Lexicon.CurationUnavailable", 503 },
        { ErrorCodes.Lexicon.InvalidScope, 400 },
        { ErrorCodes.Lexicon.InvalidCurationTarget, 400 },
        { ErrorCodes.Lexicon.InvalidReplacement, 400 },
        { ErrorCodes.Lexicon.NotFound, 404 },
        { ErrorCodes.Lexicon.ProtectedMutationRefused, 403 },
        { ErrorCodes.Lexicon.StaleCurationTarget, 409 },
        { ErrorCodes.Lexicon.RetiredMutationRefused, 409 },
        { ErrorCodes.Lexicon.CurationGenerationExhausted, 409 },
        { ErrorCodes.Lexicon.ArtifactRevisionExhausted, 409 },
        { ErrorCodes.Lexicon.CurationIntegrityFailed, 500 },
        { ErrorCodes.Lexicon.WriteFailed, 500 },
        { ErrorCodes.Lexicon.SearchFailed, 500 },
        { ErrorCodes.Covenant.ForbiddenAuthority, 403 },
        { ErrorCodes.Covenant.StaleSnapshot, 409 },
        { ErrorCodes.Covenant.IntegrityFailure, 503 },
        { ErrorCodes.Covenant.Unavailable, 503 },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Routes_require_authentication_and_render_body_errors(string route)
    {
        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient anonymous = factory.CreateClient();

        using HttpResponseMessage denied = await anonymous.PostAsync(Path(route), Json("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage malformed = await client.PostAsync(Path(route), Json("{ not json"));

        await RefusalAsync(malformed, 400, ErrorCodes.Validation.InvalidBody);

        using HttpResponseMessage media = await client.PostAsync(Path(route), new StringContent("{}", Encoding.UTF8, "text/plain"));

        await RefusalAsync(media, 415, ErrorCodes.Validation.UnsupportedMediaType);

        using HttpResponseMessage missing = await client.PostAsync(Path(route), null);

        await RefusalAsync(missing, 415, ErrorCodes.Validation.UnsupportedMediaType);

        foreach (string payload in new[] { "{}", "null" })
        {
            using HttpResponseMessage invalid = await client.PostAsync(Path(route), Json(payload));

            await RefusalAsync(invalid, 400, route == "show" ? ErrorCodes.Lexicon.InvalidScope : ErrorCodes.Lexicon.InvalidCurationTarget);
        }
    }

    [Fact]
    public async Task Exact_show_never_falls_back_and_the_round_tripped_target_drives_all_six_outcomes()
    {
        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with { Features = settings.Features with { Annals = false } },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            ILexiconService lexicon = scope.ServiceProvider.GetRequiredService<ILexiconService>();

            AttachmentMemoryProvenance source = new(Guid.NewGuid(), Guid.NewGuid(), "source", 1,
                "source-hash", DateTimeOffset.UnixEpoch, "WorkspaceFile", AttachmentSourceAvailability.Available);

            Assert.True((await lexicon.UpsertAsync("show", "Person", ["global fact"], source, LexiconScope.Global)).IsSuccess);
        }

        using HttpResponseMessage noFallback = await client.PostAsync(Path("show"), Json(ShowBody(campaign: true)));

        await RefusalAsync(noFallback, 404, ErrorCodes.Lexicon.NotFound);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            ILexiconService lexicon = scope.ServiceProvider.GetRequiredService<ILexiconService>();

            Assert.True((await lexicon.UpsertAsync("show", "Person", ["campaign fact"], LexiconScope.ForCampaign(Guid.Parse(Campaign)))).IsSuccess);
        }

        using HttpResponseMessage campaignResponse = await client.PostAsync(Path("show"), Json(ShowBody(campaign: true)));

        JsonNode campaignDetail = await DataAsync(campaignResponse);

        Assert.Equal("Campaign", campaignDetail["scope"]!["kind"]!.GetValue<string>());

        Assert.Equal(Campaign, campaignDetail["scope"]!["campaignId"]!.GetValue<string>());

        Assert.Equal("campaign fact", campaignDetail["entry"]!["facts"]![0]!.GetValue<string>());

        _ = await MutateAsync(client, "retire", campaignDetail, "Applied");

        using HttpResponseMessage retiredExact = await client.PostAsync(Path("show"), Json(ShowBody(campaign: true)));

        JsonNode retiredCampaign = await DataAsync(retiredExact);

        Assert.Equal("Retired", retiredCampaign["eligibility"]!.GetValue<string>());

        Assert.Equal("campaign fact", retiredCampaign["entry"]!["facts"]![0]!.GetValue<string>());

        JsonNode detail = await ShowAsync(client);

        Assert.Equal("Global", detail["scope"]!["kind"]!.GetValue<string>());

        Assert.Equal("SHOW", detail["target"]!["normalizedName"]!.GetValue<string>());

        Assert.False(detail["target"]!["annalHead"]!["isPresent"]!.GetValue<bool>());

        Assert.False(detail["target"]!["sensitivityLabel"]!["isPresent"]!.GetValue<bool>());

        LexiconEntryDetail? typed = JsonSerializer.Deserialize(detail.ToJsonString(),
            (System.Text.Json.Serialization.Metadata.JsonTypeInfo<LexiconEntryDetail>)ArcanumJsonContext.Default.GetTypeInfo(typeof(LexiconEntryDetail))!);

        Assert.NotNull(typed);

        Assert.True(typed.Target.Validate().IsSuccess);

        detail = await MutateAsync(client, "correct", detail, "Unchanged", "global fact");

        JsonNode stale = detail.DeepClone();

        detail = await MutateAsync(client, "correct", detail, "Applied", "replacement fact");

        Assert.Equal(2, detail["annalHistory"]!.AsArray().Count);

        Assert.Equal("source", Assert.Single(detail["historicalFactProvenance"]!.AsArray())!["logicalKey"]!.GetValue<string>());

        Assert.True(detail["target"]!["annalHead"]!["isPresent"]!.GetValue<bool>());

        using HttpResponseMessage staleResponse = await client.PostAsync(Path("correct"), Json(MutationBody(stale, "new fact")));

        await RefusalAsync(staleResponse, 409, ErrorCodes.Lexicon.StaleCurationTarget);

        detail = await MutateAsync(client, "pin", detail, "Applied");

        detail = await MutateAsync(client, "pin", detail, "AlreadyPinned");

        detail = await MutateAsync(client, "unpin", detail, "Applied");

        detail = await MutateAsync(client, "unpin", detail, "NotPinned");

        detail = await MutateAsync(client, "retire", detail, "Applied");

        Assert.Equal("Retired", detail["eligibility"]!.GetValue<string>());

        detail = await MutateAsync(client, "retire", detail, "AlreadyRetired");

        using HttpResponseMessage retiredCorrection = await client.PostAsync(Path("correct"), Json(MutationBody(detail, "refused")));

        await RefusalAsync(retiredCorrection, 409, ErrorCodes.Lexicon.RetiredMutationRefused);

        Assert.Equal("Retired", (await ShowAsync(client))["eligibility"]!.GetValue<string>());

        detail = await MutateAsync(client, "reinstate", detail, "Applied");

        _ = await MutateAsync(client, "reinstate", detail, "NotRetired");

        using HttpResponseMessage effective = await client.GetAsync($"/api/memory/lexicon/show?campaignId={Campaign}");

        Assert.Equal("replacement fact", (await DataAsync(effective))["facts"]![0]!.GetValue<string>());

        using HttpResponseMessage deleted = await client.DeleteAsync("/api/memory/lexicon/show");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using HttpResponseMessage absent = await client.PostAsync(Path("show"), Json(ShowBody()));

        await RefusalAsync(absent, 404, ErrorCodes.Lexicon.NotFound);
    }

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task Service_errors_keep_their_code_trace_and_exact_status(string code, int status)
    {
        Probe probe = new() { Error = code };

        await using ArcanumWebApplicationFactory factory = CreateFactory(probe);

        using HttpClient client = factory.CreateAuthenticatedClient();

        foreach (string route in new[] { "show", "correct", "retire", "reinstate", "pin", "unpin" })
        {
            using HttpResponseMessage response = await client.PostAsync(Path(route), Json(probe.Body(route)));

            await RefusalAsync(response, status, code);
        }

        Assert.Equal(6, probe.Calls);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Invalid_scope_name_content_and_evidence_are_refused_before_service_or_authority(string route)
    {
        Probe probe = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(probe);

        using HttpClient client = factory.CreateAuthenticatedClient();

        List<(string Body, string Code)> invalid = [];

        if (route == "show")
        {
            invalid.Add(("{\"name\":\"show\",\"scope\":{\"kind\":\"Campaign\"}}", ErrorCodes.Lexicon.InvalidScope));

            invalid.Add(("{\"name\":\"show\",\"scope\":{\"kind\":\"Global\",\"campaignId\":\"" + Campaign + "\"}}", ErrorCodes.Lexicon.InvalidScope));

            invalid.Add(("{\"name\":\" \",\"scope\":{\"kind\":\"Global\"}}", ErrorCodes.Lexicon.InvalidName));
        }
        else
        {
            foreach (string arm in new[] { "scope", "lifecycle", "annalHead", "sensitivityLabel" })
            {
                JsonNode body = JsonNode.Parse(probe.Body(route))!;

                body["target"]![arm] = null;

                invalid.Add((body.ToJsonString(), ErrorCodes.Lexicon.InvalidCurationTarget));
            }

            foreach (string arm in new[] { "annalHead", "sensitivityLabel" })
            {
                JsonNode omitted = JsonNode.Parse(probe.Body(route))!;

                omitted["target"]![arm]!.AsObject().Remove("isPresent");

                invalid.Add((omitted.ToJsonString(), ErrorCodes.Validation.InvalidBody));

                JsonNode incomplete = JsonNode.Parse(probe.Body(route))!;

                incomplete["target"]![arm]!["isPresent"] = true;

                invalid.Add((incomplete.ToJsonString(), ErrorCodes.Lexicon.InvalidCurationTarget));

                JsonNode contradictory = JsonNode.Parse(probe.Body(route))!;

                contradictory["target"]![arm]![arm == "annalHead" ? "claimId" : "labelId"] = Campaign;

                invalid.Add((contradictory.ToJsonString(), ErrorCodes.Lexicon.InvalidCurationTarget));
            }

            JsonNode invalidProvenance = JsonNode.Parse(new Probe { Protected = true }.Body(route))!;

            invalidProvenance["target"]!["sensitivityLabel"]!["generationProvenance"]!["exactGenerationIds"] = null;

            invalid.Add((invalidProvenance.ToJsonString(), ErrorCodes.Validation.InvalidBody));

            JsonNode invalidGeneration = JsonNode.Parse(new Probe { Protected = true }.Body(route))!;

            invalidGeneration["target"]!["sensitivityLabel"]!["generationProvenance"]!["exactGenerationIds"] = new JsonArray(Guid.Empty.ToString("D"));

            invalid.Add((invalidGeneration.ToJsonString(), ErrorCodes.Validation.InvalidBody));

            foreach ((string property, JsonNode? value) in new (string, JsonNode?)[]
            {
                ("entryId", JsonValue.Create(Guid.Empty)), ("curationGeneration", JsonValue.Create(0)),
                ("snapshotDigest", JsonValue.Create("bad")), ("normalizedName", JsonValue.Create(" ")),
            })
            {
                JsonNode body = JsonNode.Parse(probe.Body(route))!;

                body["target"]![property] = value;

                invalid.Add((body.ToJsonString(), ErrorCodes.Lexicon.InvalidCurationTarget));
            }

            if (route == "correct")
            {
                foreach (string content in new[] { "null", "{}", "{\"type\":\"Person\",\"facts\":[]}", "{\"type\":\" \",\"facts\":[\"fact\"]}" })
                {
                    JsonNode body = JsonNode.Parse(probe.Body(route))!;

                    body["content"] = JsonNode.Parse(content);

                    invalid.Add((body.ToJsonString(), ErrorCodes.Lexicon.InvalidReplacement));
                }
            }
        }

        foreach ((string payload, string code) in invalid)
        {
            using HttpResponseMessage response = await client.PostAsync(Path(route), Json(payload));

            await RefusalAsync(response, 400, code);
        }

        Assert.Equal(0, probe.Calls);

        Assert.Equal(0, probe.Acquisitions);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Protected_response_owns_exact_authority_through_final_byte_and_mapped_errors(string route)
    {
        foreach (bool campaign in new[] { false, true })
        {
            foreach (bool failure in route == "show" ? new[] { false } : new[] { false, true })
            {
                Probe probe = new() { Protected = true, CampaignScope = campaign, Error = failure ? ErrorCodes.Lexicon.StaleCurationTarget : null };

                using BlockingBody body = new();

                await using ArcanumWebApplicationFactory factory = CreateFactory(probe, body);

                using HttpClient client = factory.CreateAuthenticatedClient();

                Task<HttpResponseMessage> executing = client.PostAsync(Path(route), Json(probe.Body(route)));

                try
                {
                    await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

                    Assert.Equal(campaign ? CovenantOperationScope.ForCampaign(Guid.Parse(Campaign)) : CovenantOperationScope.Global, probe.Scope);

                    Assert.True(probe.ServiceHadLease);

                    Assert.Equal(1, probe.Registration!.Revalidations);

                    Assert.Equal(0, probe.Registration.Disposals);

                    Assert.Equal(0, body.Length);

                    Assert.Equal("no-store, private", probe.Context!.Response.Headers.CacheControl.ToString());

                    Assert.Equal("no-cache", probe.Context.Response.Headers.Pragma.ToString());

                    Assert.Equal("0", probe.Context.Response.Headers.Expires.ToString());

                    Assert.Empty(probe.Context.Response.Headers.ETag.ToString());

                    Assert.Empty(probe.Context.Response.Headers.LastModified.ToString());
                }
                finally
                {
                    body.Release.TrySetResult();

                    using HttpResponseMessage response = await executing.WaitAsync(TimeSpan.FromSeconds(20));
                }

                Assert.Equal(1, probe.Registration!.Disposals);

                Assert.Contains(failure ? ErrorCodes.Lexicon.StaleCurationTarget : "secret fact", Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Unlabeled_and_feature_absent_responses_have_no_protected_headers_or_speculative_write_lease(string route)
    {
        foreach (bool absent in new[] { false, true })
        {
            Probe probe = new() { Absent = absent };

            await using ArcanumWebApplicationFactory factory = CreateFactory(probe);

            using HttpClient client = factory.CreateAuthenticatedClient();

            using HttpResponseMessage response = await client.PostAsync(Path(route), Json(probe.Body(route)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.False(response.Headers.Contains("Cache-Control"));

            Assert.False(response.Headers.Contains("Pragma"));

            Assert.False(response.Content.Headers.Contains("Expires"));

            Assert.Equal(route == "show" ? 1 : 0, probe.Acquisitions);

            Assert.Equal(route == "show" && !absent ? 1 : 0, probe.Registration?.Disposals ?? 0);
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Admission_refusal_prevents_service_and_final_revalidation_refuses_payload(string route)
    {
        foreach (bool acquisition in new[] { true, false })
        {
            Probe probe = new() { Protected = true, Deny = acquisition, Stale = !acquisition };

            await using ArcanumWebApplicationFactory factory = CreateFactory(probe);

            using HttpClient client = factory.CreateAuthenticatedClient();

            using HttpResponseMessage response = await client.PostAsync(Path(route), Json(probe.Body(route)));

            await RefusalAsync(response, acquisition ? 503 : 409, acquisition ? ErrorCodes.Covenant.Unavailable : ErrorCodes.Covenant.StaleSnapshot);

            Assert.Equal(acquisition ? 0 : 1, probe.Calls);

            Assert.Equal(acquisition ? 0 : 1, probe.Registration?.Disposals ?? 0);

            Assert.DoesNotContain("secret fact", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("correct")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("pin")]
    [InlineData("unpin")]
    public async Task Protected_transition_unavailability_preserves_authority_precedence(string route)
    {
        foreach (string authority in new[] { "valid", "denied", "stale" })
        {
            Probe probe = new() { Protected = true, Error = "Lexicon.CurationUnavailable", Deny = authority == "denied", Stale = authority == "stale" };

            await using ArcanumWebApplicationFactory factory = CreateFactory(probe);

            using HttpClient client = factory.CreateAuthenticatedClient();

            using HttpResponseMessage response = await client.PostAsync(Path(route), Json(probe.Body(route)));

            await RefusalAsync(response, authority == "stale" ? 409 : 503, authority switch
            {
                "denied" => ErrorCodes.Covenant.Unavailable,
                "stale" => ErrorCodes.Covenant.StaleSnapshot,
                _ => "Lexicon.CurationUnavailable",
            });

            Assert.Equal(authority == "denied" ? 0 : 1, probe.Calls);

            Assert.Equal(1, probe.Acquisitions);

            Assert.Equal(authority == "denied" ? 0 : 1, probe.Registration?.Disposals ?? 0);

            Assert.DoesNotContain("secret fact", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Cancellation_serialization_failure_and_service_exception_release_once(string route)
    {
        foreach (string fault in new[] { "cancel", "serialization", "service" })
        {
            Probe probe = new() { Protected = true, Throw = fault == "service" };

            using BlockingBody body = new() { Fail = fault == "serialization" };

            using CancellationTokenSource cancellation = new();

            if (fault == "service")
            {
                body.Release.TrySetResult();
            }

            await using ArcanumWebApplicationFactory factory = CreateFactory(probe, body, cancellation.Token);

            using HttpClient client = factory.CreateAuthenticatedClient();

            Task<HttpResponseMessage> executing = client.PostAsync(Path(route), Json(probe.Body(route)));

            if (fault != "service")
            {
                await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

                if (fault == "cancel")
                {
                    cancellation.Cancel();
                }

                body.Release.TrySetResult();
            }

            try
            {
                using HttpResponseMessage response = await executing.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or InvalidOperationException or HttpRequestException)
            {
            }

            Assert.Equal(1, probe.Registration!.Disposals);
        }
    }

    private static ArcanumWebApplicationFactory CreateFactory(Probe probe, Stream? body = null, CancellationToken cancellation = default) => new()
    {
        BeforeEndpoint = async (context, next) =>
        {
            context.RequestServices = new ProbeServices(context.RequestServices, probe);

            probe.Context = context;

            if (body is not null)
            {
                context.Response.Body = body;

                context.Response.Headers.ETag = "old";

                context.Response.Headers.LastModified = "Sun, 27 Sep 2026 00:00:00 GMT";
            }

            if (cancellation.CanBeCanceled)
            {
                context.RequestAborted = cancellation;
            }

            await next(context);
        },
    };

    private static string Path(string route) => "/api/memory/lexicon/" + route;

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string ShowBody(bool campaign = false) => campaign
        ? "{\"name\":\"show\",\"scope\":{\"kind\":\"Campaign\",\"campaignId\":\"" + Campaign + "\"}}"
        : "{\"name\":\"show\",\"scope\":{\"kind\":\"Global\"}}";

    private static string MutationBody(JsonNode detail, string fact = "replacement fact") => new JsonObject
    {
        ["target"] = detail["target"]!.DeepClone(),
        ["content"] = new JsonObject { ["type"] = "Person", ["facts"] = new JsonArray(fact) },
    }.ToJsonString();

    private static async Task<JsonNode> ShowAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsync(Path("show"), Json(ShowBody()));

        return await DataAsync(response);
    }

    private static async Task<JsonNode> MutateAsync(HttpClient client, string route, JsonNode detail, string outcome, string fact = "replacement fact")
    {
        using HttpResponseMessage response = await client.PostAsync(Path(route), Json(MutationBody(detail, fact)));

        JsonNode data = await DataAsync(response);

        Assert.Equal(outcome, data["outcome"]!.GetValue<string>());

        return data["entry"]!;
    }

    private static async Task<JsonNode> DataAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, json);

        JsonNode body = JsonNode.Parse(json)!;

        Assert.True(body["isSuccess"]!.GetValue<bool>());

        Assert.False(string.IsNullOrWhiteSpace(body["traceId"]!.GetValue<string>()));

        return body["data"]!;
    }

    private static async Task RefusalAsync(HttpResponseMessage response, int status, string code)
    {
        string json = await response.Content.ReadAsStringAsync();

        Assert.True((int)response.StatusCode == status, $"Expected {status}, got {(int)response.StatusCode}: {json}");

        JsonNode body = JsonNode.Parse(json)!;

        Assert.False(body["isSuccess"]!.GetValue<bool>());

        Assert.Null(body["data"]);

        Assert.Equal(code, body["error"]!["code"]!.GetValue<string>());

        Assert.False(string.IsNullOrWhiteSpace(body["traceId"]!.GetValue<string>()));
    }

    private sealed class ProbeServices(IServiceProvider inner, Probe probe) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ILexiconCurationService)
            || serviceType == typeof(ICovenantExportPolicy) || serviceType == typeof(ICovenantOperationGate)
            ? probe : inner.GetService(serviceType);
    }

    private sealed class Probe : ILexiconCurationService, ICovenantExportPolicy, ICovenantOperationGate
    {
        internal bool Protected { get; init; }

        internal bool CampaignScope { get; init; }

        internal bool Absent { get; init; }

        internal bool Deny { get; init; }

        internal bool Stale { get; init; }

        internal bool Throw { get; init; }

        internal string? Error { get; init; }

        internal int Calls { get; private set; }

        internal int Acquisitions { get; private set; }

        internal CovenantOperationScope? Scope { get; private set; }

        internal Registration? Registration { get; private set; }

        internal bool ServiceHadLease { get; private set; }

        internal HttpContext? Context { get; set; }

        private LexiconEntryDetail Detail()
        {
            LexiconCurationScope scope = new(CampaignScope ? LexiconScopeKind.Campaign : LexiconScopeKind.Global, CampaignScope ? Guid.Parse(Campaign) : null);

            LexiconEntryLifecycle lifecycle = new(null, null);

            LexiconCurationTarget target = new(scope, "SHOW", Guid.Parse("bbbbbbbb-2222-4222-8222-222222222222"), 1,
                new string('A', 64), lifecycle, new(false, null, null, null, null, null, null),
                Protected ? new(true, Guid.Parse("cccccccc-3333-4333-8333-333333333333"), 1, new string('B', 64), GenerationProvenance.CreateExact([Guid.Parse(Campaign)]))
                    : new(false, null, null, null, null));

            return new(new(target.EntryId, "show", "Person", ["secret fact"], DateTimeOffset.UnixEpoch, [], scope.CampaignId), scope,
                null, lifecycle, LexiconRetrievalEligibility.Eligible, 1, target.SnapshotDigest, target, [], []);
        }

        internal string Body(string route)
        {
            if (route == "show")
            {
                return ShowBody(CampaignScope);
            }

            LexiconCurationTarget target = Detail().Target;

            JsonNode node = JsonNode.Parse(JsonSerializer.Serialize(target, ArcanumJsonContext.Default.LexiconCurationTarget))!;

            return new JsonObject
            {
                ["target"] = node,
                ["content"] = new JsonObject { ["type"] = "Person", ["facts"] = new JsonArray("replacement fact") },
            }.ToJsonString();
        }

        public Task<Result<LexiconInspectionResult<LexiconEntryDetail>>> ShowExactAsync(LexiconCurationScope scope, string name, ICovenantSnapshotReadLease? readLease, CancellationToken cancellationToken = default)
        {
            Called(readLease);

            Assert.Equal(Detail().Scope, scope);

            Assert.Equal("show", name);

            return Task.FromResult(Error is null ? Result<LexiconInspectionResult<LexiconEntryDetail>>.Success(new(Detail(), Protected))
                : Result<LexiconInspectionResult<LexiconEntryDetail>>.Failure(new Error(Error, "Injected refusal.")));
        }

        public Task<Result<LexiconInspectionResult<LexiconEntryDetail>>> ShowEffectiveAsync(LexiconCurationScope requestedScope, string name, ICovenantSnapshotReadLease? installationReadLease, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Exact curation must never use effective lookup.");

        public Task<Result<LexiconInspectionResult<IReadOnlyList<LexiconEntryDto>>>> ListInspectionAsync(ICovenantSnapshotReadLease? readLease, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<LexiconInspectionResult<IReadOnlyList<LexiconEntryDto>>>> SearchInspectionAsync(string? query, int? limit, ICovenantSnapshotReadLease? readLease, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<LexiconInspectionResult<LexiconInspectionCounts>>> CountInspectionAsync(ICovenantSnapshotReadLease? readLease, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private void Called(ICovenantOperationLease? lease)
        {
            Calls++;

            ServiceHadLease = lease is not null && Registration is { Disposals: 0 };

            if (Throw)
            {
                throw new InvalidOperationException("Injected service failure.");
            }
        }

        private Task<Result<LexiconCurationResult>> Mutate(LexiconCurationTarget target, CovenantWriteLease? lease)
        {
            Called(lease);

            Assert.Equal(Detail().Target.EntryId, target.EntryId);

            Assert.Equal(Detail().Scope, target.Scope);

            return Task.FromResult(Error is null ? Result<LexiconCurationResult>.Success(new(LexiconCurationOutcomeKind.Applied, Detail()))
                : Result<LexiconCurationResult>.Failure(new Error(Error, "Injected refusal.")));
        }

        public Task<Result<LexiconCurationResult>> CorrectAsync(LexiconCurationTarget target, LexiconReplacementContent replacement, CovenantWriteLease? writeLease, CancellationToken cancellationToken = default)
        {
            Assert.Equal("Person", replacement.Type);

            Assert.Equal(["replacement fact"], replacement.Facts);

            return Mutate(target, writeLease);
        }

        public Task<Result<LexiconCurationResult>> RetireAsync(LexiconCurationTarget target, CovenantWriteLease? writeLease, CancellationToken cancellationToken = default) => Mutate(target, writeLease);

        public Task<Result<LexiconCurationResult>> ReinstateAsync(LexiconCurationTarget target, CovenantWriteLease? writeLease, CancellationToken cancellationToken = default) => Mutate(target, writeLease);

        public Task<Result<LexiconCurationResult>> PinAsync(LexiconCurationTarget target, CovenantWriteLease? writeLease, CancellationToken cancellationToken = default) => Mutate(target, writeLease);

        public Task<Result<LexiconCurationResult>> UnpinAsync(LexiconCurationTarget target, CovenantWriteLease? writeLease, CancellationToken cancellationToken = default) => Mutate(target, writeLease);

        public ValueTask<Result<CovenantExportAdmission>> AcquireConditionalReadAsync(CovenantOperationScope? scope, CancellationToken cancellationToken)
        {
            Acquisitions++;

            Scope = scope;

            return ValueTask.FromResult(Deny ? Result<CovenantExportAdmission>.Failure(new Error(ErrorCodes.Covenant.Unavailable, "Denied."))
                : Result<CovenantExportAdmission>.Success(Absent ? CovenantExportAdmission.Absent
                    : new(new CovenantReadLease(Registration = new(scope!.Value, CovenantLeaseKind.Read, Stale)))));
        }

        public ValueTask<Result<CovenantWriteLease>> AcquireWriteAsync(CovenantOperationScope scope, CancellationToken cancellationToken)
        {
            Acquisitions++;

            Scope = scope;

            return ValueTask.FromResult(Deny ? Result<CovenantWriteLease>.Failure(new Error(ErrorCodes.Covenant.Unavailable, "Denied."))
                : Result<CovenantWriteLease>.Success(new(Registration = new(scope, CovenantLeaseKind.Write, Stale))));
        }

        public Task<Result<CovenantSessionExportSensitivity>> InspectSessionAsync(Guid sessionId, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<CovenantCampaignExportExclusions>> InventoryCampaignExclusionsAsync(Guid campaignId, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantInstallationReadLease>> AcquireInstallationReadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantReadLease>> AcquireReadAsync(CovenantOperationScope scope, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantTurnLease>> AcquireTurnAsync(CanonicalCampaignContext campaign, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantMcpLease>> AcquireMcpAsync(CovenantOperationScope scope, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantAcceleratorLease>> AcquireAcceleratorAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantCleanupLease>> AcquireCleanupAsync(CovenantOperationScope scope, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantCampaignExclusiveLease>> AcquireCampaignExclusiveAsync(Guid campaignId, CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantProtectedTransferLease>> AcquireProtectedTransferAsync(ProtectedTransferScope scope, CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantExclusiveLease>> ResumeOrAcquireExclusiveAsync(CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantCampaignExclusiveLease>> ResumeCampaignExclusiveAsync(Guid campaignId, CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantProtectedTransferLease>> ResumeProtectedTransferAsync(ProtectedTransferScope scope, CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<CovenantExclusiveLease>> ResumeExclusiveAsync(CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Registration(CovenantOperationScope scope, CovenantLeaseKind kind, bool stale) : ICovenantLeaseRegistration
    {
        internal int Revalidations { get; private set; }

        internal int Disposals { get; private set; }

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(Guid.NewGuid(), 1, kind, CovenantLeaseCoverage.Scoped,
            scope, null, 1, 1, 0, null, null, null, null, null, false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {
            Revalidations++;

            return ValueTask.FromResult(stale ? Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "Revoked.")) : Result.Success());
        }

        public ValueTask ReleaseAsync()
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
