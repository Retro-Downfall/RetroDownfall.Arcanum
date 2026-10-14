using System.Reflection;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The four Long Rest ports of <see cref="CampaignRollupTurnPreparer"/> are required. A turn never discovers at preparation time
/// that Long Rest has no coordinator, so no fail-closed branch for an absent port exists; what remains is the fail-closed answer
/// to a configuration that is no longer enabled.
/// </summary>
public sealed class LongRestRequiredPortTests
{
    [Theory]
    [InlineData("longRest", typeof(ILongRestSweepCoordinator))]
    [InlineData("longRestMaintenance", typeof(ILongRestMaintenance))]
    [InlineData("longRestDiscovery", typeof(ILongRestDiscoveryStore))]
    [InlineData("longRestConfiguration", typeof(ILongRestConfigurationProvider))]
    public void The_Long_Rest_ports_are_required_non_nullable_constructor_parameters(string name, Type port)
    {
        ConstructorInfo constructor = Assert.Single(typeof(CampaignRollupTurnPreparer).GetConstructors());

        ParameterInfo parameter = Assert.Single(constructor.GetParameters(), candidate => candidate.Name == name);

        Assert.Equal(port, parameter.ParameterType);

        Assert.False(parameter.IsOptional);

        Assert.False(parameter.HasDefaultValue);

        Assert.Equal(NullabilityState.NotNull, new NullabilityInfoContext().Create(parameter).WriteState);

        // They are declared before every optional parameter, which is what makes them required.
        ParameterInfo[] all = constructor.GetParameters();

        Assert.True(Array.FindIndex(all, static p => p.IsOptional) > parameter.Position);
    }

    [Fact]
    public async Task A_Long_Rest_configuration_that_is_no_longer_enabled_terminalizes_the_claim_without_requesting_maintenance()
    {
        ArcanumSettings settings = new();

        settings.Features.LongRest = true;

        TestOptionsMonitor<ArcanumSettings> options = new(settings);

        CampaignRollupTurnSnapshotTests.ProbeStore store = new();

        CampaignRollupTurnSnapshotTests.AcceptingClaims claims = new();

        // The feature is on in settings, but the epoch the turn captures reports Long Rest disabled: the turn fails closed before
        // any maintenance is requested, and the claim it acquired is terminalized rather than leaked.
        CampaignRollupTurnPreparer preparer = new(store, options,
            new CampaignRollupTurnSnapshotTests.Resolver(), null!,
            new UnreachedCoordinator(), UnreachedLongRestPorts.Maintenance, UnreachedLongRestPorts.Discovery, UnreachedLongRestPorts.Configuration,
            claimBegin: new CampaignRollupTurnSnapshotTests.InputStore(), claims: claims,
            authoritySnapshot: new CampaignRollupTurnSnapshotTests.Authority());

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn, CampaignRollupTurnSnapshotTests.Campaign,
            InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools, null).Value;

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", SessionId: Guid.NewGuid()), invocation, false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);

        Assert.Equal(0, store.Reads);

        Assert.Equal(1, claims.Completions);
    }

    private sealed class UnreachedCoordinator : ILongRestSweepCoordinator
    {
        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, CancellationToken ct) =>
            throw new InvalidOperationException("A disabled Long Rest configuration must refuse before maintenance dispatch.");

        public Task<Result<LongRunningOperationDto>> RequestAsync(
            CanonicalCampaignContext scope, LongRestSweepScope partition, CancellationToken ct) =>
            throw new InvalidOperationException("A disabled Long Rest configuration must refuse before partition dispatch.");

        public Task<Result<LongRestSweepProgress>> ResumeAsync(
            Guid operationId, LongRestMaintenanceCapability? capability, CancellationToken ct) =>
            throw new InvalidOperationException("A disabled Long Rest configuration must refuse before provider work.");
    }
}
