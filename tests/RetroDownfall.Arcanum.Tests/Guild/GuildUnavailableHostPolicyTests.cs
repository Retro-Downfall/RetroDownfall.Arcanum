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
    public async Task Disabled_composition_refuses_unbound_authority_without_initializing_identity()
    {
        ServiceCollection services = new();

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        await using ServiceProvider provider = services.BuildServiceProvider();

        IGuildSignetHostPolicy? policy = provider.GetService<IGuildSignetHostPolicy>();

        Assert.NotNull(policy);

        Assert.IsType<GuildLocalHostPolicy>(policy);

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
            static error => Assert.Equal(ErrorCodes.Guild.Forbidden, error.Code));

        Assert.Equal(Guid.Empty, policy.InstallationGeneration);

        Assert.Same(provider.GetRequiredService<GuildLocalHostPolicy>(), policy);

        Assert.Same(provider.GetRequiredService<SignetAuthority>(), provider.GetRequiredService<ISignetAuthority>());

        Assert.False(provider.GetRequiredService<IGuildLocalHostReadiness>().RequireExecutionReady().IsSuccess);
    }
}
