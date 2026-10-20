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
                    : configured with { Providers = configured.Providers.Select(provider => provider with
                    {
                        Models = provider.Models.Select(model => model with { SupportsTools = false }).ToArray()
                    }).ToArray() };
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
