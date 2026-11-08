using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildUnavailableHostPolicyTests
{
    [Fact]
    public async Task Dormant_composition_resolves_a_policy_that_refuses_every_authority_operation()
    {
        ServiceCollection services = new();

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        await using ServiceProvider provider = services.BuildServiceProvider();

        IGuildSignetHostPolicy? policy = provider.GetService<IGuildSignetHostPolicy>();

        Assert.NotNull(policy);

        Assert.Equal(Guid.Empty, policy.InstallationGeneration);

        Result<GuildSignetAdmissionEvidence> admission = await policy.AuthorizeAdmissionAsync(null!, null!, default);

        Result<GuildSignetPolicyEvidence> current = await policy.ResolveCurrentAsync([], default);

        Result revocation = await policy.AuthorizeRevocationAsync(null!, null!, default);

        Result recovery = await policy.AuthorizeRecoveryAsync(null!, null!, default);

        Assert.True(admission.IsFailure);

        Assert.True(current.IsFailure);

        Assert.True(revocation.IsFailure);

        Assert.True(recovery.IsFailure);

        Assert.All(new[] { admission.Error, current.Error, revocation.Error, recovery.Error },
            static error => Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, error.Code));

        Assert.DoesNotContain(services, static descriptor => descriptor.ServiceType == typeof(SignetAuthority)
            || descriptor.ServiceType == typeof(ISignetAuthority) || descriptor.ServiceType == typeof(IQuestStore)
            || descriptor.ServiceType == typeof(IGuildQuestCoordinator));
    }
}
