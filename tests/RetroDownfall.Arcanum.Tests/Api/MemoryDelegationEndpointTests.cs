using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Infrastructure.Resilience;
using System.Text;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Intelligence;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryDelegation;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class MemoryDelegationEndpointTests
{
    [Theory]
    [InlineData("argument")]
    [InlineData("json")]
    [InlineData("transport-limit")]
    [InlineData("utf8-read")]
    public async Task Read_failures_do_not_fabricate_deserializer_provenance_or_measured_bytes(string mode)
    {
        RequestAuthorityProvider provider = new();

        OperatorAuthorityContextIssuer issuer = new(provider);

        await using var services = new ServiceCollection().AddSingleton<IOperatorAuthorityContextIssuer>(issuer).BuildServiceProvider();

        DefaultHttpContext http = new();

        http.RequestServices = services;

        var authority = issuer.Issue(CovenantAuthorityRequirement.ProtectedRead).Value;

        CovenantRequestFeatures.RecordAuthority(http, new CovenantAuthorityFeature(authority, authority.Requirement, authority.AuthorityEpoch));

        http.Request.ContentType = "application/json";

        Exception failure = mode switch
        {
            "argument" => new ArgumentException("private-read-canary"),
            "json" => new JsonException("private-read-canary"),
            "utf8-read" => new DecoderFallbackException("private-read-canary"),
            _ => new BadHttpRequestException("private-transport-limit-canary", StatusCodes.Status413PayloadTooLarge),
        };

        await using var body = new FailedBodyRead(failure);

        http.Request.Body = body;

        var read = await MemoryDelegationRequestJson.ReadAsync(http);

        Assert.NotNull(read.Error);

        http.Response.Body = new MemoryStream();

        await read.Error.ExecuteAsync(http);

        string json = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());

        Assert.Equal(StatusCodes.Status400BadRequest, http.Response.StatusCode);

        Assert.DoesNotContain("private-", json);

        if (mode == "transport-limit")
        {
            Assert.Contains("transport", json, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("Reduce", json, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("at least", json, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain((MemoryDelegationRequestJson.RequestBytes + 1L).ToString(), json);
        }
        else
        {
            Assert.Contains("The memory delegation request could not be completed.", json);

            Assert.DoesNotContain("declared parent", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class FailedBodyRead(Exception failure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Semantic_parent_or_grant_refusal_keeps_the_constant_without_caller_provenance(bool missingParent)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateAuthenticatedClient();

        var request = missingParent ? new MemoryDelegationRequest(null!, Grant())
            : new MemoryDelegationRequest(new("private-prompt-canary"), null!);

        using var response = await client.PostAsync("/api/intelligence/memory-delegation", JsonContent.Create(request, ArcanumJsonContext.Default.MemoryDelegationRequest));

        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Contains(ErrorCodes.MemoryDelegation.InvalidGrant, json);

        Assert.Contains("The memory delegation request could not be completed.", json);

        Assert.DoesNotContain("private-", json);

        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);
    }

    [Theory]
    [InlineData("null-file", ErrorCodes.Validation.AttachedFiles, "parent.attachedFiles[0]")]
    [InlineData("path-length", ErrorCodes.Validation.AttachedFiles, "parent.attachedFiles[1].relativePath")]
    [InlineData("prompt", "Validation.PromptTooLong", "parent.prompt")]
    [InlineData("system-prompt", "Validation.AdditionalSystemPromptTooLong", "parent.additionalSystemPrompt")]
    [InlineData("reasoning", ErrorCodes.Validation.ReasoningEffortAndBudgetMutuallyExclusive, "parent.reasoning")]
    [InlineData("client-tools", ErrorCodes.ClientTools.InvalidSchema, "forwardClientTools=false")]
    [InlineData("vision", ErrorCodes.Scrying.VisionNotSupported, "vision-capable")]
    public async Task Real_preflight_mints_only_closed_field_guidance_before_hydration(string mode, string code, string expected)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        if (mode == "vision")
        {
            var configure = factory.SettingsOverride;

            factory.SettingsOverride = current => configure!(current) with
            {
                Features = current.Features with { Covenant = true, Scrying = true },
                DefaultModel = "private-model-canary",
                Providers = [new() { Name = "test", Type = AiProviderKind.OpenAICompatible,
                    Models = [new("private-model-canary", SupportsTools: true, SupportsVision: false)] }],
            };
        }

        int reads = 0;

        var original = factory.ServiceOverrides;

        factory.ServiceOverrides = services =>
        {
            original!(services);

            services.RemoveAll<IMemoryDelegationSourceReader>();

            services.AddSingleton<IMemoryDelegationSourceReader>(new RefusingSources(() => reads++));
        };

        int pathLimit = ArcanumSettingClamps.MaxAttachedFileRelativePathChars(ArcanumRuntimeDefaults.CliMaxAttachedFileRelativePathChars);

        using JsonDocument clientChoice = JsonDocument.Parse("\"private-tool-canary\"");

        var parent = new PingRequest("private-prompt-canary", Model: "private-model-canary");

        parent = mode switch
        {
            "null-file" => parent with { AttachedFiles = [null!] },
            "path-length" => parent with { AttachedFiles = [new("first.txt", "ok"), new("private-path-canary" + new string('x', pathLimit), "private-content-canary")] },
            "prompt" => parent with { Prompt = "private-prompt-canary" + new string('x', ArcanumSettingClamps.MaxPingPromptChars(ArcanumRuntimeDefaults.Intelligence.MaxPingPromptChars)) },
            "system-prompt" => parent with { AdditionalSystemPrompt = "private-system-canary" + new string('x', ArcanumSettingClamps.MaxPingPromptChars(ArcanumRuntimeDefaults.Intelligence.MaxPingPromptChars)) },
            "reasoning" => parent with { Reasoning = new(ReasoningEffortLevel.Medium, 128) },
            "client-tools" => parent with { ClientToolChoice = clientChoice.RootElement.Clone() },
            _ => parent with { ScryingFoci = [new(Convert.ToBase64String([1, 2, 3]), "image/png")] },
        };

        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/intelligence/memory-delegation", JsonContent.Create(
            new MemoryDelegationRequest(parent, Grant()), ArcanumJsonContext.Default.MemoryDelegationRequest));

        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Contains(code, json);

        Assert.Contains(expected, json, StringComparison.OrdinalIgnoreCase);

        if (mode is "prompt" or "system-prompt")
            Assert.Contains("limit " + ArcanumSettingClamps.MaxPingPromptChars(ArcanumRuntimeDefaults.Intelligence.MaxPingPromptChars), json);

        Assert.DoesNotContain("private-", json);

        Assert.Equal(0, reads);

        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData(ErrorCodes.Validation.AttachedFiles)]
    [InlineData("Validation.PromptTooLong")]
    [InlineData(ErrorCodes.MemoryDelegation.InvalidGrant)]
    public async Task A_source_error_cannot_claim_local_preflight_or_body_diagnostic_provenance(string code)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        UntrustedFailureSources source = new(code);

        var original = factory.ServiceOverrides;

        factory.ServiceOverrides = services =>
        {
            original!(services);

            services.RemoveAll<IMemoryDelegationSourceReader>();

            services.AddSingleton<IMemoryDelegationSourceReader>(source);
        };

        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/intelligence/memory-delegation", JsonContent.Create(
            new MemoryDelegationRequest(new("parent"), Grant()), ArcanumJsonContext.Default.MemoryDelegationRequest));

        string json = await response.Content.ReadAsStringAsync();

        Assert.Contains(code, json);

        Assert.Equal(code == "Validation.PromptTooLong" ? HttpStatusCode.InternalServerError : HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Contains("The memory delegation request could not be completed.", json);

        Assert.DoesNotContain("private-", json);

        Assert.Equal(1, source.Reads);

        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);
    }

    private sealed class UntrustedFailureSources(string code) : IMemoryDelegationSourceReader
    {
        internal int Reads;

        public Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryDelegationGrant grant, ArcanumInvocationContext parent, CovenantTurnLease lease, CancellationToken ct)
        {
            Reads++;

            return Task.FromResult(Result<MemoryEvaluationSourceSnapshot>.Failure(new(code,
                "private-path-canary private-model-canary private-prompt-canary private-grant-canary")));
        }

        public Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct) => Task.FromResult(Result.Success());
    }

    [Theory]
    [InlineData("body")]
    [InlineData("preflight")]
    [InlineData("mismatched-code")]
    [InlineData("negative-facts")]
    public async Task Unknown_or_inconsistent_local_diagnostic_data_keeps_the_protected_constant(string mode)
    {
        RequestAuthorityProvider provider = new();

        OperatorAuthorityContextIssuer issuer = new(provider);

        DefaultHttpContext http = new();

        var authority = issuer.Issue(CovenantAuthorityRequirement.ProtectedRead).Value;

        var feature = new CovenantAuthorityFeature(authority, authority.Requirement, authority.AuthorityEpoch);

        CovenantRequestFeatures.RecordAuthority(http, feature);

        MemoryDelegationCallerOrigin origin = new(feature, issuer);

        MemoryDelegationCallerDiagnostic diagnostic = mode switch
        {
            "body" => MemoryDelegationCallerDiagnostic.ForBody(origin, (MemoryDelegationBodyFailure)255),
            "preflight" => MemoryDelegationCallerDiagnostic.ForPreflight(origin, new((PingRequestFailureReason)255)),
            "negative-facts" => MemoryDelegationCallerDiagnostic.ForPreflight(origin, new(PingRequestFailureReason.AttachmentTooLarge, -1, -2, -3)),
            _ => MemoryDelegationCallerDiagnostic.ForBody(origin, MemoryDelegationBodyFailure.Shape),
        };

        string code = mode == "body" ? ErrorCodes.MemoryDelegation.InvalidGrant : ErrorCodes.Validation.AttachedFiles;

        http.Response.Body = new MemoryStream();

        await new MemoryDelegationJsonResult(null, Result<PromptResponseDto>.Failure(new(code, "private-canary")), diagnostic).ExecuteAsync(http);

        string json = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());

        Assert.Contains("The memory delegation request could not be completed.", json);

        Assert.DoesNotContain("private-", json);

        Assert.Equal(400, http.Response.StatusCode);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_scope_authority_refusal_overrides_an_earlier_typed_caller_error(bool swapped)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var (http, scope) = ResponseScope(h, root);

        var feature = CovenantRequestFeatures.Authority(http)!;

        var origin = MemoryDelegationCallerOrigin.Capture(http)!;

        var diagnostic = MemoryDelegationCallerDiagnostic.ForPreflight(origin, new(PingRequestFailureReason.AttachmentPathMissing, 0));

        object key = http.Items.Single(pair => ReferenceEquals(pair.Value, feature)).Key;

        if (swapped) http.Items[key] = new CovenantAuthorityFeature(feature.Context, feature.Requirement, feature.AuthorityEpoch);
        else http.Items.Remove(key);

        http.Response.Body = new MemoryStream();

        await new MemoryDelegationJsonResult(scope, Result<PromptResponseDto>.Failure(new(ErrorCodes.Validation.AttachedFiles, "private-canary")), diagnostic).ExecuteAsync(http);

        string json = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());

        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);

        Assert.Contains(ErrorCodes.MemoryDelegation.StaleSource, json);

        Assert.Contains("The memory delegation request could not be completed.", json);

        Assert.DoesNotContain("relativePath", json);

        Assert.DoesNotContain("private-", json);
    }

    [Fact]
    public void Caller_diagnostic_provenance_has_no_source_generated_wire_metadata()
    {
        foreach (Type type in new[] { typeof(MemoryDelegationCallerDiagnostic), typeof(MemoryDelegationCallerOrigin), typeof(MemoryDelegationOpenOutcome), typeof(PingRequestFailureDetails) })
            Assert.Null(ArcanumJsonContext.Default.GetTypeInfo(type));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Declared_and_streamed_body_overflow_deliver_bounded_size_guidance(bool streamed)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateAuthenticatedClient();

        byte[] bytes = new byte[MemoryDelegationRequestJson.RequestBytes + 1];

        bytes.AsSpan().Fill((byte)' ');

        Encoding.UTF8.GetBytes("private-body-canary").CopyTo(bytes, 0);

        using HttpContent content = streamed ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);

        content.Headers.ContentType = new("application/json");

        Assert.Equal(!streamed, content.Headers.ContentLength is not null);

        using var response = await client.PostAsync("/api/intelligence/memory-delegation", content);

        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Contains(ErrorCodes.MemoryDelegation.InvalidGrant, json);

        Assert.Contains(MemoryDelegationRequestJson.RequestBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), json);

        Assert.Contains(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), json);

        Assert.Contains("Reduce", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("private-", json);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());

        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("swapped")]
    [InlineData("rotated")]
    public async Task Early_body_refusal_revalidates_the_captured_original_authority_before_delivery(string change)
    {
        RequestAuthorityProvider provider = new();

        OperatorAuthorityContextIssuer issuer = new(provider);

        await using var services = new ServiceCollection().AddSingleton<IOperatorAuthorityContextIssuer>(issuer).BuildServiceProvider();

        DefaultHttpContext http = new();

        http.RequestServices = services;

        var authority = issuer.Issue(CovenantAuthorityRequirement.ProtectedRead).Value;

        var feature = new CovenantAuthorityFeature(authority, authority.Requirement, authority.AuthorityEpoch);

        CovenantRequestFeatures.RecordAuthority(http, feature);

        http.Request.ContentType = "application/json";

        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{private-canary"));

        var read = await MemoryDelegationRequestJson.ReadAsync(http);

        Assert.NotNull(read.Error);

        object key = http.Items.Single(pair => ReferenceEquals(pair.Value, feature)).Key;

        if (change == "rotated") provider.RotateMasterKey();
        else if (change == "swapped") http.Items[key] = new CovenantAuthorityFeature(authority, authority.Requirement, authority.AuthorityEpoch);
        else http.Items.Remove(key);

        http.Response.Body = new MemoryStream();

        await read.Error.ExecuteAsync(http);

        string json = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());

        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);

        Assert.Contains(ErrorCodes.MemoryDelegation.StaleSource, json);

        Assert.Contains("The memory delegation request could not be completed.", json);

        Assert.DoesNotContain("declared parent", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("private-", json);

        Assert.Equal("no-store, private", http.Response.Headers.CacheControl.ToString());
    }

    [SkippableTheory]
    [InlineData(ErrorCodes.Validation.AttachedFiles)]
    [InlineData(ErrorCodes.Scrying.VisionNotSupported)]
    [InlineData(ErrorCodes.MemoryDelegation.InvalidGrant)]
    [InlineData(ErrorCodes.Covenant.Unavailable)]
    [InlineData(ErrorCodes.MemoryDelegation.StaleSource)]
    public async Task An_error_code_alone_never_authorizes_provider_or_source_message_detail(string code)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var (http, scope) = ResponseScope(h, root);

        http.Response.Body = new MemoryStream();

        var payload = Result<PromptResponseDto>.Failure(new Error(code,
            "private-path-canary private-model-canary private-prompt-canary private-grant-canary"));

        await new MemoryDelegationJsonResult(scope, payload).ExecuteAsync(http);

        string json = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());

        Assert.Contains(code, json);

        Assert.Contains("The memory delegation request could not be completed.", json);

        Assert.DoesNotContain("private-", json);

        Assert.Equal("no-store, private", http.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("shape", "declared parent and grant")]
    [InlineData("unknown", "declared parent and grant")]
    [InlineData("content-type", "application/json")]
    [InlineData("charset", "UTF-8")]
    [InlineData("utf8", "strict UTF-8")]
    public async Task Transport_refusals_deliver_closed_actionable_guidance_without_echoing_the_body(string mode, string expected)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateAuthenticatedClient();

        string text = mode == "unknown" ? "{\"private-member-canary\":\"private-grant-canary\"}" : "{private-prompt-canary";

        byte[] body = mode == "utf8" ? [0x7b, 0x22, 0xff, 0x22, 0x7d] : Encoding.UTF8.GetBytes(text);

        using var content = new ByteArrayContent(body);

        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mode == "content-type" ? "text/plain" : mode == "charset" ? "application/json; charset=utf-16" : "application/json");

        using var response = await client.PostAsync("/api/intelligence/memory-delegation", content);

        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Contains(ErrorCodes.MemoryDelegation.InvalidGrant, json);

        Assert.Contains(expected, json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("private-", json, StringComparison.Ordinal);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());

        Assert.Equal("no-cache", response.Headers.Pragma.ToString());

        Assert.Null(response.Headers.ETag);

        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Attached_file_refusals_report_safe_field_index_and_bounds_without_private_path_or_model(bool oversize, bool multibyte)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateAuthenticatedClient();

        int size = checked((int)ArcanumSettingClamps.MaxAttachFileSizeBytes(ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes) + 1);

        string content = oversize ? multibyte ? new string('é', (size + 1) / 2) : new string('x', size) : "private-body-canary";

        int measured = Encoding.UTF8.GetByteCount(content);

        var parent = new PingRequest("private-prompt-canary", Model: "private-model-canary", AttachedFiles:
            [new(oversize ? "private-path-canary.txt" : "", content)]);

        using var response = await client.PostAsync("/api/intelligence/memory-delegation", JsonContent.Create(
            new MemoryDelegationRequest(parent, Grant()), ArcanumJsonContext.Default.MemoryDelegationRequest));

        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Contains(ErrorCodes.Validation.AttachedFiles, json);

        Assert.Contains("parent.attachedFiles[0]", json);

        Assert.Contains(oversize ? "Split" : "relativePath", json, StringComparison.OrdinalIgnoreCase);

        if (oversize)
        {
            Assert.Contains(measured.ToString(System.Globalization.CultureInfo.InvariantCulture), json);

            Assert.Contains((size - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), json);
        }
        Assert.DoesNotContain("private-", json, StringComparison.Ordinal);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());

        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_tainted_epoch_refuses_before_binding(bool tainted)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        var authority = new RequestAuthorityProvider();

        authority.MakeUnavailable(tainted);

        var original = factory.ServiceOverrides;

        factory.ServiceOverrides = services =>
        {
            original!(services);

            services.RemoveAll<IOperatorAuthorityContextIssuer>();

            services.AddSingleton<IOperatorAuthorityContextIssuer>(new OperatorAuthorityContextIssuer(authority));
        };

        int entered = 0;

        factory.BeforeEndpoint = async (http, next) => { entered++; await next(http); };

        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/intelligence/memory-delegation", new StringContent("{private-sentinel", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.Equal(0, entered);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
    }

    [SkippableFact]
    public async Task Process_quota_refuses_before_hydration_and_returns_conflict()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        int reads = 0;

        var original = factory.ServiceOverrides;

        factory.ServiceOverrides = services =>
        {
            original!(services);

            services.RemoveAll<MemoryDelegationCoordinator>();

            services.AddSingleton(h.Coordinator);

            services.RemoveAll<IMemoryDelegationSourceReader>();

            services.AddSingleton<IMemoryDelegationSourceReader>(new RefusingSources(() => reads++));
        };

        using var client = factory.CreateAuthenticatedClient();

        List<MemoryDelegationRoot> roots = [];

        try
        {
            for (int index = 0; index < 32; index++) roots.Add(await h.PrepareAsync(Guid.NewGuid()));

            using var response = await client.PostAsync("/api/intelligence/memory-delegation", JsonContent.Create(new MemoryDelegationRequest(new("parent"), Grant()), ArcanumJsonContext.Default.MemoryDelegationRequest));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            Assert.Equal(0, reads);

            Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);
        }
        finally
        {
            foreach (var root in roots) await root.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(ToolPolicy.ReadOnlyTools, true)]
    [InlineData(ToolPolicy.AllTools, true)]
    [InlineData(ToolPolicy.NoForbiddenArts, false)]
    public void Native_delegate_availability_matches_the_real_policy_filter_with_mcp_disabled(ToolPolicy policy, bool allowed)
    {
        var settings = new ArcanumSettings
        {
            DefaultModel = "model",
            Providers = [new() { Name = "test", Type = AiProviderKind.OpenAICompatible, Models = [new("model", SupportsTools: true)] }]
        };

        settings.Security.Ward.ForbiddenArts = [ArcanumBuiltInToolNames.DelegateTask];

        var result = MemoryDelegationRequestScope.ValidateNativeDelegation(new("parent", DisableMcpTools: true, ToolPolicy: policy), settings);

        Assert.Equal(allowed, result.IsSuccess);
    }

    [Theory]
    [InlineData(true, false, HttpStatusCode.ServiceUnavailable, 0)]
    [InlineData(false, true, HttpStatusCode.Conflict, 1)]
    [InlineData(false, false, HttpStatusCode.ServiceUnavailable, 0)]
    public async Task MemoryDelegation_health_selected_native_candidates_gate_source_hydration(
        bool firstSupportsTools, bool healthySupportsTools, HttpStatusCode expected, int expectedReads)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        var configure = factory.SettingsOverride;

        factory.SettingsOverride = current => configure!(current) with
        {
            DefaultModel = "shared-model",
            Providers =
            [
                new() { Name = "test", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://example.test/v1", Models = [new("shared-model", SupportsTools: firstSupportsTools)] },
                new() { Name = "healthy", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://example.test/v1", Models = [new("shared-model", SupportsTools: healthySupportsTools)] },
            ]
        };

        IProviderHealthTracker health = new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance);

        for (int count = 0; count < 10; count++) health.MarkFailed("test");

        int reads = 0;

        var original = factory.ServiceOverrides;

        factory.ServiceOverrides = services =>
        {
            original!(services);

            services.RemoveAll<IProviderHealthTracker>();

            services.AddSingleton(health);

            services.RemoveAll<IMemoryDelegationSourceReader>();

            services.AddSingleton<IMemoryDelegationSourceReader>(new RefusingSources(() => reads++));
        };

        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/intelligence/memory-delegation", JsonContent.Create(
            new MemoryDelegationRequest(new("parent", DisableMcpTools: true), Grant()), ArcanumJsonContext.Default.MemoryDelegationRequest));

        Assert.Equal(expected, response.StatusCode);

        Assert.Equal(expectedReads, reads);

        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unused_expiry_allows_plain_response_but_consumed_expiry_refuses_plaintext(bool consumed)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        if (consumed)
        {
            await using var child = MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest));
        }

        var (http, scope) = ResponseScope(h, root);

        h.Time.Advance(TimeSpan.FromMinutes(5));

        http.Response.Body = new MemoryStream();

        http.Response.Headers.ETag = "must-strip";

        var result = new MemoryDelegationJsonResult(scope, new PromptResponseDto("private-output", null));

        await result.ExecuteAsync(http);

        Assert.Equal(consumed ? 409 : 200, http.Response.StatusCode);

        string json = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());

        Assert.Equal(!consumed, json.Contains("private-output", StringComparison.Ordinal));

        Assert.Equal("no-store, private", http.Response.Headers.CacheControl.ToString());

        Assert.Equal(0, http.Response.Headers.ETag.Count);

        Assert.True((await h.Source.Gate.ValidateTurnLeaseAsync(h.Source.Lease, default)).IsFailure);

        await Assert.ThrowsAsync<InvalidOperationException>(() => result.ExecuteAsync(http));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entire_serialization_effect_cancels_and_drains_before_mutation_completes(bool writePrefix)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        await using (var child = MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest))) { }

        var (http, scope) = ResponseScope(h, root);

        await using var body = new SerializationBarrier(writePrefix);

        http.Response.Body = body;

        var writing = new MemoryDelegationJsonResult(scope, new PromptResponseDto("private-output", null)).ExecuteAsync(http);

        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var mutation = h.Coordinator.BeginMutationAsync().AsTask();

        try
        {
            await body.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(mutation.IsCompleted);

            Assert.False(writing.IsCompleted);

            Assert.Equal(writePrefix, body.Length > 0);
        }
        finally
        {
            body.Release.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(TimeSpan.FromSeconds(10)));

        await using var ownership = await mutation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True((await h.Source.Gate.ValidateTurnLeaseAsync(h.Source.Lease, default)).IsFailure);
    }

    private static (DefaultHttpContext Http, MemoryDelegationRequestScope Scope) ResponseScope(MemoryDelegationLifecycleTests.Harness h, MemoryDelegationRoot root)
    {
        var issuer = new OperatorAuthorityContextIssuer(new RequestAuthorityProvider());

        var http = new DefaultHttpContext();

        http.RequestServices = new ServiceCollection().AddSingleton<IOperatorAuthorityContextIssuer>(issuer).BuildServiceProvider();

        var authority = issuer.Issue(CovenantAuthorityRequirement.ProtectedRead).Value;

        var feature = new CovenantAuthorityFeature(authority, authority.Requirement, authority.AuthorityEpoch);

        CovenantRequestFeatures.RecordAuthority(http, feature);

        var invocation = ArcanumInvocationContexts.ForTurn(http, new PingRequest("parent"), h.Source.Invocation.Campaign);

        return (http, new(http, feature, issuer, h.Source.Gate, invocation, h.Source.Lease, root, h.Coordinator));
    }

    private sealed class SerializationBarrier(bool writePrefix) : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (writePrefix) await base.WriteAsync(buffer[..Math.Min(16, buffer.Length)], cancellationToken);

            Entered.TrySetResult();

            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();

                await Release.Task;

                throw;
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Real_wrapper_preserves_canonical_parent_and_does_not_replay_or_release_before_response(bool sessionBacked, bool disableMcp)
    {
        await using var factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true,
            configure: settings => settings.Features.Annals = true);

        ObservedSources? observed = null;

        var original = factory.ServiceOverrides;

        factory.ServiceOverrides = services =>
        {
            original!(services);

            services.RemoveAll<IMemoryDelegationSourceReader>();

            services.AddScoped<IMemoryDelegationSourceReader>(sp => observed = new(ActivatorUtilities.CreateInstance<MemoryDelegationSourceReader>(sp)));
        };

        using var client = factory.CreateAuthenticatedClient();

        Guid? session = null;

        if (sessionBacked)
        {
            using var creation = factory.Services.CreateScope();

            session = MemoryEvaluationSourceHarness.Require(await creation.ServiceProvider.GetRequiredService<ISessionTurnBeginStore>()
                .CreateBoundSessionAsync(CanonicalCampaignContext.GlobalOnly, "capsule parent", default));
        }

        string id = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "selected fact");

        using var sourceScope = factory.Services.CreateScope();

        var connection = await sourceScope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenCoreConnectionAsync(default);

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT v.VersionId, v.ContentHash FROM annal_claims c JOIN annal_heads h ON h.ClaimId=c.ClaimId JOIN annal_versions v ON v.VersionId=h.CurrentVersionId WHERE c.SubjectStoreCode=1 AND c.SubjectId=$id";

        command.Parameters.AddWithValue("$id", id);

        await using var rows = await command.ExecuteReaderAsync();

        Assert.True(await rows.ReadAsync());

        MemoryDelegationGrant grant = new(new(MemoryEvaluationScopeKind.Global), new([], [new(id, rows.GetString(0), AnnalContentHashFormat.LegacyStoreDigest, new((byte[])rows[1]))], []));

        await rows.DisposeAsync();

        var parent = new PingRequest("parent", SessionId: session, DisableMcpTools: disableMcp, SkipSpellRouting: true,
            StatelessMessages: sessionBacked ? null : [new("user", "parent")]);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/intelligence/memory-delegation")
            {
                Content = JsonContent.Create(new MemoryDelegationRequest(parent, grant), ArcanumJsonContext.Default.MemoryDelegationRequest)
            };

            request.Headers.Add(ArcanumApiHeaders.IdempotencyKey, "capsule-no-replay");

            using var response = await client.SendAsync(request);

            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

            Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());

            Assert.Equal(attempt + 1, factory.FakeIntelligence.ExecutePromptCallCount);

            Assert.Same(observed!.Parent, factory.FakeIntelligence.LastInvocation);

            Assert.Equal(sessionBacked ? ArcanumExecutionSurface.SessionBackedOperatorTurn : ArcanumExecutionSurface.StatelessOperatorTurn, observed.Parent!.Surface);

            Assert.True(observed.Parent.CanReadCovenant);

            Assert.Equal(disableMcp ? ToolPolicy.NoTools : ToolPolicy.AllTools, observed.Parent.ToolPolicy);

            Assert.True((await sourceScope.ServiceProvider.GetRequiredService<ICovenantOperationGate>().ValidateTurnLeaseAsync(observed.Lease!, default)).IsFailure);
        }
    }

    [Theory]
    [InlineData("no-context", HttpStatusCode.Forbidden)]
    [InlineData("no-tools", HttpStatusCode.Forbidden)]
    [InlineData("all-disabled", HttpStatusCode.Forbidden)]
    [InlineData("forwarded", HttpStatusCode.ServiceUnavailable)]
    [InlineData("missing-runner", HttpStatusCode.ServiceUnavailable)]
    [InlineData("unsupported-model", HttpStatusCode.ServiceUnavailable)]
    [InlineData("invalid-file", HttpStatusCode.BadRequest)]
    [InlineData("scope-mismatch", HttpStatusCode.BadRequest)]
    public async Task Invalid_or_unavailable_parent_refuses_before_source_hydration(string mode, HttpStatusCode status)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        if (mode is "unsupported-model" or "forwarded")
        {
            var settings = factory.SettingsOverride;

            factory.SettingsOverride = current =>
            {
                var configured = settings!(current);

                return mode == "forwarded" ? configured with { Features = configured.Features with { ClientTools = true } }
                    : configured with
                    {
                        Providers = configured.Providers.Select(provider => provider with
                        {
                            Models = provider.Models.Select(model => model with { SupportsTools = false }).ToArray()
                        }).ToArray()
                    };
            };
        }

        int reads = 0;

        var original = factory.ServiceOverrides;

        factory.ServiceOverrides = services =>
        {
            original!(services);

            services.RemoveAll<IMemoryDelegationSourceReader>();

            services.AddSingleton<IMemoryDelegationSourceReader>(new RefusingSources(() => reads++));

            if (mode == "missing-runner") services.RemoveAll<ISubagentRunner>();
        };

        using var client = factory.CreateAuthenticatedClient();

        if (mode == "no-context") client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ContextPolicy, "none");

        var parent = new PingRequest("parent", ToolPolicy: mode == "no-tools" ? ToolPolicy.NoTools : null,
            DisableAllTools: mode == "all-disabled", ForwardClientTools: mode == "forwarded",
            AttachedFiles: mode == "invalid-file" ? [new("", "content")] : null);

        var grant = Grant();

        if (mode == "scope-mismatch") grant = grant with { Scope = new(MemoryEvaluationScopeKind.Campaign, Guid.NewGuid()) };

        using var response = await client.PostAsync("/api/intelligence/memory-delegation", JsonContent.Create(new MemoryDelegationRequest(parent, grant), ArcanumJsonContext.Default.MemoryDelegationRequest));

        Assert.Equal(status, response.StatusCode);

        Assert.Equal(0, reads);

        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);
    }

    [Theory]
    [InlineData("grant", "authority")]
    [InlineData("scope", "epoch")]
    [InlineData("memory", "capability")]
    [InlineData("target", "snapshot")]
    [InlineData("digest", "unexpected")]
    public async Task Unknown_grant_members_refuse_without_echoing_protected_input(string container, string member)
    {
        string json = JsonSerializer.Serialize(new MemoryDelegationRequest(new("parent"), Grant()), ArcanumJsonContext.Default.MemoryDelegationRequest);

        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;

        var grant = node["grant"]!;

        var target = container switch
        {
            "scope" => grant["scope"]!,
            "memory" => grant["memory"]!,
            "target" => grant["memory"]!["saga"]![0]!,
            "digest" => grant["memory"]!["saga"]![0]!["contentDigest"]!,
            _ => grant,
        };

        target[member] = "private-sentinel";

        var http = new DefaultHttpContext();

        http.Request.ContentType = "application/json";

        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(node.ToJsonString()));

        var read = await MemoryDelegationRequestJson.ReadAsync(http);

        Assert.NotNull(read.Error);

        http.Response.Body = new MemoryStream();

        await read.Error.ExecuteAsync(http);

        Assert.Equal(400, http.Response.StatusCode);

        Assert.DoesNotContain("private-sentinel", Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray()));
    }

    private static MemoryDelegationGrant Grant() => new(new(MemoryEvaluationScopeKind.Global),
        new([], [new("source", "version", AnnalContentHashFormat.LegacyStoreDigest, new(new byte[32]))], []));

    private sealed class RefusingSources(Action read) : IMemoryDelegationSourceReader
    {
        public Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryDelegationGrant grant, ArcanumInvocationContext parent, CovenantTurnLease lease, CancellationToken ct)
        {
            read();

            return Task.FromResult(Result<MemoryEvaluationSourceSnapshot>.Failure(new Error(ErrorCodes.MemoryDelegation.StaleSource, "Unavailable.")));
        }

        public Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct) => Task.FromResult(Result.Success());
    }

    private sealed class ObservedSources(IMemoryDelegationSourceReader inner) : IMemoryDelegationSourceReader
    {
        internal ArcanumInvocationContext? Parent;

        internal CovenantTurnLease? Lease;

        public Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryDelegationGrant grant, ArcanumInvocationContext parent, CovenantTurnLease lease, CancellationToken ct)
        {
            Parent = parent;

            Lease = lease;

            return inner.ReadAsync(grant, parent, lease, ct);
        }

        public Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct) => inner.ValidateAsync(snapshot, lease, ct);
    }

    private sealed class RequestAuthorityProvider : ICovenantAuthoritySnapshotProvider
    {
        public CovenantAuthoritySnapshot? Current { get; private set; } = new FakeCovenantAuthorityProvider().Current;

        internal void RotateMasterKey() => Current = Current! with { MasterKeyVersion = Current.MasterKeyVersion + 1 };

        internal void MakeUnavailable(bool tainted) => Current = tainted ? Current! with { HostToolsState = CovenantHostToolsState.HostToolsTainted } : null;
    }

    [SkippableTheory]
    [InlineData("missing")]
    [InlineData("swapped")]
    [InlineData("rotated")]
    public async Task Scope_rechecks_original_authority_not_only_borrowed_lease(string change)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        var authority = new RequestAuthorityProvider();

        var issuer = new OperatorAuthorityContextIssuer(authority);

        var http = new DefaultHttpContext();

        http.RequestServices = new ServiceCollection().AddSingleton<IOperatorAuthorityContextIssuer>(issuer).BuildServiceProvider();

        var original = issuer.Issue(CovenantAuthorityRequirement.ProtectedRead).Value;

        var feature = new CovenantAuthorityFeature(original, original.Requirement, original.AuthorityEpoch);

        CovenantRequestFeatures.RecordAuthority(http, feature);

        var invocation = ArcanumInvocationContexts.ForTurn(http, new PingRequest("parent"), h.Source.Invocation.Campaign);

        await using var scope = new MemoryDelegationRequestScope(http, feature, issuer, h.Source.Gate, invocation,
            h.Source.Lease, root, h.Coordinator);

        Assert.True((await scope.RevalidateAsync(default)).IsSuccess);

        object key = http.Items.Single(pair => ReferenceEquals(pair.Value, feature)).Key;

        if (change == "rotated") authority.RotateMasterKey();
        else if (change == "swapped") http.Items[key] = new CovenantAuthorityFeature(original, original.Requirement, original.AuthorityEpoch);
        else http.Items.Remove(key);

        Assert.True((await h.Source.Gate.ValidateTurnLeaseAsync(h.Source.Lease, default)).IsSuccess);

        Assert.True((await scope.RevalidateAsync(default)).IsFailure);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(true, HttpStatusCode.BadRequest)]
    public async Task Refusals_are_protected_and_authentication_precedes_binding(bool authenticated, HttpStatusCode status)
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = authenticated ? factory.CreateAuthenticatedClient() : factory.CreateClient();

        using var response = await client.PostAsync("/api/intelligence/memory-delegation",
            new StringContent("{private-sentinel", Encoding.UTF8, "application/json"));

        Assert.Equal(status, response.StatusCode);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());

        Assert.DoesNotContain("private-sentinel", await response.Content.ReadAsStringAsync());
    }
}
