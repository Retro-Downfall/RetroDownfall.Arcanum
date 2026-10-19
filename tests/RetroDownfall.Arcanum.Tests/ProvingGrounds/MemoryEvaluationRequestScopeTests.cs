using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class MemoryEvaluationRequestScopeTests
{
    [Fact]
    public async Task Genuine_request_scope_has_read_only_stateless_turn_and_retains_lease_until_disposal()
    {
        var authority = new FakeCovenantAuthorityProvider();

        var gate = CovenantOperationGateFixture.CreateGate(authority: authority);

        var issuer = new OperatorAuthorityContextIssuer(authority);

        using var services = new ServiceCollection().AddSingleton<IOperatorAuthorityContextIssuer>(issuer).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };

        var response = new ResponseFeature();

        http.Features.Set<IHttpResponseFeature>(response);

        var original = issuer.Issue(CovenantAuthorityRequirement.ProtectedRead).Value;

        CovenantRequestFeatures.RecordAuthority(http, new(original, CovenantAuthorityRequirement.ProtectedRead, original.AuthorityEpoch));

        var resolver = new Resolver();

        var opened = await new MemoryEvaluationRequestScopeFactory(resolver, gate, issuer).OpenAsync(http, new(MemoryEvaluationScopeKind.Global), default);

        Assert.True(opened.IsSuccess);

        var scope = opened.Value;

        Assert.True(scope.Invocation.CanReadCovenant);

        Assert.False(scope.Invocation.CanStageCovenantMutation);

        Assert.False(scope.Invocation.CanPrepareCovenantRetirement);

        Assert.Equal(ArcanumExecutionSurface.StatelessOperatorTurn, scope.Invocation.Surface);

        Assert.Equal(ToolPolicy.NoTools, scope.Invocation.ToolPolicy);

        Assert.Null(resolver.Seen!.SessionId);

        Assert.Null(resolver.Seen.WorkingDirectory);

        Assert.True((await gate.ValidateTurnLeaseAsync(scope.Lease, default)).IsSuccess);

        Assert.True((await scope.RevalidateAsync(default)).IsSuccess);

        Assert.Single(response.Completions);

        await response.Complete();

        Assert.True((await gate.ValidateTurnLeaseAsync(scope.Lease, default)).IsFailure);

        await scope.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_wrong_original_protected_read_feature_cannot_hydrate(bool wrong)
    {
        var authority = new FakeCovenantAuthorityProvider();

        var gate = CovenantOperationGateFixture.CreateGate(authority: authority);

        var issuer = new OperatorAuthorityContextIssuer(authority);

        var http = new DefaultHttpContext();

        if (wrong)
        {
            var original = issuer.Issue(CovenantAuthorityRequirement.CovenantManage).Value;

            CovenantRequestFeatures.RecordAuthority(http, new(original, original.Requirement, original.AuthorityEpoch));
        }

        var resolver = new Resolver();

        var opened = await new MemoryEvaluationRequestScopeFactory(resolver, gate, issuer).OpenAsync(http, new(MemoryEvaluationScopeKind.Global), default);

        Assert.True(opened.IsFailure);

        Assert.Null(resolver.Seen);
    }

    [Fact]
    public async Task Authority_rotation_during_canonical_resolution_cannot_be_adopted()
    {
        var authority = new FakeCovenantAuthorityProvider();

        var gate = CovenantOperationGateFixture.CreateGate(authority: authority);

        var issuer = new OperatorAuthorityContextIssuer(authority);

        var http = new DefaultHttpContext();

        var original = issuer.Issue(CovenantAuthorityRequirement.ProtectedRead).Value;

        CovenantRequestFeatures.RecordAuthority(http, new(original, original.Requirement, original.AuthorityEpoch));

        var opened = await new MemoryEvaluationRequestScopeFactory(new Resolver(authority.Advance), gate, issuer).OpenAsync(http, new(MemoryEvaluationScopeKind.Global), default);

        Assert.True(opened.IsFailure);
    }

    private sealed class ResponseFeature : HttpResponseFeature
    {
        internal List<(Func<object, Task> Callback, object State)> Completions = [];

        public override void OnCompleted(Func<object, Task> callback, object state) => Completions.Add((callback, state));

        internal async Task Complete() { foreach (var item in Completions) await item.Callback(item.State); }
    }

    private sealed class Resolver(Action? beforeReturn = null) : ICanonicalCampaignContextResolver
    {
        public CanonicalCampaignResolutionRequest? Seen;

        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken ct)
        { Seen = request; beforeReturn?.Invoke(); return ValueTask.FromResult(Result<CanonicalCampaignContext>.Success(CanonicalCampaignContext.GlobalOnly)); }
    }
}
