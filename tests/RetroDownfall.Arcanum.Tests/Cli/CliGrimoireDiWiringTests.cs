using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Guards the CLI Grimoire stack against drifting from host wiring when
/// <see cref="IGrimoireRepository"/> gains new constructor dependencies (e.g. session attachments).
/// </summary>
public sealed class CliGrimoireDiWiringTests
{

    [Fact]
    public void ConfigureCliServices_registers_session_attachment_store_required_by_grimoire_repository()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        Assert.Contains(services, static d => d.ServiceType == typeof(ISessionAttachmentStore));

        Assert.Contains(services, static d => d.ServiceType == typeof(IGrimoireRepository));

    }


    /// <summary>
    /// <c>arcanum run "&lt;prompt&gt;"</c> resolves <see cref="IChronosyncEngine"/> from the real CLI
    /// container, so every transitive dependency of that graph must be registered there. Asserting
    /// on registrations alone missed a dependency (<c>IHostWorkspaceContext</c>) that only the
    /// shipped binary hit, so this constructs the graph instead of inspecting the descriptors.
    /// </summary>
    [Fact]
    public void ConfigureCliServices_can_construct_the_chronosync_graph_used_by_run()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        provider
            .GetRequiredService<IGrimoireDbPassphraseSource>()
            .SetPassphrase("cli-di-wiring-test");

        using IServiceScope scope = provider.CreateScope();

        IChronosyncEngine chronosync = scope.ServiceProvider
            .GetRequiredService<IChronosyncEngine>();

        Assert.NotNull(chronosync);

    }

}
