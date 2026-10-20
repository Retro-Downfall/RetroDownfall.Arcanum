using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Security;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("ProcessEnvironment")]
public sealed class MemoryDelegationCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_product_compositions_use_one_barrier_for_every_source_writer(bool host)
    {
        using ArcanumTestHomeScope home = new("memory-delegation-composition");

        IServiceCollection services = Host.CreateApplicationBuilder().Services;

        if (host) services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());
        else
        {
            services.AddArcanumSecretStore();

            services.AddArcanumGrimoireForCli();
        }

        services.AddSingleton<IWeaveService>(static _ => null!);

        services.AddSingleton<ISecretStore>(new TestApiKeySecretStore("memory-delegation-composition"));

        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<GrimoireDbPassphraseSource>(provider.GetRequiredService<IGrimoireDbPassphraseSource>()).SetPassphrase("memory-delegation-composition");

        var coordinator = provider.GetRequiredService<MemoryDelegationCoordinator>();

        Assert.Same(coordinator, provider.GetRequiredService<IMemoryDelegationMutationBarrier>());

        Assert.Same(coordinator, provider.GetRequiredService<GrimoireConnectionAdmissionGate>().MemoryDelegation);

        await using var scope = provider.CreateAsyncScope();

        var repository = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

        AssertBarrier(repository, coordinator);

        if (!host) return;

        string[] names = ["ISagaMemoryStore", "ILexiconService", "ISagaMemoryReviewService", "ISagaMemoryErasureService",
            "ICovenantMutationService", "ICovenantMemoryReviewService", "ICovenantEntryErasureService", "ILongRestService",
            "ILongRestSweepStore", "ICampaignRepository", "IDataRetentionService", "EmbeddingsResetService",
            "ICovenantSensitiveArtifactPurger", "ICovenantProtectedArtifactErasureKernel", "CovenantOwnerCleanupCoordinator"];

        foreach (string name in names)
        {
            Type service = Assert.Single(services.Select(descriptor => descriptor.ServiceType).Distinct(), type => type.Name == name);

            object writer = scope.ServiceProvider.GetRequiredService(service);

            AssertBarrier(writer, coordinator);
        }
    }

    private static void AssertBarrier(object writer, MemoryDelegationCoordinator coordinator)
    {
        FieldInfo field = Assert.Single(writer.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IMemoryDelegationMutationBarrier));

        Assert.Same(coordinator, field.GetValue(writer));
    }
}
