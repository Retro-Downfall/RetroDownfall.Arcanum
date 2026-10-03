using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The composition registers one labelled-artifact guard behind two interfaces, and which instance a
/// caller gets depends on the scope it is resolved in.
/// </summary>
/// <remarks>
/// The Core interface is what a caller outside the database layer sees; the transaction forms live on an
/// Infrastructure interface that extends it, because they name an ADO.NET transaction and Core names no
/// storage type. Nothing about that split may change what a consumer gets: <c>LexiconService</c> is built
/// by type with the Core interface as an optional parameter, so a registration that stopped forwarding
/// would hand it null and its delete would skip the label refusal in silence, with every unit test that
/// builds the service by hand still green. This resolves both interfaces from the real container, which
/// is the only place the forwarder exists.
///
/// <para>The guard is scoped because it reads through the scope's own ledger. One instance per scope is
/// what makes the two interfaces one guard; a singleton would hand every request the ledger of whichever
/// scope built it first.</para>
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class CovenantLabeledArtifactGuardCompositionTests
{
    [Fact]
    public async Task Both_guard_interfaces_resolve_to_one_instance_within_a_scope()
    {
        await using ServiceProvider provider = BuildProvider();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        ICovenantLabeledArtifactGuard core = scope.ServiceProvider
            .GetRequiredService<ICovenantLabeledArtifactGuard>();

        ICovenantLabeledArtifactTransactionGuard transaction = scope.ServiceProvider
            .GetRequiredService<ICovenantLabeledArtifactTransactionGuard>();

        Assert.Same(transaction, core);

        Assert.IsType<CovenantLabeledArtifactGuard>(core);

        // Asked again in the same scope, so a registration that built a fresh guard per resolution
        // cannot pass by happening to agree once.
        Assert.Same(
            core,
            scope.ServiceProvider.GetRequiredService<ICovenantLabeledArtifactGuard>());

        Assert.Same(
            transaction,
            scope.ServiceProvider.GetRequiredService<ICovenantLabeledArtifactTransactionGuard>());
    }

    [Fact]
    public async Task The_guard_is_one_instance_per_scope_and_never_shared_across_scopes()
    {
        await using ServiceProvider provider = BuildProvider();

        await using AsyncServiceScope first = provider.CreateAsyncScope();

        await using AsyncServiceScope second = provider.CreateAsyncScope();

        ICovenantLabeledArtifactGuard firstCore = first.ServiceProvider
            .GetRequiredService<ICovenantLabeledArtifactGuard>();

        ICovenantLabeledArtifactGuard secondCore = second.ServiceProvider
            .GetRequiredService<ICovenantLabeledArtifactGuard>();

        ICovenantLabeledArtifactTransactionGuard firstTransaction = first.ServiceProvider
            .GetRequiredService<ICovenantLabeledArtifactTransactionGuard>();

        ICovenantLabeledArtifactTransactionGuard secondTransaction = second.ServiceProvider
            .GetRequiredService<ICovenantLabeledArtifactTransactionGuard>();

        Assert.NotSame(firstCore, secondCore);

        Assert.NotSame(firstTransaction, secondTransaction);

        Assert.Same(firstTransaction, firstCore);

        Assert.Same(secondTransaction, secondCore);
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        ServiceProvider provider = services.BuildServiceProvider();

        // The scope's ledger sits on the database context, whose options read the passphrase source when
        // they are built. Nothing here opens the database.
        Assert.IsType<GrimoireDbPassphraseSource>(
                provider.GetRequiredService<IGrimoireDbPassphraseSource>())
            .SetPassphrase("label-guard-composition-passphrase");

        return provider;
    }
}
